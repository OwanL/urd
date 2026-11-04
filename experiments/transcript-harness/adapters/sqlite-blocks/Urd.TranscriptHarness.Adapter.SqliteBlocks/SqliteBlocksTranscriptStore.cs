using System.Globalization;
using Microsoft.Data.Sqlite;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.SqliteBlocks;

// Family-2 adapter: SHARED SQLite, content blocks (per STORAGE-EXPERIMENT.md).
// One shared database; message rows carry headers only, and every part's raw
// TEXT content lives in a separately addressable `blocks` row referenced by
// the message's part row. Raw text + JSON-ish metadata only — no binary
// encoding variant is introduced; part rows are plain columns.
//
// Layout record (declared):
//   sessions     — session-existence rows (created on first message insert).
//   messages     — header rows only: (session_id, ordinal) PK + role. No content.
//   parts        — one row per part: kind, tool-call/id metadata, block
//                  reference, full-content character length, and a bounded
//                  stored preview prefix. Part rows are the message-to-block
//                  reference table.
//   blocks       — separately addressable raw TEXT content rows (`block_id`
//                  PK, `content`). block_id is the opaque unique string
//                  "blk:" + sessionId + ":" + ordinal + ":" + partIndex.
//   streams      — active draft streams; every StreamDraft call COMMITS one
//                  appended batch in its own transaction (no deferred-to-
//                  finalize buffering). Drafts never appear in any read window.
//   assignments  — bounded assignment/result records.
//
// Block-reference invariants (explicit, all maintained inside mutations):
//   - every part row references exactly one block row; the blocks table holds
//     no inline/small-part special case, so reference ownership is trivial
//     (one-to-one) and countable by construction;
//   - blocks are created strictly in the same committed transaction as the
//     part rows that reference them, so no committed block is ever
//     unreferenced after any acknowledged mutation;
//   - orphan cleanup is explicit and synchronous: any mutation that removes
//     part rows first deletes exactly the block rows those removed parts
//     referenced (subselect on the parts primary key), inside the SAME
//     transaction. No background GC, no reclaim timer.
//
// Bounded reads: OpenRecentPreview touches ONLY message headers + part rows —
// every preview was computed at write time (C#-exact leading prefix, cap 256
// UTF-16 code units stored). previewChars beyond the stored cap falls back to
// reading the single referenced block of that one part (documented bound; the
// harness defaults to ≤ 24 chars so the metadata path covers it).
// FetchToolResult is a targeted two-row read: one part row + its block.
//
// Edit mechanics: ReplaceSuffix = one committed transaction — remove the
// suffix's blocks (via its parts), the parts, and the message headers, then
// reinsert the replacement densely from the edit point (delete+insert; a
// shorter replacement truncates). DeleteSession = one transaction covering
// assignment-invariant handling, messages, parts, blocks, drafts and the
// session row.
//
// Acknowledgement boundary (declared): every mutation runs one transaction and
// COMMITs before the call returns; journal_mode=WAL + synchronous=NORMAL hands
// committed data to the OS without an fsync — the OS-flush ack tier. NO
// power-loss or fsync-durability claim; SQLite WAL gives process-crash
// recovery: acknowledged (committed) records replay identically; an
// unacknowledged mid-transaction mutation persists as before-state or fully
// committed after-state; recovery never invents or reorders records.

public sealed class SqliteBlocksTranscriptStore : ITranscriptStore, IStoreStatistics
{
    /// <summary>
    /// Leading-prefix character cap stored in part rows' preview column.
    /// Preview requests with previewChars &le; this cap are answered purely
    /// from part metadata; larger requests fall back to reading the single
    /// referenced block (documented; bounded-metadata path is the default).
    /// </summary>
    internal const int PreviewStoredPrefix = 256;

    private SqliteConnection? _connection;

    public StoreDescriptor Describe() => new(
        "SqliteBlocksStore",
        AckTier.OsFlush,
        "journal_mode=WAL; synchronous=NORMAL; wal_autocheckpoint=1000 (SQLite default); " +
            "every mutation is one transaction COMMIT-ed before the call returns",
        "Family 2 shared-SQLite content-block layout (Microsoft.Data.Sqlite 10.0.14, shared single database). " +
            "Message rows are headers only; every part's raw TEXT content is a separately addressable blocks row " +
            "referenced by its part row (one-to-one; uniform, no inline special case). Explicit orphan cleanup: " +
            "removed parts take their blocks with them inside the same transaction. Preview metadata is derived " +
            "at write time with a bounded stored prefix (default path never reads block content). " +
            "StreamDraft = one committed persisted batch per call; drafts survive reopen and never serve partial content. " +
            "Edit = atomic suffix block/part/message delete + reinsert in one transaction. " +
            "Recovery: SQLite WAL/rollback; acknowledged records replay identically; no power-loss claim.");

