using System.Globalization;
using Microsoft.Data.Sqlite;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.SqliteRows;

// Family-1 adapter: SHARED SQLite, one row per message (the classic
// row-per-message baseline).
//
// Layout record (declared, per STORAGE-EXPERIMENT.md; distinct from the
// content-block design of family 2):
//   - One database holds every session. Session existence is a row in
//     `sessions`; message content is one `messages` row per message whose
//     (session_id, ordinal) primary key IS the ordered index. Ordinals are
//     dense 0..count-1 by construction (suffix delete+insert renumbering).
//   - Message parts live ONLY inside the message row's `parts_json` envelope
//     (raw text + JSON; see PartsCodec). There are NO content-block rows and
//     NO spill table — large tool results are inline message-row content. This
//     is exactly where the family-2 block design would differ.
//   - `parts_meta` is a DERIVED, queryable metadata table (kind, content
//     character length, toolCallId, bounded leading TEXT prefix) per part.
//     Bounded preview reads are answered from this table WITHOUT decoding any
//     message payload. It never stores full content, only the bounded prefix.
//   - `streams` persists active draft streams; every StreamDraft call commits
//     one batch (append into the draft row) in its own transaction — no
//     deferred-all-writes-to-finalize buffering. FinalizeStream inserts the
//     final message and removes the draft in one committed transaction, so
//     nothing partial is ever served as message content.
//   - `assignments` stores the bounded assignment/result records.
//
// Edit mechanics (declared): ReplaceSuffix = one committed transaction doing
// DELETE WHERE session=? AND ordinal>=? followed by INSERT of the replacement
// rows renumbered densely from the edit point — the classic suffix delete +
// insert, atomic by SQLite transaction. History replacement only.
//
// Acknowledgement boundary (declared): every mutation runs one transaction and
// COMMITs it BEFORE the call returns; with journal_mode=WAL +
// synchronous=NORMAL a commit hands completed data to the OS without an fsync
// — that is the OS-flush ack tier, recorded verbatim. NO power-loss or
// fsync-durability claim is made; SQLite WAL provides process-crash recovery.
//
// Recovery rules (declared): ordinary reopen replays the WAL / rolls back any
// torn commit; a record is either absent or complete; every acknowledged
// (committed) record replays identically after reopen. Recovery never invents
// or reorders records. An unacknowledged (mid-transaction) mutation may persist
// as the full before-state or the fully committed after-state. Active drafts
// are persisted alongside messages (per-batch committed), so a reopen with an
// unfinished stream finds it intact and it still never serves content.

public sealed class SqliteRowsTranscriptStore : ITranscriptStore, IStoreStatistics
{
    /// <summary>
    /// Leading-prefix character cap stored in parts_meta.preview. Preview
    /// requests with previewChars &le; this cap are answered purely from
    /// parts_meta; larger requests may decode the flagged message's entire
    /// envelope (documented; the bounded-metadata path is the designed default).
    /// </summary>
    internal const int PreviewStoredPrefix = 256;

    private SqliteConnection? _connection;

    public StoreDescriptor Describe() => new(
        "SqliteRowsStore",
        AckTier.OsFlush,
        "journal_mode=WAL; synchronous=NORMAL; wal_autocheckpoint=1000 (SQLite default); " +
            "every mutation is one transaction COMMIT-ed before the call returns",
        "Family 1 shared-SQLite row-per-message baseline (Microsoft.Data.Sqlite 10.0.14, shared single database). " +
            "Content is JSON envelope text inside the message row; no content blocks, no spill table. " +
            "parts_meta is derived preview metadata with a bounded stored prefix (default path never decodes payloads). " +
            "Edit = atomic DELETE+INSERT of the suffix in one transaction. " +
            "StreamDraft = one committed persisted batch per call; drafts survive reopen and never serve partial content. " +
            "Recovery: SQLite WAL/rollback; acknowledged records replay identically; no power-loss claim.");

