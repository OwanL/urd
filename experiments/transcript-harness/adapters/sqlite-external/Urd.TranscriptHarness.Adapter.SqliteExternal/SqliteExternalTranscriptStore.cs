using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.SqliteExternal;

// Family-7 adapter: SQLite METADATA + EXTERNAL PAYLOAD FILES.
//
// One shared SQLite database is the authority for all metadata, indexes and
// edits; every part's raw TEXT content lives in a separate, IMMUTABLE payload
// file addressed by a safe generated ID (`payloads/pl-<32 hex chars>` — Guid
// N-format, always lowercase hex, filesystem-safe, never derived from session
// or user content, so no path/hostile-name concerns and NO content-addressed
// dedup: identical parts get distinct files by construction). Part rows record
// the pointer (payload_id), write-time content statistics and a bounded
// preview prefix. Streams and assignments re-use the proven blocks-layout
// tables.
//
// Two-store ordering (declared — the designed risk of this family, treated
// explicitly, never hidden):
//   PUBLICATION: write + OS-flush the payload file FIRST, then COMMIT the
//     metadata transaction that references it. A kill in between can only
//     leave an UNREFERENCED payload file (never a published pointer without a
//     payload: the pointer does not exist until the payload is fully written).
//   REMOVAL: COMMIT the metadata removal FIRST, then delete the obsolete
//     payload files (existence-tolerant, idempotent; a delete failure or kill
//     before delete leaves an unreferenced file).
//   STARTUP ORPHAN SWEEP: Open() scans the payloads directory and deletes any
//     file not referenced by a committed part row, BEFORE serving anything —
//     this reclaims both of the above states; no background GC, no timers.
//   Both states are therefore exactly one declared shape: unreferenced payload
//   files, never referenced-missing ones. A missing file behind a committed
//   pointer cannot be produced by the store under this ordering (only by
//   external interference); reads fail loudly if it is ever observed.
//   There is NO assumed cross-file transaction: SQLite WAL covers the metadata
//   file only; the payload side is ordered by the above, not by atomicity.
//
// Bounded reads: OpenRecentPreview touches ONLY metadata + part rows on the
// default path — previews were computed at write time (C#-exact leading
// prefix, cap 256 UTF-16 code units) and payload files are not opened at the
// normal cap. previewChars beyond the stored cap falls back to reading the
// single referenced payload file of that one part (documented bound). Fetch
// ToolResult is a targeted read: one part row + its one payload file.
//
// Edit mechanics: ReplaceSuffix = one committed metadata transaction (remove
// the suffix's part rows — their payload IDs subselected first — and message
// headers, then reinsert replacements densely); obsolete payload files are
// removed AFTER the commit per the removal ordering.
// No whole-session memory mirror is kept; each read streams exactly the
// window's part rows and payload files.
//
// Acknowledgement boundary (declared): every mutation COMMITs its one
// metadata transaction before the call returns (after its payload writes);
// journal_mode=WAL + synchronous=NORMAL hands committed data to the OS
// without an fsync — the OS-flush ack tier. NO power-loss or fsync-durability
// claim is made for either file; SQLite WAL gives process-crash recovery for
// the metadata: acknowledged (committed) records replay identically; an
// unacknowledged mid-transaction mutation persists as before-state or fully
// committed after-state (with its payload files present, or unreferenced
// after the sweep); recovery never invents or reorders records.

public sealed class SqliteExternalTranscriptStore : ITranscriptStore, IStoreStatistics
{
    /// <summary>
    /// Leading-prefix character cap stored in part rows' preview column.
    /// Preview requests with previewChars &le; this cap are answered purely
    /// from metadata (payload files are not opened on this path); larger
    /// requests fall back to reading the single referenced payload file.
    /// </summary>
    internal const int PreviewStoredPrefix = 256;

    private SqliteConnection? _connection;

