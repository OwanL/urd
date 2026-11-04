using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Urd.TranscriptHarness.Adapter.SqliteRows;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.SqlitePerSession;

// Family-8 adapter: PER-SESSION SQLite files + one shared coordination DB.
//
// Layout (declared, per STORAGE-EXPERIMENT.md family 8):
//   <storeRoot>/coordination.db              — assignments ONLY + deletion-intent
//                                              markers. Authority for assignment
//                                              records (including their history
//                                              after a target session was deleted).
//   <storeRoot>/sessions/s-<esc>.<h16>/      — one small directory per session:
//     transcript.db (+ .db-wal/.db-shm)       transcript content for that session.
//
// Session-internal storage reuses the rows adapter (SqliteRowsTranscriptStore,
// project reference — not duplicated): each session file is one rows store
// instance with its own transcript.db, so all session-internal operations are
// ordinary transactional row deletes/inserts (append, per-call persisted
// stream batches, finalize, suffix delete+insert inside one transaction,
// bounded previews, targeted fetch). This adapter adds only file placement,
// the coordination DB, lazy open/remove and the documented deletion protocol.
//
// Session directory names: "s-" + Uri.EscapeDataString(id) + "." + first-16-hex
// of SHA-256(id). URI escaping is injective (unsafe/reserved path bytes are
// percent-encoded and '%' itself is escaped, so no encode/decode confusion);
// on case-insensitive filesystems ids differing only in case would share an
// escaped base name, so the id-hash disambiguates; the full id also lives
// inside the file. The "s-" prefix keeps Windows-device-style ids (CON, AUX)
// out of the reserved-name trap. Session ids must remain short enough for a
// legal file path (documented bound; harness ids qualify).
//
// Cross-store authority (no fictitious cross-db atomicity):
//   - assignments: the coordinator serves every assignment ever created and
//     survives target-session deletion;
//   - session existence/counts/content: the per-session file itself;
//   - deletion intent: coordinator pending_deletions rows.
//
// Deletion protocol (each step one durable commit):
//   1. coordinator transaction: enforce the target-session invariant
//      (RequireSettled fails while open assignments exist; CancelOpen cancels
//      them) and INSERT the intent marker — atomically;
//   2. filesystem (a filesystem action, not a cross-db transaction): dispose
//      the open session instance, then File.Delete transcript.db and its
//      -wal/-shm companions (existence-tolerant);
//   3. coordinator transaction: DELETE the intent marker.
//   A kill in flight leaves exactly one recoverable state: committed marker
//   with (partially removed) files behind. Open() recovery scans
//   pending_deletions, removes leftovers and clears markers; the marker never
//   outlives the file removal. Conversely any mutation to a session clears a
//   stale marker BEFORE its file is created, so recovered removals can never
//   delete acknowledged content of a later-created session.
//
// Recovery limits (declared honestly):
//   - unacknowledged mutations inside one session file recover by that file's
//     WAL (before-state or committed after-state, never partial);
//   - interrupted deletions resume at Open() as above;
//   - NOT claimed: power-loss durability (WAL + NORMAL is the OS-flush tier,
//     no fsync); atomicity across the databases beyond the authorities above.
//
// Acknowledgement boundary: every mutation COMMITs its own transaction
// (session file and/or coordinator) before the call returns;
// journal_mode=WAL + synchronous=NORMAL hands committed data to the OS without
// an fsync — the OS-flush ack tier. NO power-loss or fsync-durability claim.

public sealed class SqlitePerSessionTranscriptStore : ITranscriptStore, IStoreStatistics
{
    private const string TranscriptDb = "transcript.db";

    private SqliteConnection? _coordinator;
    private readonly Dictionary<string, SqliteRowsTranscriptStore> _sessions = new(StringComparer.Ordinal);