    public void Open(string storeRoot, string attachmentRoot)
    {
        // attachmentRoot is harness-owned; this store persists only the stable
        // image reference/path records and never touches attachment files.
        NativeDependency.EnsureResolved();
        Directory.CreateDirectory(storeRoot);
        _connection?.Dispose();
        _connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(storeRoot, "transcript.db") }.ToString());
        _connection.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec("PRAGMA foreign_keys=OFF"); // single-file store; referential moves are explicit SQL, not FKs
        Exec(SchemaSql);
    }

    // ---- reads ----

    public SessionMeta[] ListSessionsMeta()
    {
        // `sessions` rows are the existence record; counts derive from the
        // message table so they cannot drift. Left join keeps sessions that
        // were truncated to zero messages visible (reference-store parity).
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
            "SELECT m.ordinal, m.role, m.parts_json FROM (" +
            "  SELECT ordinal FROM messages WHERE session_id = @s ORDER BY ordinal DESC LIMIT @n" +
            ") win JOIN messages m ON m.session_id = @s AND m.ordinal = win.ordinal " +
            "ORDER BY m.ordinal",
            new Dictionary<string, object> { ["@s"] = sessionId, ["@n"] = (long)count });
    }

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count)
    {
        // Reference-parity clamping: to = clamp(before, 0, count); from = max(0, to - count).
        if (count <= 0) return [];
        var total = MessageCountOf(sessionId);
        var to = Math.Clamp(beforeOrdinal, 0, total);
        var from = Math.Max(0, to - count);
        return ReadMessages(
            "SELECT ordinal, role, parts_json FROM messages " +
            "WHERE session_id = @s AND ordinal >= @from AND ordinal < @to ORDER BY ordinal",
            new Dictionary<string, object> { ["@s"] = sessionId, ["@from"] = from, ["@to"] = to });
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
            "SELECT ordinal, role, parts_json FROM messages " +
            "WHERE session_id = @s AND ordinal >= @from AND ordinal < @to ORDER BY ordinal",
            new Dictionary<string, object>
            {
                ["@s"] = sessionId,
                ["@from"] = fromOrdinal,
                ["@to"] = toOrdinalExclusive,
            });
    }

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars)
    {
        if (count <= 0) return new WindowPreview(sessionId, 0, []);

        // Hold one read snapshot through any fallback, including after the
        // metadata reader reaches DONE. Deferred: do not reserve the writer lock.
        using var tx = Db.BeginTransaction(deferred: true);
        // Message-led LEFT JOIN retains headers with no parts. Neither the
        // window nor its cursor needs a separate count or any payload JSON.
        using var command = Command(
            "SELECT m.ordinal, m.role, pm.kind, pm.content_chars, pm.tool_call_id, pm.preview, pm.bounded " +
            "FROM (" +
            "  SELECT ordinal FROM messages WHERE session_id = @s ORDER BY ordinal DESC LIMIT @n" +
            ") win JOIN messages m ON m.session_id = @s AND m.ordinal = win.ordinal " +
            "LEFT JOIN parts_meta pm ON pm.session_id = m.session_id AND pm.ordinal = m.ordinal " +
            "ORDER BY m.ordinal, pm.part_index", tx);
        command.Parameters.AddWithValue("@s", sessionId);
        command.Parameters.AddWithValue("@n", (long)count);
        using var reader = command.ExecuteReader();

        var items = new List<MessagePreview>();
        long activeOrdinal = -1;
        string activeRole = string.Empty;
        var parts = new List<PartPreview>();
        var currentFallbacks = new List<int>();
        void CompleteCurrent()
        {
            if (activeOrdinal < 0) return;
            if (currentFallbacks.Count > 0)
            {
                var messageParts = PartsCodec.FromJson(RequirePartsJson(sessionId, activeOrdinal, tx));
                foreach (var index in currentFallbacks)
                {
                    var content = PartsCodec.ContentOf(messageParts[index]);
                    parts[index] = parts[index] with
                    {
                        Preview = PreviewText.Prefix(content, previewChars),
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
                currentFallbacks = [];
            }
            if (reader.IsDBNull(2)) continue; // zero-part message: keep its header
            var kind = reader.GetString(2);
            var contentChars = reader.GetInt64(3);
            var toolCallId = reader.IsDBNull(4) ? null : reader.GetString(4);
            var preview = reader.GetString(5);
            var bounded = reader.GetBoolean(6);
            string text;
            if (bounded && previewChars > PreviewStoredPrefix)
            {
                // A Unicode-safe stored prefix can be one code unit shorter
                // than the cap; that must not trigger payload reads at <= cap.
                // Larger requests decode this message's entire envelope once.
                currentFallbacks.Add(parts.Count);
                text = string.Empty; // filled in when the message completes
            }
            else
            {
                text = PreviewText.Prefix(preview, previewChars);
            }
            parts.Add(new PartPreview(kind, contentChars, toolCallId, text));
        }
        CompleteCurrent();
        reader.Close();
        tx.Commit();
        return new WindowPreview(sessionId, items.Count == 0 ? 0 : items[0].Ordinal, items);
    }

    public string FetchToolResult(string sessionId, long ordinal, int partIndex)
    {
        var parts = PartsCodec.FromJson(RequirePartsJson(sessionId, ordinal));
        var part = parts.ElementAtOrDefault(partIndex)
            ?? throw new InvalidOperationException(
                $"No part {partIndex} in {sessionId}@{ordinal} (parts={parts.Count})");
        return part is ToolResultPart toolResult
            ? toolResult.Content
            : throw new InvalidOperationException(
                $"Part {partIndex} in {sessionId}@{ordinal} is {part.GetType().Name}, not a tool result");
    }

    // ---- mutations (one committed transaction per call; OS-flush ack) ----

    public long AppendMessage(string sessionId, MessageRecord message)
    {
        using var tx = Db.BeginTransaction();
        var ordinal = InsertMessage(sessionId, message, tx);
        tx.Commit();
        return ordinal;
    }

    public void StreamDraft(string sessionId, string streamId, string chunk)
    {
        // One incremental persisted batch per call. A different streamId while
        // a stream is active in this session is an error; a streamId still
        // active in another session is also rejected (a stream id names one
        // live draft). The batch is registered even when the chunk is empty,
        // so stream identity is observable before the first non-empty batch.
        using var tx = Db.BeginTransaction();
        var existing = QueryText(tx, "SELECT stream_id FROM streams WHERE session_id = @s", sessionId);
        if (existing is null)
        {
            var otherSession = QueryText(tx, "SELECT session_id FROM streams WHERE stream_id = @id", streamId);
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
        using var tx = Db.BeginTransaction();
        var owningSession = QueryText(tx, "SELECT session_id FROM streams WHERE stream_id = @id", streamId);
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
        using var tx = Db.BeginTransaction();
        if (!RowExists(tx, "SELECT 1 FROM sessions WHERE session_id = @s", sessionId))
        {
            throw new InvalidOperationException($"Unknown session {sessionId}");
        }
        var total = MessageCount(tx, sessionId);
        if (fromOrdinal < 0 || fromOrdinal > total)
        {
            throw new InvalidOperationException(
                $"ReplaceSuffix from={fromOrdinal} outside count={total} in {sessionId}");
        }
        using (var stripMeta = Command(
            "DELETE FROM parts_meta WHERE session_id = @s AND ordinal >= @from", tx))
        {
            stripMeta.Parameters.AddWithValue("@s", sessionId);
            stripMeta.Parameters.AddWithValue("@from", fromOrdinal);
            stripMeta.ExecuteNonQuery();
        }
        using (var strip = Command(
            "DELETE FROM messages WHERE session_id = @s AND ordinal >= @from", tx))
        {
            strip.Parameters.AddWithValue("@s", sessionId);
            strip.Parameters.AddWithValue("@from", fromOrdinal);
            strip.ExecuteNonQuery();
        }
        foreach (var message in newMessages)
        {
            InsertMessage(sessionId, message, tx);
        }
        tx.Commit();
        return fromOrdinal + newMessages.Count;
    }

    public void CreateAssignment(AssignmentRecord assignment)
    {
        using var tx = Db.BeginTransaction();
        if (RowExists(tx, "SELECT 1 FROM assignments WHERE assignment_id = @id", assignment.AssignmentId))
        {
            throw new InvalidOperationException($"Duplicate assignment id {assignment.AssignmentId}");
        }
        using var command = Command(
            "INSERT INTO assignments(assignment_id, target_session_id, title, status, result_text) " +
            "VALUES(@id, @target, @title, @status, @result)", tx);
        command.Parameters.AddWithValue("@id", assignment.AssignmentId);
        command.Parameters.AddWithValue("@target", assignment.TargetSessionId);
        command.Parameters.AddWithValue("@title", assignment.Title);
        command.Parameters.AddWithValue("@status", (int)assignment.Status);
        command.Parameters.AddWithValue("@result", (object?)assignment.ResultText ?? DBNull.Value);
        command.ExecuteNonQuery();
        tx.Commit();
    }

    public void SettleAssignment(SettleAssignmentRequest request)
    {
        using var tx = Db.BeginTransaction();
        if (!RowExists(tx, "SELECT 1 FROM assignments WHERE assignment_id = @id", request.AssignmentId))
        {
            throw new InvalidOperationException($"Unknown assignment {request.AssignmentId}");
        }
        using var command = Command(
            "UPDATE assignments SET status = @status, result_text = @result WHERE assignment_id = @id", tx);
        command.Parameters.AddWithValue("@id", request.AssignmentId);
        command.Parameters.AddWithValue("@status", (int)AssignmentStatus.Settled);
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
            (AssignmentStatus)(long)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public void DeleteSession(string sessionId, DeletePolicy policy)
    {
        using var tx = Db.BeginTransaction();
        if (!RowExists(tx, "SELECT 1 FROM sessions WHERE session_id = @s", sessionId))
        {
            throw new InvalidOperationException($"DeleteSession of unknown session {sessionId}");
        }
        using (var open = Command(
            "SELECT COUNT(*) FROM assignments WHERE target_session_id = @s AND status = @open", tx))
        {
            open.Parameters.AddWithValue("@s", sessionId);
            open.Parameters.AddWithValue("@open", (int)AssignmentStatus.Open);
            var openCount = (long)(open.ExecuteScalar() ?? 0L);
            if (openCount > 0)
            {
                if (policy == DeletePolicy.RequireSettled)
                {
                    throw new InvalidOperationException(
                        $"DeleteSession {sessionId}: {openCount} open assignment(s) violate the RequireSettled invariant");
                }
                // CancelOpen: cancellation is part of the SAME atomic logical operation.
                using var cancel = Command(
                    "UPDATE assignments SET status = @cancelled WHERE target_session_id = @s AND status = @open", tx);
                cancel.Parameters.AddWithValue("@s", sessionId);
                cancel.Parameters.AddWithValue("@cancelled", (int)AssignmentStatus.Cancelled);
                cancel.Parameters.AddWithValue("@open", (int)AssignmentStatus.Open);
                cancel.ExecuteNonQuery();
            }
        }
        // Final closure: payloads, derived metadata, active drafts and the
        // session row, atomically. Settled/cancelled assignment records stay
        // (they are assignment history, no longer dangling).
        ExecIn(tx, "DELETE FROM messages WHERE session_id = @s", sessionId);
        ExecIn(tx, "DELETE FROM parts_meta WHERE session_id = @s", sessionId);
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
          role       TEXT NOT NULL,
          parts_json TEXT NOT NULL,  -- full content of every part (raw text + JSON; the ONLY content storage)
          PRIMARY KEY (session_id, ordinal)
        );
        CREATE TABLE IF NOT EXISTS parts_meta (
          session_id     TEXT NOT NULL,
          ordinal        INTEGER NOT NULL,
          part_index     INTEGER NOT NULL,
          kind           TEXT NOT NULL,   -- text/call/result/image
          content_chars  INTEGER NOT NULL,-- full-content character length
          tool_call_id   TEXT,            -- null unless call/result
          preview        TEXT NOT NULL,   -- bounded leading prefix (<= PreviewStoredPrefix)
          bounded        INTEGER NOT NULL,-- 1 when the preview was cut at the stored cap
          PRIMARY KEY (session_id, ordinal, part_index)
        );
        CREATE TABLE IF NOT EXISTS streams (
          stream_id  TEXT PRIMARY KEY,    -- one live draft stream per id
          session_id TEXT NOT NULL,
          draft_text TEXT NOT NULL,       -- accumulated committed batches
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

    private void ExecIn(SqliteTransaction tx, string sql, string sessionId)
    {
        using var command = Command(sql, tx);
        command.Parameters.AddWithValue("@s", sessionId);
        command.ExecuteNonQuery();
    }

    /// <summary>Inserts the message at the tail under the current transaction and returns the dense ordinal.</summary>
    private long InsertMessage(string sessionId, MessageRecord message, SqliteTransaction tx)
    {
        using (var ensure = Command("INSERT OR IGNORE INTO sessions(session_id) VALUES(@s)", tx))
        {
            ensure.Parameters.AddWithValue("@s", sessionId);
            ensure.ExecuteNonQuery();
        }
        var ordinal = MessageCount(tx, sessionId);
        using (var insert = Command(
            "INSERT INTO messages(session_id, ordinal, role, parts_json) VALUES(@s, @ord, @role, @json)", tx))
        {
            insert.Parameters.AddWithValue("@s", sessionId);
            insert.Parameters.AddWithValue("@ord", ordinal);
            insert.Parameters.AddWithValue("@role", message.Role);
            insert.Parameters.AddWithValue("@json", PartsCodec.Write(message.Parts));
            insert.ExecuteNonQuery();
        }
        for (var i = 0; i < message.Parts.Count; i++)
        {
            var (kind, toolCallId) = PartsCodec.MetaOf(message.Parts[i]);
            var content = PartsCodec.ContentOf(message.Parts[i]);
            var preview = PreviewText.Prefix(content, PreviewStoredPrefix);
            using var insertMeta = Command(
                "INSERT INTO parts_meta(session_id, ordinal, part_index, kind, content_chars, tool_call_id, preview, bounded) " +
                "VALUES(@s, @ord, @i, @kind, @chars, @id, @preview, @bounded)", tx);
            insertMeta.Parameters.AddWithValue("@s", sessionId);
            insertMeta.Parameters.AddWithValue("@ord", ordinal);
            insertMeta.Parameters.AddWithValue("@i", i);
            insertMeta.Parameters.AddWithValue("@kind", kind);
            insertMeta.Parameters.AddWithValue("@chars", content.Length);
            insertMeta.Parameters.AddWithValue("@id", (object?)toolCallId ?? DBNull.Value);
            insertMeta.Parameters.AddWithValue("@preview", preview);
            insertMeta.Parameters.AddWithValue("@bounded", content.Length > preview.Length ? 1 : 0);
            insertMeta.ExecuteNonQuery();
        }
        return ordinal;
    }

    private long MessageCount(SqliteTransaction tx, string sessionId)
    {
        // The ordered primary key makes this count a single index seek on the tail.
        using var command = Command(
            "SELECT COALESCE(MAX(ordinal) + 1, 0) FROM messages WHERE session_id = @s", tx);
        command.Parameters.AddWithValue("@s", sessionId);
        return ScalarLong(command);
    }

    /// <summary>Message count of a session; 0 for unknown or empty sessions.</summary>
    private long MessageCountOf(string sessionId)
    {
        using var command = Command("SELECT COUNT(*) FROM messages WHERE session_id = @s");
        command.Parameters.AddWithValue("@s", sessionId);
        return ScalarLong(command);
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
        while (reader.Read())
        {
            messages.Add(new MessageRecord(
                reader.GetString(1),
                PartsCodec.FromJson(reader.GetString(2))));
        }
        return [.. messages];
    }

    private string RequirePartsJson(string sessionId, long ordinal, SqliteTransaction? tx = null)
    {
        using var command = Command(
            "SELECT parts_json FROM messages WHERE session_id = @s AND ordinal = @ord", tx);
        command.Parameters.AddWithValue("@s", sessionId);
        command.Parameters.AddWithValue("@ord", ordinal);
        var result = command.ExecuteScalar();
        return result as string
            ?? throw new InvalidOperationException(
                $"Unknown session {sessionId} or ordinal {ordinal} outside history");
    }

    private string? QueryText(SqliteTransaction tx, string sql, string parameterValue)
    {
        using var command = Command(sql, tx);
        command.Parameters.AddWithValue("@s", parameterValue);
        command.Parameters.AddWithValue("@id", parameterValue);
        return command.ExecuteScalar() as string;
    }

    private static bool RowExists(SqliteTransaction tx, string sql, string parameterValue)
    {
        using var command = Db0(tx).CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@s", parameterValue);
        command.Parameters.AddWithValue("@id", parameterValue);
        return command.ExecuteScalar() is not null;
    }

    private static SqliteConnection Db0(SqliteTransaction tx) => tx.Connection
        ?? throw new InvalidOperationException("Transaction has no connection");

    private static long ScalarLong(SqliteCommand command) => Convert.ToInt64(
        command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
}