    public void Open(string storeRoot, string attachmentRoot)
    {
        // attachmentRoot is harness-owned; this store persists only stable
        // image reference records and never touches attachment files.
        NativeDependency.EnsureResolved();
        Directory.CreateDirectory(storeRoot);
        _connection?.Dispose();
        _connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(storeRoot, "transcript.db") }.ToString());
        _connection.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec("PRAGMA foreign_keys=OFF"); // single-file store; reference moves are explicit SQL, not FKs
        Exec(SchemaSql);
    }

    // ---- reads ----

    public SessionMeta[] ListSessionsMeta()
    {
        // Session rows are the existence record; counts derive from the
        // message table so they cannot drift. Zero-message sessions stay
        // listed (reference-store parity). Ordering left to the database;
        // the oracle sorts both sides before comparing.
        using var command = Command(
            "SELECT s.session_id, COUNT(m.ordinal) " +
            "FROM sessions s LEFT JOIN messages m ON m.session_id = s.session_id " +
            "GROUP BY s.session_id ORDER BY s.session_id");
        using var reader = command.ExecuteReader();
        var metas = new List<SessionMeta>();
        while (reader.Read())
        {
            metas.Add(new SessionMeta(reader.GetString(0), reader.GetInt64(1)));
        }
        return [.. metas];
    }

    public MessageRecord[] OpenRecent(string sessionId, int count)
    {
        if (count <= 0) return [];
        return ReadMessages(
            "SELECT m.ordinal, m.role, p.kind, p.tool_call_id, p.tool_name, p.image_id, b.content " +
            "FROM (" +
            "  SELECT ordinal FROM messages WHERE session_id = @s ORDER BY ordinal DESC LIMIT @n" +
            ") win " +
            "JOIN messages m ON m.session_id = @s AND m.ordinal = win.ordinal " +
            "JOIN parts p ON p.session_id = @s AND p.ordinal = m.ordinal " +
            "JOIN blocks b ON b.block_id = p.block_id " +
            "ORDER BY m.ordinal, p.part_index",
            new Dictionary<string, object> { ["@s"] = sessionId, ["@n"] = (long)count });
    }

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count)
    {
        // Reference-parity clamping: to = clamp(before, 0, count); from = max(0, to - count).
        if (count <= 0) return [];
        var total = MessageCountOf(sessionId);
        var to = Math.Clamp(beforeOrdinal, 0, total);
        var from = Math.Max(0, to - count);
        return ReadRange(sessionId, from, to);
    }

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive)
    {
        // Reference-parity bounds: unknown session is an empty window; a known
        // session validates the window strictly (from >= 0, to >= from, to <= count).
        if (!SessionRowExists(sessionId)) return [];
        var total = MessageCountOf(sessionId);
        if (fromOrdinal < 0 || toOrdinalExclusive < fromOrdinal || toOrdinalExclusive > total)
        {
            throw new InvalidOperationException(
                $"Range [{fromOrdinal},{toOrdinalExclusive}) outside count={total} in {sessionId}");
        }
        return ReadMessages(
            "SELECT m.ordinal, m.role, p.kind, p.tool_call_id, p.tool_name, p.image_id, b.content " +
            "FROM messages m " +
            "JOIN parts p ON p.session_id = m.session_id AND p.ordinal = m.ordinal " +
            "JOIN blocks b ON b.block_id = p.block_id " +
            "WHERE m.session_id = @s AND m.ordinal >= @from AND m.ordinal < @to " +
            "ORDER BY m.ordinal, p.part_index",
            new Dictionary<string, object> { ["@s"] = sessionId, ["@from"] = fromOrdinal, ["@to"] = toOrdinalExclusive });
    }

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars)
    {
        // Unknown/empty sessions or non-positive counts read as an empty window
        // (absence is not an error; reference-store parity).
        var total = MessageCountOf(sessionId);
        if (count <= 0 || total == 0)
        {
            return new WindowPreview(sessionId, 0, []);
        }
        var from = Math.Max(0, total - count);

        // Bounded metadata read: ONLY header + part rows are touched on the
        // default path; block content is never opened. Roles come from the
        // header column only.
        using var command = Command(
            "SELECT p.ordinal, m.role, p.kind, p.content_chars, p.tool_call_id, p.preview, p.bounded, p.part_index " +
            "FROM parts p " +
            "JOIN messages m ON m.session_id = p.session_id AND m.ordinal = p.ordinal " +
            "WHERE p.session_id = @s AND p.ordinal >= @from ORDER BY p.ordinal, p.part_index");
        command.Parameters.AddWithValue("@s", sessionId);
        command.Parameters.AddWithValue("@from", from);
        using var reader = command.ExecuteReader();

        var items = new List<MessagePreview>();
        long activeOrdinal = -1;
        string activeRole = string.Empty;
        var parts = new List<PartPreview>();
        var fallbackIndexes = new List<int>();
        void CompleteCurrent()
        {
            if (activeOrdinal < 0) return;
            if (fallbackIndexes.Count > 0)
            {
                foreach (var index in fallbackIndexes)
                {
                    // Fallback = read ONE referenced block (documented bound:
                    // the only preview path that touches content text).
                    var content = BlockContentOf(sessionId, activeOrdinal, index);
                    parts[index] = parts[index] with
                    {
                        Preview = content[..Math.Min(content.Length, Math.Max(previewChars, 0))],
                    };
                }
            }
            items.Add(new MessagePreview(activeOrdinal, activeRole, parts));
        }
        while (reader.Read())
        {
            var ordinal = reader.GetInt64(0);
            if (ordinal != activeOrdinal)
            {
                CompleteCurrent();
                activeOrdinal = ordinal;
                activeRole = reader.GetString(1);
                parts = [];
                fallbackIndexes = [];
            }
            var kind = reader.GetString(2);
            var contentChars = reader.GetInt64(3);
            var toolCallId = reader.IsDBNull(4) ? null : reader.GetString(4);
            var preview = reader.GetString(5);
            var bounded = reader.GetBoolean(6);
            string text;
            if (bounded && previewChars > preview.Length)
            {
                fallbackIndexes.Add(parts.Count);
                text = string.Empty; // filled when this message completes
            }
            else
            {
                text = preview[..Math.Min(preview.Length, Math.Max(previewChars, 0))];
            }
            parts.Add(new PartPreview(kind, contentChars, toolCallId, text));
        }
        CompleteCurrent();
        return new WindowPreview(sessionId, from, items);
    }

    public string FetchToolResult(string sessionId, long ordinal, int partIndex)
    {
        // Targeted read: one part row + its block row, nothing else decoded.
        using var command = Command(
            "SELECT b.content FROM parts p " +
            "JOIN blocks b ON b.block_id = p.block_id " +
            "WHERE p.session_id = @s AND p.ordinal = @ord AND p.part_index = @pi AND p.kind = 'toolResult'");
        command.Parameters.AddWithValue("@s", sessionId);
        command.Parameters.AddWithValue("@ord", ordinal);
        command.Parameters.AddWithValue("@pi", (long)partIndex);
        var result = command.ExecuteScalar() as string
            ?? throw new InvalidOperationException(
                $"No tool result part {partIndex} in {sessionId}@{ordinal}");
        return result;
    }

    // ---- mutations (one committed transaction per call; OS-flush ack) ----

    public long AppendMessage(string sessionId, MessageRecord message)
    {
        using var tx = Begin();
        var ordinal = InsertMessage(sessionId, message, tx);
        tx.Commit();
        return ordinal;
    }

    public void StreamDraft(string sessionId, string streamId, string chunk)
    {
        // One incremental persisted batch per call. Identity rules match the
        // reference: one live draft per session; a live streamId cannot move
        // to another session; the first call registers identity even for an
        // empty chunk.
        using var tx = Begin();
        var existing = ScalarText(tx, "SELECT stream_id FROM streams WHERE session_id = @s", sessionId, sessionId);
        if (existing is null)
        {
            var otherSession = ScalarText(tx, "SELECT session_id FROM streams WHERE stream_id = @id", streamId, streamId);
            if (otherSession is not null)
            {
                throw new InvalidOperationException(
                    $"Stream {streamId} is already active in session {otherSession}");
            }
            using var insert = Command(
                "INSERT INTO streams(session_id, stream_id, draft_text) VALUES(@s, @id, @text)", tx);
            insert.Parameters.AddWithValue("@s", sessionId);
            insert.Parameters.AddWithValue("@id", streamId);
            insert.Parameters.AddWithValue("@text", chunk);
            insert.ExecuteNonQuery();
        }
        else if (existing != streamId)
        {
            throw new InvalidOperationException(
                $"Session {sessionId} already has active draft {existing}; only one draft per session");
        }
        else
        {
            using var update = Command(
                "UPDATE streams SET draft_text = draft_text || @chunk WHERE session_id = @s", tx);
            update.Parameters.AddWithValue("@s", sessionId);
            update.Parameters.AddWithValue("@chunk", chunk);
            update.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage)
    {
        using var tx = Begin();
        var owningSession = ScalarText(tx, "SELECT session_id FROM streams WHERE stream_id = @id", streamId, streamId);
        if (owningSession is null || owningSession != sessionId)
        {
            throw new InvalidOperationException($"No active draft {streamId} in {sessionId}");
        }
        using (var delete = Command("DELETE FROM streams WHERE stream_id = @id", tx))
        {
            delete.Parameters.AddWithValue("@id", streamId);
            delete.ExecuteNonQuery();
        }
        var ordinal = InsertMessage(sessionId, finalMessage, tx);
        tx.Commit();
        return ordinal;
    }

    public long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages)
    {
        using var tx = Begin();
        if (!RowExists(tx, sessionId))
        {
            throw new InvalidOperationException($"Unknown session {sessionId}");
        }
        var total = MessageCount(tx, sessionId);
        if (fromOrdinal < 0 || fromOrdinal > total)
        {
            throw new InvalidOperationException(
                $"ReplaceSuffix from={fromOrdinal} outside count={total} in {sessionId}");
        }
        // Explicit orphan cleanup FIRST, by primary-key subselect: the blocks
        // the removed parts referenced, and nothing else, inside this tx.
        ExecIn(tx,
            "DELETE FROM blocks WHERE block_id IN (" +
            "  SELECT block_id FROM parts WHERE session_id = @s AND ordinal >= @from)",
            sessionId, fromOrdinal);
        ExecIn(tx, "DELETE FROM parts WHERE session_id = @s AND ordinal >= @from", sessionId, fromOrdinal);
        ExecIn(tx, "DELETE FROM messages WHERE session_id = @s AND ordinal >= @from", sessionId, fromOrdinal);
        foreach (var message in newMessages)
        {
            InsertMessage(sessionId, message, tx);
        }
        tx.Commit();
        return fromOrdinal + newMessages.Count;
    }

    public void CreateAssignment(AssignmentRecord assignment)
    {
        using var tx = Begin();
        if (RowExists(tx, assignment.AssignmentId, "assignments", "assignment_id"))
        {
            throw new InvalidOperationException($"Duplicate assignment id {assignment.AssignmentId}");
        }
        using var command = Command(
            "INSERT INTO assignments(assignment_id, target_session_id, title, status, result_text) " +
            "VALUES(@id, @target, @title, @status, @result)", tx);
        command.Parameters.AddWithValue("@id", assignment.AssignmentId);
        command.Parameters.AddWithValue("@target", assignment.TargetSessionId);
        command.Parameters.AddWithValue("@title", assignment.Title);
        command.Parameters.AddWithValue("@status", (long)assignment.Status);
        command.Parameters.AddWithValue("@result", (object?)assignment.ResultText ?? DBNull.Value);
        command.ExecuteNonQuery();
        tx.Commit();
    }

    public void SettleAssignment(SettleAssignmentRequest request)
    {
        using var tx = Begin();
        if (!RowExists(tx, request.AssignmentId, "assignments", "assignment_id"))
        {
            throw new InvalidOperationException($"Unknown assignment {request.AssignmentId}");
        }
        using var command = Command(
            "UPDATE assignments SET status = @status, result_text = @result WHERE assignment_id = @id", tx);
        command.Parameters.AddWithValue("@id", request.AssignmentId);
        command.Parameters.AddWithValue("@status", (long)AssignmentStatus.Settled);
        command.Parameters.AddWithValue("@result", request.ResultText);
        command.ExecuteNonQuery();
        tx.Commit();
    }

    public AssignmentRecord GetAssignment(string assignmentId)
    {
        using var command = Command(
            "SELECT target_session_id, title, status, result_text FROM assignments WHERE assignment_id = @id");
        command.Parameters.AddWithValue("@id", assignmentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Unknown assignment {assignmentId}");
        }
        return new AssignmentRecord(
            assignmentId,
            reader.GetString(0),
            reader.GetString(1),
            (AssignmentStatus)reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public void DeleteSession(string sessionId, DeletePolicy policy)
    {
        using var tx = Begin();
        if (!RowExists(tx, sessionId))
        {
            throw new InvalidOperationException($"DeleteSession of unknown session {sessionId}");
        }
        long openCount;
        using (var open = Command(
            "SELECT COUNT(*) FROM assignments WHERE target_session_id = @s AND status = @open", tx))
        {
            open.Parameters.AddWithValue("@s", sessionId);
            open.Parameters.AddWithValue("@open", (long)AssignmentStatus.Open);
            openCount = (long)(open.ExecuteScalar() ?? 0L);
        }
        if (openCount > 0)
        {
            if (policy == DeletePolicy.RequireSettled)
            {
                throw new InvalidOperationException(
                    $"DeleteSession {sessionId}: {openCount} open assignment(s) violate the RequireSettled invariant");
            }
            // CancelOpen: cancellation joins the SAME atomic logical operation.
            using var cancel = Command(
                "UPDATE assignments SET status = @cancelled WHERE target_session_id = @s AND status = @open", tx);
            cancel.Parameters.AddWithValue("@s", sessionId);
            cancel.Parameters.AddWithValue("@cancelled", (long)AssignmentStatus.Cancelled);
            cancel.Parameters.AddWithValue("@open", (long)AssignmentStatus.Open);
            cancel.ExecuteNonQuery();
        }
        // Final closure: blocks (explicit orphan cleanup), parts, messages,
        // drafts and the session row, atomically. Settled/cancelled assignment
        // records stay (history; no longer dangling).
        ExecIn(tx, "DELETE FROM blocks WHERE block_id IN (SELECT block_id FROM parts WHERE session_id = @s)", sessionId);
        ExecIn(tx, "DELETE FROM parts WHERE session_id = @s", sessionId);
        ExecIn(tx, "DELETE FROM messages WHERE session_id = @s", sessionId);
        ExecIn(tx, "DELETE FROM streams WHERE session_id = @s", sessionId);
        ExecIn(tx, "DELETE FROM sessions WHERE session_id = @s", sessionId);
        tx.Commit();
    }

    public StoreStatistics Sample() => new(GC.GetTotalAllocatedBytes(precise: false));

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    // ---- internals ----

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS sessions (
          session_id TEXT PRIMARY KEY
        );
        CREATE TABLE IF NOT EXISTS messages (
          session_id TEXT NOT NULL,
          ordinal    INTEGER NOT NULL,
          role       TEXT NOT NULL,       -- header only; content lives in blocks
          PRIMARY KEY (session_id, ordinal)
        );
        CREATE TABLE IF NOT EXISTS parts (
          session_id    TEXT NOT NULL,
          ordinal       INTEGER NOT NULL,
          part_index    INTEGER NOT NULL,
          kind          TEXT NOT NULL,     -- text/toolCall/toolResult/image
          block_id      TEXT NOT NULL,     -- reference into blocks (one-to-one)
          tool_call_id  TEXT,              -- toolCall/toolResult only
          tool_name     TEXT,              -- toolCall only
          image_id      TEXT,              -- image only
          content_chars INTEGER NOT NULL,  -- full-content character length (write-time)
          preview       TEXT NOT NULL,     -- bounded leading prefix (<= PreviewStoredPrefix)
          bounded       INTEGER NOT NULL,  -- 1 when preview cut at the stored cap
          PRIMARY KEY (session_id, ordinal, part_index)
        );
        CREATE TABLE IF NOT EXISTS blocks (
          block_id TEXT PRIMARY KEY,       -- "blk:" + session + ":" + ordinal + ":" + part
          content  TEXT NOT NULL           -- raw TEXT; the separately addressable content
        );
        CREATE TABLE IF NOT EXISTS streams (
          stream_id  TEXT PRIMARY KEY,     -- one live draft stream per id
          session_id TEXT NOT NULL,
          draft_text TEXT NOT NULL,        -- accumulated committed batches
          UNIQUE (session_id)
        );
        CREATE TABLE IF NOT EXISTS assignments (
          assignment_id     TEXT PRIMARY KEY,
          target_session_id TEXT NOT NULL,
          title             TEXT NOT NULL,
          status            INTEGER NOT NULL, -- 0 open / 1 settled / 2 cancelled
          result_text       TEXT
        );
        """;

    private SqliteConnection Db => _connection
        ?? throw new InvalidOperationException("Store is not open (Open() was not called)");

    private SqliteTransaction Begin() => Db.BeginTransaction();

    private SqliteCommand Command(string sql, SqliteTransaction? tx = null)
    {
        var command = Db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }

    private void Exec(string sql)
    {
        using var command = Command(sql);
        command.ExecuteNonQuery();
    }

    private void ExecIn(SqliteTransaction tx, string sql, string sessionId, long from = long.MinValue)
    {
        using var command = Command(sql, tx);
        command.Parameters.AddWithValue("@s", sessionId);
        if (from != long.MinValue) command.Parameters.AddWithValue("@from", from);
        command.ExecuteNonQuery();
    }

    private long InsertMessage(string sessionId, MessageRecord message, SqliteTransaction tx)
    {
        using (var ensure = Command("INSERT OR IGNORE INTO sessions(session_id) VALUES(@s)", tx))
        {
            ensure.Parameters.AddWithValue("@s", sessionId);
            ensure.ExecuteNonQuery();
        }
        var ordinal = MessageCount(tx, sessionId);
        using (var insert = Command(
            "INSERT INTO messages(session_id, ordinal, role) VALUES(@s, @ord, @role)", tx))
        {
            insert.Parameters.AddWithValue("@s", sessionId);
            insert.Parameters.AddWithValue("@ord", ordinal);
            insert.Parameters.AddWithValue("@role", message.Role);
            insert.ExecuteNonQuery();
        }
        for (var i = 0; i < message.Parts.Count; i++)
        {
            var part = message.Parts[i];
            var (kind, toolCallId, toolName, imageId) = PartOf(part);
            var content = ContentOf(part);
            var blockId = $"blk:{sessionId}:{ordinal}:{i}";
            using (var block = Command("INSERT INTO blocks(block_id, content) VALUES(@b, @c)", tx))
            {
                block.Parameters.AddWithValue("@b", blockId);
                block.Parameters.AddWithValue("@c", content);
                block.ExecuteNonQuery();
            }
            var cut = Math.Min(content.Length, PreviewStoredPrefix);
            using var meta = Command(
                "INSERT INTO parts(session_id, ordinal, part_index, kind, block_id, tool_call_id, tool_name, image_id, content_chars, preview, bounded) " +
                "VALUES(@s, @ord, @i, @kind, @b, @tc, @tn, @ii, @chars, @preview, @bounded)", tx);
            meta.Parameters.AddWithValue("@s", sessionId);
            meta.Parameters.AddWithValue("@ord", ordinal);
            meta.Parameters.AddWithValue("@i", (long)i);
            meta.Parameters.AddWithValue("@kind", kind);
            meta.Parameters.AddWithValue("@b", blockId);
            meta.Parameters.AddWithValue("@tc", (object?)toolCallId ?? DBNull.Value);
            meta.Parameters.AddWithValue("@tn", (object?)toolName ?? DBNull.Value);
            meta.Parameters.AddWithValue("@ii", (object?)imageId ?? DBNull.Value);
            meta.Parameters.AddWithValue("@chars", (long)content.Length);
            meta.Parameters.AddWithValue("@preview", cut > 0 ? content[..cut] : string.Empty);
            meta.Parameters.AddWithValue("@bounded", content.Length > PreviewStoredPrefix ? 1L : 0L);
            meta.ExecuteNonQuery();
        }
        return ordinal;
    }

    private static (string Kind, string? ToolCallId, string? ToolName, string? ImageId) PartOf(MessagePart part) => part switch
    {
        TextPart => ("text", null, null, null),
        ToolCallPart c => ("toolCall", c.ToolCallId, c.Name, null),
        ToolResultPart r => ("toolResult", r.ToolCallId, null, null),
        ImagePart i => ("image", null, null, i.ImageId),
        _ => throw new InvalidOperationException($"Unsupported part type {part.GetType().Name}"),
    };

    /// <summary>The raw text content stored (and previewed) for a part; images
    /// preview their relative path (oracle-conform mapping).</summary>
    private static string ContentOf(MessagePart part) => part switch
    {
        TextPart t => t.Content,
        ToolCallPart c => c.ArgsJson,
        ToolResultPart r => r.Content,
        ImagePart i => i.RelativePath,
        _ => string.Empty,
    };

    private long MessageCount(SqliteTransaction tx, string sessionId)
    {
        using var command = Command(
            "SELECT COALESCE(MAX(ordinal) + 1, 0) FROM messages WHERE session_id = @s", tx);
        command.Parameters.AddWithValue("@s", sessionId);
        return ScalarLong(command);
    }

    /// <summary>Message count of a session; 0 for unknown or empty sessions.</summary>
    private long MessageCountOf(string sessionId)
    {
        using var command = Command(
            "SELECT COALESCE(MAX(ordinal) + 1, 0) FROM messages WHERE session_id = @s");
        command.Parameters.AddWithValue("@s", sessionId);
        return ScalarLong(command);
    }

    private string BlockContentOf(string sessionId, long ordinal, int partIndex)
    {
        using var command = Command(
            "SELECT b.content FROM parts p JOIN blocks b ON b.block_id = p.block_id " +
            "WHERE p.session_id = @s AND p.ordinal = @ord AND p.part_index = @pi");
        command.Parameters.AddWithValue("@s", sessionId);
        command.Parameters.AddWithValue("@ord", ordinal);
        command.Parameters.AddWithValue("@pi", (long)partIndex);
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException($"Missing block for {sessionId}@{ordinal}[{partIndex}]");
    }

    private bool SessionRowExists(string sessionId)
    {
        using var command = Command("SELECT 1 FROM sessions WHERE session_id = @s");
        command.Parameters.AddWithValue("@s", sessionId);
        return command.ExecuteScalar() is not null;
    }

    private MessageRecord[] ReadMessages(string sql, Dictionary<string, object> parameters)
    {
        using var command = Command(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        using var reader = command.ExecuteReader();
        var messages = new List<MessageRecord>();
        long activeOrdinal = -1;
        string activeRole = string.Empty;
        var parts = new List<MessagePart>();
        while (reader.Read())
        {
            var ordinal = reader.GetInt64(0);
            if (ordinal != activeOrdinal)
            {
                if (activeOrdinal >= 0) messages.Add(new MessageRecord(activeRole, [.. parts]));
                activeOrdinal = ordinal;
                activeRole = reader.GetString(1);
                parts = [];
            }
            var kind = reader.GetString(2);
            var toolCallId = reader.IsDBNull(3) ? null : reader.GetString(3);
            var toolName = reader.IsDBNull(4) ? null : reader.GetString(4);
            var imageId = reader.IsDBNull(5) ? null : reader.GetString(5);
            var content = reader.GetString(6);
            parts.Add(kind switch
            {
                "text" => new TextPart(content),
                "toolCall" => new ToolCallPart(toolCallId!, toolName!, content),
                "toolResult" => new ToolResultPart(toolCallId!, content),
                "image" => new ImagePart(imageId!, content),
                _ => throw new InvalidOperationException($"Corrupt part row: unknown kind '{kind}'"),
            });
        }
        if (activeOrdinal >= 0) messages.Add(new MessageRecord(activeRole, [.. parts]));
        return [.. messages];
    }

    private static long ScalarLong(SqliteCommand command) => Convert.ToInt64(
        command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);

    private string? ScalarText(SqliteTransaction tx, string sql, string value, string idValue)
    {
        using var command = Command(sql, tx);
        command.Parameters.AddWithValue("@s", value);
        command.Parameters.AddWithValue("@id", idValue);
        return command.ExecuteScalar() as string;
    }

    private bool RowExists(SqliteTransaction tx, string value, string? table = null, string? column = null)
    {
        var sql = table is null
            ? "SELECT 1 FROM sessions WHERE session_id = @s"
            : $"SELECT 1 FROM {table} WHERE {column} = @id";
        using var command = Command(sql, tx);
        command.Parameters.AddWithValue("@s", value);
        command.Parameters.AddWithValue("@id", value);
        return command.ExecuteScalar() is not null;
    }
}