    public StoreDescriptor Describe() => new(
        "SqliteExternalStore",
        AckTier.OsFlush,
        "metadata db: journal_mode=WAL; synchronous=NORMAL; wal_autocheckpoint=1000 (SQLite default); " +
            "every mutation is one metadata transaction COMMIT-ed before the call returns, written AFTER its " +
            "payload files (write + OS-flush first, commit second); payload removals happen AFTER the removal " +
            "commit; Open() sweeps unreferenced payload files before serving",
        "Family 7 SQLite-metadata + external-payload layout (Microsoft.Data.Sqlite 10.0.14, metadata single " +
            "database; per-part immutable payload files payloads/pl-<guid hex> addressed by safe generated IDs, " +
            "pointers recorded in SQLite). Two-store ordering, explicitly declared: publish = payload write+flush " +
            "then metadata commit (a kill leaves an unreferenced payload, never a published pointer without a " +
            "payload); removal = metadata commit then payload deletion (a kill leaves an unreferenced payload); " +
            "Open() orphan sweep reclaims unreferenced payloads before serving; referenced-missing payloads are not " +
            "producible by the store and fail loudly if observed. Write-time bounded previews (256 code units) make " +
            "the default preview path metadata-only; beyond-cap requests read the single referenced payload file. " +
            "StreamDraft = one committed persisted batch per call in SQLite. Suffix edits are atomic in the " +
            "database (delete + dense reinsert in one transaction), obsolete payloads removed after the commit. " +
            "Recovery: SQLite WAL/rollback for metadata; no cross-file transaction and no power-loss claim.");