    public StoreDescriptor Describe() => new(
        "SqlitePerSessionStore",
        AckTier.OsFlush,
        "coordination.db and each per-session transcript.db: journal_mode=WAL; synchronous=NORMAL; " +
            "wal_autocheckpoint=1000 (SQLite default); every mutation is one transaction COMMIT-ed before the call " +
            "returns; session deletion = coordinator intent-marker commit, then transcript.db/-wal/-shm removal, " +
            "then marker clear (each step its own commit; Open() completes interrupted removals)",
        "Family 8: one small SQLite file per session (session-internal storage is the shared rows adapter's " +
            "transactional row layout: append; one persisted batch per StreamDraft call; finalize; suffix " +
            "delete+insert in one transaction; bounded write-time previews; targeted fetch) plus one shared " +
            "coordination.db holding assignments (authority for assignment records, served across target-session " +
            "deletion) and pending_deletions intent markers (authority for recoverable deletion state). Per-session " +
            "file presence is the authority for session existence/counts/content; WAL is not assumed atomic across " +
            "databases. Recovery: per-file WAL/rollback on reopen; interrupted deletion completed at Open() before " +
            "serving; acknowledged records replay identically; no power-loss claim; session ids bounded by file-path " +
            "length constraints.");

    public void Open(string storeRoot, string attachmentRoot)
    {
        // attachmentRoot is harness-owned; stores persist only stable image
        // reference records.
        NativeDependency.EnsureResolved();
        // Microsoft.Data.Sqlite pools CLOSED connections by file name and the
        // pooled native handles keep deleted database files locked; purge idle
        // pooled handles before any per-session file removal below (only idle
        // connections are affected; active ones are unaffected).
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(storeRoot);
        CloseCoordinator();
        _coordinator = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(storeRoot, "coordination.db"), Pooling = false }.ToString());
        _coordinator.Open();
        Directory.CreateDirectory(SessionsRoot);
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA synchronous=NORMAL");
        Exec(SchemaSql);

        // Recoverable-deletion recovery: complete every pending removal (file
        // and companions may already be gone) before serving anything.
        foreach (var sessionId in PendingDeletionIds().ToArray())
        {
            RemoveSessionFiles(sessionId);
            ClearPendingDeletion(sessionId);
        }
    }

    // ---- reads (per-session authority; the coordinator is not consulted) ----

    public SessionMeta[] ListSessionsMeta()
    {
        var metas = new List<SessionMeta>();
        var pending = PendingDeletionIds().ToHashSet(StringComparer.Ordinal);
        foreach (var dir in Directory.EnumerateDirectories(SessionsRoot, "s-*"))
        {
            var dbPath = Path.Combine(dir, TranscriptDb);
            if (!File.Exists(dbPath)) continue;
            var (sessionId, count) = ReadSessionSummary(dbPath);
            if (sessionId is null) continue; // phantom file (schema-only, e.g. killed first stream)
            if (pending.Contains(sessionId)) continue; // deletion in flight
            metas.Add(new SessionMeta(sessionId, count));
        }
        return [.. metas.OrderBy(m => m.SessionId, StringComparer.Ordinal)];
    }

    public MessageRecord[] OpenRecent(string sessionId, int count)
    {
        if (count <= 0 || !SessionFileExists(sessionId)) return [];
        return Store(sessionId).OpenRecent(sessionId, count);
    }

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count)
    {
        if (count <= 0 || !SessionFileExists(sessionId)) return [];
        return Store(sessionId).PageOlder(sessionId, beforeOrdinal, count);
    }

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive)
    {
        if (!SessionFileExists(sessionId)) return [];
        return Store(sessionId).ReadRange(sessionId, fromOrdinal, toOrdinalExclusive);
    }

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars)
    {
        if (count <= 0 || !SessionFileExists(sessionId))
        {
            return new WindowPreview(sessionId, 0, []);
        }
        return Store(sessionId).OpenRecentPreview(sessionId, count, previewChars);
    }

    public string FetchToolResult(string sessionId, long ordinal, int partIndex)
    {
        if (!SessionFileExists(sessionId))
        {
            throw new InvalidOperationException($"No tool result part {partIndex} in {sessionId}@{ordinal}");
        }
        return Store(sessionId).FetchToolResult(sessionId, ordinal, partIndex);
    }

    // ---- mutations ----

    public long AppendMessage(string sessionId, MessageRecord message)
    {
        ClearStalePendingDeletion(sessionId);
        return Store(sessionId).AppendMessage(sessionId, message);
    }

    public void StreamDraft(string sessionId, string streamId, string chunk)
    {
        ClearStalePendingDeletion(sessionId);
        Store(sessionId).StreamDraft(sessionId, streamId, chunk);
    }

    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage)
    {
        ClearStalePendingDeletion(sessionId);
        return Store(sessionId).FinalizeStream(sessionId, streamId, finalMessage);
    }

    public long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages)
    {
        if (!SessionExists(sessionId))
        {
            throw new InvalidOperationException($"Unknown session {sessionId}");
        }
        return Store(sessionId).ReplaceSuffix(sessionId, fromOrdinal, newMessages);
    }

    public void CreateAssignment(AssignmentRecord assignment)
    {
        using var tx = Coordinator.BeginTransaction();
        if (RowExists(tx, "SELECT 1 FROM assignments WHERE assignment_id = @id", assignment.AssignmentId))
        {
            throw new InvalidOperationException($"Duplicate assignment id {assignment.AssignmentId}");
        }
        using var command = CoordinatorCommand(
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
        using var tx = Coordinator.BeginTransaction();
        if (!RowExists(tx, "SELECT 1 FROM assignments WHERE assignment_id = @id", request.AssignmentId))
        {
            throw new InvalidOperationException($"Unknown assignment {request.AssignmentId}");
        }
        using var command = CoordinatorCommand(
            "UPDATE assignments SET status = @status, result_text = @result WHERE assignment_id = @id", tx);
        command.Parameters.AddWithValue("@id", request.AssignmentId);
        command.Parameters.AddWithValue("@status", (long)AssignmentStatus.Settled);
        command.Parameters.AddWithValue("@result", request.ResultText);
        command.ExecuteNonQuery();
        tx.Commit();
    }

    public AssignmentRecord GetAssignment(string assignmentId)
    {
        using var command = CoordinatorCommand(
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
        if (!SessionExists(sessionId))
        {
            throw new InvalidOperationException($"DeleteSession of unknown session {sessionId}");
        }
        // Step 1 (one coordinator transaction): invariant + intent marker.
        using (var tx = Coordinator.BeginTransaction())
        {
            long openCount;
            using (var open = CoordinatorCommand(
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
                using var cancel = CoordinatorCommand(
                    "UPDATE assignments SET status = @cancelled WHERE target_session_id = @s AND status = @open", tx);
                cancel.Parameters.AddWithValue("@s", sessionId);
                cancel.Parameters.AddWithValue("@cancelled", (long)AssignmentStatus.Cancelled);
                cancel.Parameters.AddWithValue("@open", (long)AssignmentStatus.Open);
                cancel.ExecuteNonQuery();
            }
            using var intent = CoordinatorCommand("INSERT INTO pending_deletions(session_id) VALUES(@s)", tx);
            intent.Parameters.AddWithValue("@s", sessionId);
            intent.ExecuteNonQuery();
            tx.Commit();
        }
        // Step 2 (filesystem; not a cross-db transaction): close any open
        // instance of the session, purge idle pooled handles (Microsoft.Data
        // .Sqlite pools closed connections by file name and pooled native
        // handles would keep transcript.db locked), then remove the file +
        // WAL/SHM companions.
        if (_sessions.Remove(sessionId, out var openStore))
        {
            openStore.Dispose();
        }
        SqliteConnection.ClearAllPools();
        RemoveSessionFiles(sessionId);
        // Step 3 (one coordinator transaction): marker clear after full removal.
        ClearPendingDeletion(sessionId);
    }

    public StoreStatistics Sample() => new(GC.GetTotalAllocatedBytes(precise: false));

    public void Dispose()
    {
        foreach (var store in _sessions.Values) store.Dispose();
        _sessions.Clear();
        CloseCoordinator();
    }

    // ---- internals ----

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS assignments (
          assignment_id     TEXT PRIMARY KEY,
          target_session_id TEXT NOT NULL,
          title             TEXT NOT NULL,
          status            INTEGER NOT NULL, -- 0 open / 1 settled / 2 cancelled
          result_text       TEXT
        );
        CREATE TABLE IF NOT EXISTS pending_deletions (
          session_id TEXT PRIMARY KEY       -- committed deletion intent, completed at Open()
        );
        """;

    private string StoreRoot => _coordinator is null
        ? throw new InvalidOperationException("Store is not open (Open() was not called)")
        : Path.GetDirectoryName(_coordinator.DataSource)!;

    private SqliteConnection Coordinator => _coordinator
        ?? throw new InvalidOperationException("Store is not open (Open() was not called)");

    private string SessionsRoot => Path.Combine(StoreRoot, "sessions");

    private SqliteCommand CoordinatorCommand(string sql, SqliteTransaction? tx = null)
    {
        var command = Coordinator.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }

    private void Exec(string sql)
    {
        using var command = CoordinatorCommand(sql);
        command.ExecuteNonQuery();
    }

    private static string SessionDirName(string sessionId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId));
        var hash = Convert.ToHexString(bytes[..8]).ToLowerInvariant();
        return $"s-{Uri.EscapeDataString(sessionId)}.{hash}";
    }

    private string SessionDir(string sessionId) => Path.Combine(SessionsRoot, SessionDirName(sessionId));

    private bool SessionFileExists(string sessionId) =>
        File.Exists(Path.Combine(SessionDir(sessionId), TranscriptDb));

    /// <summary>Real session existence: a sessions row inside the file (files
    /// with only schema are phantoms — e.g. from a killed first stream — and
    /// are neither served nor deletable; they remain inert).</summary>
    private bool SessionExists(string sessionId) =>
        SessionFileExists(sessionId) && Store(sessionId).ListSessionsMeta().Length == 1;

    private SqliteRowsTranscriptStore Store(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var existing)) return existing;
        var dir = SessionDir(sessionId);
        Directory.CreateDirectory(dir);
        var store = new SqliteRowsTranscriptStore();
        store.Open(dir, Path.Combine(dir, "att"));
        _sessions[sessionId] = store;
        return store;
    }

    /// <summary>One light direct read per file during listing: id + dense count
    /// straight from the session file (the two authority tables); no pooled
    /// instances are opened for a listing. Sessions-table row absent = phantom
    /// file, skipped. MAX(ordinal)+1 is the dense count (rows-adapter parity).</summary>
    private static (string? SessionId, long Count) ReadSessionSummary(string dbPath)
    {
        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT (SELECT session_id FROM sessions LIMIT 1), " +
                "(SELECT COALESCE(MAX(ordinal) + 1, 0) FROM messages)";
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return (null, 0);
            var id = reader.IsDBNull(0) ? null : reader.GetString(0);
            return (id, reader.GetInt64(1));
        }
        catch (SqliteException)
        {
            return (null, 0);
        }
    }

    private IEnumerable<string> PendingDeletionIds()
    {
        using var command = CoordinatorCommand("SELECT session_id FROM pending_deletions");
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return reader.GetString(0);
    }

    /// <summary>Defense for the create-vs-delete race: a mutation to a session
    /// clears a stale intent marker BEFORE any file is created, so recovered
    /// removals can never remove acknowledged content of a later session.</summary>
    private void ClearStalePendingDeletion(string sessionId)
    {
        if (PendingDeletionIds().Contains(sessionId))
        {
            ClearPendingDeletion(sessionId);
        }
    }

    private void ClearPendingDeletion(string sessionId)
    {
        using var command = CoordinatorCommand("DELETE FROM pending_deletions WHERE session_id = @s");
        command.Parameters.AddWithValue("@s", sessionId);
        command.ExecuteNonQuery();
    }

    /// <summary>Filesystem removal of one session's transcript.db and its
    /// WAL/SHM companions; existence-tolerant so interrupted removals resume;
    /// the directory entry is removed best-effort.</summary>
    private void RemoveSessionFiles(string sessionId)
    {
        var dir = SessionDir(sessionId);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = Path.Combine(dir, TranscriptDb + suffix);
            if (File.Exists(path)) File.Delete(path);
        }
        try { Directory.Delete(dir, recursive: false); } catch (IOException) { /* best effort */ }
    }

    private static bool RowExists(SqliteTransaction tx, string sql, string value)
    {
        var connection = tx.Connection ?? throw new InvalidOperationException("Transaction has no connection");
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", value);
        command.Parameters.AddWithValue("@s", value);
        return command.ExecuteScalar() is not null;
    }

    private void CloseCoordinator()
    {
        _coordinator?.Dispose();
        _coordinator = null;
    }
}