    public void Open(string storeRoot, string attachmentRoot)
    {
        // attachmentRoot is harness-owned; this store persists only stable
        // image reference records and never touches attachment files.
        NativeDependency.EnsureResolved();
        Directory.CreateDirectory(storeRoot);
        Directory.CreateDirectory(PayloadsDir(storeRoot));
        _connection?.Dispose();
        _connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(storeRoot, "transcript.db") }.ToString());
        _connection.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec("PRAGMA foreign_keys=OFF"); // single-file store; reference moves are explicit SQL, not FKs
        Exec(SchemaSql);
        SweepUnreferencedPayloads();
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
            "SELECT m.ordinal, m.role, p.kind, p.payload_id, p.tool_call_id, p.tool_name, p.image_id " +
            "FROM (" +
            "  SELECT ordinal FROM messages WHERE session_id = @s ORDER BY ordinal DESC LIMIT @n" +
            ") win " +
            "JOIN messages m ON m.session_id = @s AND m.ordinal = win.ordinal " +
            "JOIN parts p ON p.session_id = @s AND p.ordinal = m.ordinal " +
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
            "SELECT m.ordinal, m.role, p.kind, p.payload_id, p.tool_call_id, p.tool_name, p.image_id " +
            "FROM messages m " +
            "JOIN parts p ON p.session_id = m.session_id AND p.ordinal = m.ordinal " +
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
        // default path; payload files are never opened there.
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
        var fallback = new List<(int Index, long Ordinal)>();
        void CompleteCurrent()
        {
            if (activeOrdinal < 0) return;
            foreach (var (index, fallbackOrdinal) in fallback)
            {
                // Fallback = read ONE referenced payload file (documented
                // bound: the only preview path that touches payload bytes).
                var content = PayloadText(PayloadIdOf(sessionId, fallbackOrdinal, index));
                parts[index] = parts[index] with
                {
                    Preview = content[..Math.Min(content.Length, Math.Max(previewChars, 0))],
                };
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
                fallback = [];
            }
            var kind = reader.GetString(2);
            var contentChars = reader.GetInt64(3);
            var toolCallId = reader.IsDBNull(4) ? null : reader.GetString(4);
            var preview = reader.GetString(5);
            var bounded = reader.GetBoolean(6);
            string text;
            if (bounded && previewChars > preview.Length)
            {
                fallback.Add((parts.Count, ordinal));
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
        // Targeted read: one part row + its payload file, nothing else opened.
        return PayloadText(PayloadOf(sessionId, ordinal, partIndex, "toolResult"));
    }

    // ---- mutations (payload ordering: write first; commit second; delete after) ----

    public long AppendMessage(string sessionId, MessageRecord message)
    {
        using var tx = Begin();
        var ordinal = InsertMessage(sessionId, message, tx);
        tx.Commit(); // payload files were written + flushed before this commit
        return ordinal;
    }

    public void StreamDraft(string sessionId, string streamId, string chunk)
    {
        // One incremental persisted batch per call, in SQLite. Identity rules
        // match the reference: one live draft per session; a live streamId
        // cannot move to another session; the first call registers identity
        // even for an empty chunk.
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
        List<string> obsoletePayloads;
        using (var tx = Begin())
        {
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
            // Collect the removed parts' payload pointers BEFORE the rows go
            // (payload_id subselect on the parts primary key; files themselves
            // are removed only after the commit — the declared removal order).
            obsoletePayloads = PayloadIds(tx,
                "SELECT payload_id FROM parts WHERE session_id = @s AND ordinal >= @from", sessionId, fromOrdinal);
            ExecIn(tx, "DELETE FROM parts WHERE session_id = @s AND ordinal >= @from", sessionId, fromOrdinal);
            ExecIn(tx, "DELETE FROM messages WHERE session_id = @s AND ordinal >= @from", sessionId, fromOrdinal);
            foreach (var message in newMessages)
            {
                InsertMessage(sessionId, message, tx);
            }
            tx.Commit();
        }
        DeletePayloads(obsoletePayloads); // after the commit, per the declared ordering
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
        List<string> obsoletePayloads;
        using (var tx = Begin())
        {
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
            obsoletePayloads = PayloadIds(tx,
                "SELECT payload_id FROM parts WHERE session_id = @s", sessionId);
            // Final closure: parts, messages, drafts and the session row,
            // atomically. Settled/cancelled assignment records stay (history;
            // no longer dangling).
            ExecIn(tx, "DELETE FROM parts WHERE session_id = @s", sessionId);
            ExecIn(tx, "DELETE FROM messages WHERE session_id = @s", sessionId);
            ExecIn(tx, "DELETE FROM streams WHERE session_id = @s", sessionId);
            ExecIn(tx, "DELETE FROM sessions WHERE session_id = @s", sessionId);
            tx.Commit();
        }
        DeletePayloads(obsoletePayloads); // after the commit, per the declared ordering
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
          role       TEXT NOT NULL,       -- header only; content lives in payload files
          PRIMARY KEY (session_id, ordinal)
        );
        CREATE TABLE IF NOT EXISTS parts (
          session_id    TEXT NOT NULL,
          ordinal       INTEGER NOT NULL,
          part_index    INTEGER NOT NULL,
          kind          TEXT NOT NULL,     -- text/toolCall/toolResult/image
          payload_id    TEXT NOT NULL,     -- safe generated ID; external immutable file payload
          tool_call_id  TEXT,              -- toolCall/toolResult only
          tool_name     TEXT,              -- toolCall only
          image_id      TEXT,              -- image only
          content_chars INTEGER NOT NULL,  -- full-content character length (write-time)
          preview       TEXT NOT NULL,     -- bounded leading prefix (<= PreviewStoredPrefix)
          bounded       INTEGER NOT NULL,  -- 1 when preview cut at the stored cap
          PRIMARY KEY (session_id, ordinal, part_index)
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

    private string PayloadsDir(string? storeRoot = null)
    {
        var root = storeRoot ?? Path.GetDirectoryName(Db.DataSource)!;
        return Path.Combine(root, "payloads");
    }

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

    /// <summary>Safe generated payload ID: never derived from session or user
    /// content (no dedup either); immutable once published — the file is
    /// written exactly once and never rewritten.</summary>
    private static string NewPayloadId() => $"pl-{Guid.NewGuid():N}";

    /// <summary>PUBLICATION step 1 (before the metadata commit): write the
    /// payload file fully and hand it to the OS (OS-flush tier). A failure or
    /// kill before publication leaves an unreferenced file for the startup
    /// sweep; a published metadata pointer can never lack its payload file.</summary>
    private static void PublishPayload(string payloadsDir, string payloadId, string content)
    {
        var path = Path.Combine(payloadsDir, payloadId);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
        writer.Flush(); // to the OS; no fsync is claimed
    }

    /// <summary>REMOVAL step (after the metadata commit): delete obsolete
    /// payload files; existence-tolerant and idempotent — a failure or kill
    /// before deletion leaves an unreferenced file for the startup sweep.</summary>
    private void DeletePayloads(IReadOnlyList<string> payloadIds)
    {
        if (payloadIds.Count == 0) return;
        var dir = PayloadsDir();
        foreach (var payloadId in payloadIds)
        {
            try
            {
                File.Delete(Path.Combine(dir, payloadId));
            }
            catch (IOException)
            {
                // Reclaimed by the next Open() orphan sweep.
            }
        }
    }

    /// <summary>STARTUP ORPHAN SWEEP: unreferenced payload files exist only in
    /// the two declared crash states (published-but-uncommitted,
    /// removed-committed-but-not-yet-deleted). They are dead weight only, so
    /// the sweep deletes them before serving; no background GC. A live
    /// payload behind a committed part row is never touched.</summary>
    private void SweepUnreferencedPayloads()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        using (var command = Command("SELECT payload_id FROM parts"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) referenced.Add(reader.GetString(0));
        }
        var dir = PayloadsDir();
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (!referenced.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
            }
        }
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
        var payloadsDir = PayloadsDir();
        for (var i = 0; i < message.Parts.Count; i++)
        {
            var part = message.Parts[i];
            var (kind, toolCallId, toolName, imageId) = PartOf(part);
            var content = ContentOf(part);
            // PUBLICATION ORDER: payload file fully written + flushed here,
            // BEFORE the transaction that carries the referencing row commits.
            var payloadId = NewPayloadId();
            PublishPayload(payloadsDir, payloadId, content);
            var cut = Math.Min(content.Length, PreviewStoredPrefix);
            using var meta = Command(
                "INSERT INTO parts(session_id, ordinal, part_index, kind, payload_id, tool_call_id, tool_name, image_id, content_chars, preview, bounded) " +
                "VALUES(@s, @ord, @i, @kind, @p, @tc, @tn, @ii, @chars, @preview, @bounded)", tx);
            meta.Parameters.AddWithValue("@s", sessionId);
            meta.Parameters.AddWithValue("@ord", ordinal);
            meta.Parameters.AddWithValue("@i", (long)i);
            meta.Parameters.AddWithValue("@kind", kind);
            meta.Parameters.AddWithValue("@p", payloadId);
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
    /// store their relative path (oracle-conform mapping).</summary>
    private static string ContentOf(MessagePart part) => part switch
    {
        TextPart t => t.Content,
        ToolCallPart c => c.ArgsJson,
        ToolResultPart r => r.Content,
        ImagePart i => i.RelativePath,
        _ => string.Empty,
    };

    private string PayloadIdOf(string sessionId, long ordinal, int partIndex)
    {
        using var command = Command(
            "SELECT payload_id FROM parts WHERE session_id = @s AND ordinal = @ord AND part_index = @pi");
        command.Parameters.AddWithValue("@s", sessionId);
        command.Parameters.AddWithValue("@ord", ordinal);
        command.Parameters.AddWithValue("@pi", (long)partIndex);
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException($"Missing part {partIndex} in {sessionId}@{ordinal}");
    }

    private string PayloadOf(string sessionId, long ordinal, int partIndex, string kind)
    {
        using var command = Command(
            "SELECT payload_id FROM parts " +
            "WHERE session_id = @s AND ordinal = @ord AND part_index = @pi AND kind = @kind");
        command.Parameters.AddWithValue("@s", sessionId);
        command.Parameters.AddWithValue("@ord", ordinal);
        command.Parameters.AddWithValue("@pi", (long)partIndex);
        command.Parameters.AddWithValue("@kind", kind);
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException(
                $"No {kind} part {partIndex} in {sessionId}@{ordinal}");
    }

    /// <summary>Reads ONE referenced payload file. Missing files behind a
    /// committed pointer are never produced by the store under the declared
    /// ordering; they surface loudly as read failures (external interference).</summary>
    private string PayloadText(string payloadId)
    {
        try
        {
            return File.ReadAllText(Path.Combine(PayloadsDir(), payloadId), Encoding.UTF8);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Committed payload {payloadId} missing or unreadable (not producible by this store's ordering)", ex);
        }
    }

    private static List<string> PayloadIds(
        SqliteTransaction tx, string sql, string sessionId, long from = long.MinValue)
    {
        using var command = tx.Connection!.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@s", sessionId);
        if (from != long.MinValue) command.Parameters.AddWithValue("@from", from);
        var ids = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

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
            var payloadId = reader.GetString(3);
            var toolCallId = reader.IsDBNull(4) ? null : reader.GetString(4);
            var toolName = reader.IsDBNull(5) ? null : reader.GetString(5);
            var imageId = reader.IsDBNull(6) ? null : reader.GetString(6);
            var content = PayloadText(payloadId);
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