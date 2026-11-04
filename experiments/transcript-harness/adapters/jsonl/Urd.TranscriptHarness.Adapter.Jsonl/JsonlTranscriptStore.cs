using Microsoft.Data.Sqlite;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.JsonlStore;

/// <summary>
/// Family 10: bounded-read JSONL reference (hybrid layout).
///
/// One JSONL file per session holds the transcript records, one message per
/// line; a small SQLite index database (Microsoft.Data.Sqlite, pinned
/// 10.0.14) is the coordination layer: per-message byte offset + length +
/// preview metadata rows, authoritative session counts + file pointers,
/// assignments plus the assignment-target index, and stream state. Reads
/// fetch single lines through persisted byte offsets — never a whole-session
/// memory mirror, and the preview path decodes only persisted short metadata,
/// never large content.
///
/// Durability ordering (declared): the message line is written and flushed to
/// OS buffers BEFORE the SQLite index transaction; a mutation is acknowledged
/// only once the index transaction commits. The tier is OsFlush — bounded by
/// the weakest stage: the JSONL data hand-off is OS-buffered (no fsync), even
/// though SQLite commits with its default journal mode and synchronous
/// settings. A process kill therefore leaves either the before-state or the
/// after-state: unindexed file tails are truncated during Open (recovery by
/// content, never by trusting the index alone), so an interrupted data stage
/// is never served.
///
/// No cross-file atomicity is claimed. Edits are generation rewrites: the new
/// generation file is written completely (byte-identical prefix copy plus the
/// appended replacements), then the metadata pointer (file + offsets + count)
/// is published in one SQLite transaction; the superseded file is deleted
/// best-effort afterwards. An interrupted rewrite leaves an unreferenced
/// orphan file that later Open passes delete; orphans are cleaned, never
/// served, and never counted as state.
/// </summary>
public sealed class JsonlTranscriptStore : ITranscriptStore
{
    public const string StoreName = "JsonlOffsetIndex";
    private const string IndexDbName = "index.db";
    private const string SessionsDirName = "sessions";

    private string _storeRoot = string.Empty;
    private string _sessionsRoot = string.Empty;
    private SqliteConnection? _db;
    private SqliteTransaction? _openTx;

    public StoreDescriptor Describe() => new(
        StoreName,
        AckTier.OsFlush,
        "JSONL message lines: FileStream.Write + Flush() only (OS buffer cache, NO fsync; no FileOptions.WriteThrough; " +
        "no random-access-flush API exists for .NET streams). SQLite index: Microsoft.Data.Sqlite 10.0.14 with all defaults " +
        "(journal_mode=DELETE rollback journal, synchronous=FULL), so each index transaction commit fsyncs its own journal; " +
        "the declared ack tier is still OsFlush because the JSONL data stage is the weakest link and is never fsynced. " +
        "Commit ordering per mutation: JSONL line(s) written and flushed FIRST, SQLite index transaction SECOND, " +
        "and the mutation is acknowledged only after the transaction commits. No flush/WAL/synchronous overrides anywhere.",
        "Layout: <storeRoot>/index.db — SQLite coordination (session pointer rows: count/generation/file/bytes, per-message " +
        "offset/length/role/preview rows, assignments + assignment-target index, stream marker and batch rows) — plus " +
        "sessions/<hex(name)>/messages-<gen>.jsonl, one message record per line. Reads fetch exactly the requested window " +
        "through persisted byte offsets; there is no whole-session memory mirror. The preview index stores per-part kind, " +
        "toolCallId, full content character length (UTF-16 code units — the same unit the harness compares with " +
        "string.Length and [..n] slicing) and a bounded preview string (64 code units) computed at write time; preview " +
        "requests beyond that cap fall back to bounded per-row reads of only the requested window. Durability: the JSONL " +
        "data stage is OS-buffered (NO fsync) and is flushed before the SQLite index transaction commits; the tier is " +
        "OsFlush for that reason. StreamDraft persists one " +
        "SQLite transaction per call (no call is buffered until finalize); FinalizeStream appends the message line, then " +
        "commits the message row + count + marker and batch deletion in one transaction, so a finalized stream is either " +
        "fully inserted or absent. Edits are generation rewrites with full prefix byte-copy cost (O(session bytes); no " +
        "in-place edit), the pointer is published atomically in SQLite after the new file is complete, and NO cross-file " +
        "atomicity is claimed — a crash mid-rewrite leaves an unreferenced orphan file, deleted by the next Open. Orphan " +
        "cleanup at Open: unindexed JSONL tails are truncated to the indexed byte length, unreferenced session directories " +
        "and unreferenced files inside session directories are deleted; a referenced file that is missing or shorter than " +
        "the index reports is corruption and fails loudly. DeleteSession commits catalog/assignment-index/stream removal " +
        "atomically in SQLite under the RequireSettled / CancelOpen policies (cancelled assignment records survive; no " +
        "dangling open references), then deletes the transcript files best-effort — a crash before that leaves orphans " +
        "that the next Open reclaims.");

    public void Open(string storeRoot, string attachmentRoot)
    {
        if (_db is not null) throw new InvalidOperationException("Store already open");
        try
        {
            _storeRoot = storeRoot;
            _sessionsRoot = Path.Combine(storeRoot, SessionsDirName);
            Directory.CreateDirectory(_sessionsRoot);
            _db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_storeRoot, IndexDbName),
                Pooling = false, // Dispose must release index.db (tests delete the store root)
            }.ToString());
            _db.Open();
            foreach (var ddl in SchemaStatements)
            {
                Execute(ddl);
            }
            RecoverAndSweep();
        }
        catch
        {
            _db?.Dispose();
            _db = null;
            throw;
        }
    }

    /// <summary>One SQLite DDL statement per command: Microsoft.Data.Sqlite does not run multi-statement scripts.</summary>
    private static readonly string[] SchemaStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS sessions(
            name TEXT PRIMARY KEY, count INTEGER NOT NULL,
            generation INTEGER NOT NULL, file TEXT NOT NULL, bytes INTEGER NOT NULL)
        """,
        """
        CREATE TABLE IF NOT EXISTS messages(
            session TEXT NOT NULL, ordinal INTEGER NOT NULL, offset INTEGER NOT NULL,
            length INTEGER NOT NULL, role TEXT NOT NULL, meta TEXT NOT NULL,
            PRIMARY KEY(session, ordinal))
        """,
        """
        CREATE TABLE IF NOT EXISTS assignments(
            id TEXT PRIMARY KEY, target TEXT NOT NULL, title TEXT NOT NULL,
            status INTEGER NOT NULL, result TEXT)
        """,
        """
        CREATE TABLE IF NOT EXISTS assign_index(
            target TEXT NOT NULL, id TEXT NOT NULL, status INTEGER NOT NULL,
            PRIMARY KEY(target, id))
        """,
        "CREATE TABLE IF NOT EXISTS draft_marker(session TEXT PRIMARY KEY, stream TEXT NOT NULL, last_seq INTEGER NOT NULL)",
        """
        CREATE TABLE IF NOT EXISTS draft_batch(
            session TEXT NOT NULL, stream TEXT NOT NULL, seq INTEGER NOT NULL, chunk TEXT NOT NULL,
            PRIMARY KEY(session, stream, seq))
        """,
    ];

    // ---- recovery ----

    /// <summary>
    /// Recovery by content before any serving: unindexed JSONL tails are
    /// truncated (the indexed prefix is authoritative), then unreferenced
    /// orphans from interrupted rewrites and deleted sessions are removed.
    /// A referenced file that is missing or shorter than the index reports is
    /// corruption, not recovery, and fails loudly.
    /// </summary>
    private void RecoverAndSweep()
    {
        var referencedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<(string Session, string File, long Bytes)>();
        using (var command = _db!.CreateCommand())
        {
            command.CommandText = "SELECT name, file, bytes FROM sessions ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }
        foreach (var (session, file, bytes) in rows)
        {
            var dir = Path.Combine(_sessionsRoot, SessionDirName(session));
            if (!Directory.Exists(dir))
            {
                throw new InvalidOperationException($"Referenced session directory is missing for {session} ({file})");
            }
            var path = Path.Combine(dir, file);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Referenced transcript file is missing for {session} ({file})");
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (stream.Length < bytes)
            {
                throw new InvalidOperationException(
                    $"Transcript file shorter than index ({stream.Length} < {bytes}) for session {session}");
            }
            if (stream.Length > bytes)
            {
                stream.SetLength(bytes); // unindexed tail: an uncommitted data stage is never served
                stream.Flush();
            }
            referencedDirs.Add(SessionDirName(session));
            SweepSessionDir(dir, file);
        }
        foreach (var dir in Directory.EnumerateDirectories(_sessionsRoot))
        {
            if (!referencedDirs.Contains(Path.GetFileName(dir)))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static void SweepSessionDir(string dir, string currentFile)
    {
        foreach (var entry in Directory.EnumerateFiles(dir))
        {
            if (string.Equals(Path.GetFileName(entry), currentFile, StringComparison.Ordinal)) continue;
            try
            {
                File.Delete(entry); // interrupted-rewrite orphan; the next Open pass retries on failure
            }
            catch (IOException)
            {
            }
        }
    }

    // ---- low-level SQLite helpers ----

    private SqliteTransaction Begin()
    {
        var transaction = _db!.BeginTransaction();
        _openTx = transaction;
        return transaction;
    }

    private void CommitTx(SqliteTransaction transaction)
    {
        transaction.Commit();
        _openTx = null;
    }

    private void RollbackTx(SqliteTransaction transaction)
    {
        _openTx = null;
        transaction.Rollback();
    }

    private int Execute(string sql, params (string Name, object Value)[] binds)
    {
        using var command = BindCommand(sql, binds);
        return command.ExecuteNonQuery();
    }

    private int ExecuteScalar(string sql, params (string Name, object Value)[] binds) =>
        Convert.ToInt32(BindCommand(sql, binds).ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);

    private SqliteCommand BindCommand(string sql, params (string Name, object Value)[] binds)
    {
        var command = _db!.CreateCommand();
        command.CommandText = sql;
        if (_openTx is { } transaction)
        {
            command.Transaction = transaction; // Microsoft.Data.Sqlite requires explicit association
        }
        foreach (var (name, value) in binds)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command;
    }

    private int CountOf(string sql, params (string Name, object Value)[] binds)
    {
        using var reader = BindCommand(sql, binds).ExecuteReader();
        reader.Read();
        return reader.GetInt32(0);
    }

    private bool ExistsSession(string sessionId)
    {
        using var reader = BindCommand("SELECT 1 FROM sessions WHERE name = $n", ("$n", sessionId)).ExecuteReader();
        return reader.Read();
    }

    private sealed record SessionPtr(long Count, long Generation, string File, long Bytes);

    private SessionPtr? TrySessionMeta(string sessionId)
    {
        using var reader = BindCommand(
            "SELECT count, generation, file, bytes FROM sessions WHERE name = $n", ("$n", sessionId)).ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return new SessionPtr(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3));
    }

    private sealed record MessageRow(long Ordinal, long Offset, long Length, string Role, string Meta);

    private List<MessageRow> ReadMessageRows(string sessionId, long from, long to)
    {
        var rows = new List<MessageRow>((int)Math.Max(0, to - from));
        if (from >= to) return rows;
        using var reader = BindCommand(
            "SELECT ordinal, offset, length, role, meta FROM messages " +
            "WHERE session = $s AND ordinal >= $f AND ordinal < $t ORDER BY ordinal",
            ("$s", sessionId), ("$f", from), ("$t", to)).ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new MessageRow(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4)));
        }
        return rows;
    }

    private string SessionPath(string sessionId, string? file = null) =>
        Path.Combine(_sessionsRoot, SessionDirName(sessionId), file ?? PublishedFile(sessionId));

    private string PublishedFile(string sessionId) =>
        (TrySessionMeta(sessionId) ?? throw new InvalidOperationException($"Unknown session {sessionId}")).File;

    private static string SessionDirName(string sessionId) => SessionDirs.DirName(sessionId);

    private sealed record Written(long Offset, long Length, string Role, string Meta, long Bytes);

    /// <summary>Data stage: append one line into an open stream; returns the byte range for the index row.</summary>
    private static Written AppendLine(FileStream stream, MessageRecord message)
    {
        var line = MessageJson.EncodeMessage(message);
        var offset = stream.Position;
        stream.Write(line);
        return new Written(offset, line.Length - 1, message.Role, MessageJson.EncodePartsMeta(message.Parts), stream.Position);
    }

    // ---- reads ----

    /// <summary>Decodes the window by fetching each line through its persisted byte offset.</summary>
    private MessageRecord[] ReadWindow(string sessionId, long from, long to, string file)
    {
        var rows = ReadMessageRows(sessionId, from, to);
        if (rows.Count == 0) return [];
        using var stream = new FileStream(
            Path.Combine(_sessionsRoot, SessionDirName(sessionId), file),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        var messages = new List<MessageRecord>(rows.Count);
        foreach (var row in rows)
        {
            var line = new byte[row.Length];
            stream.Seek(row.Offset, SeekOrigin.Begin);
            stream.ReadExactly(line);
            messages.Add(MessageJson.DecodeMessage(line));
        }
        return [.. messages];
    }

    public SessionMeta[] ListSessionsMeta()
    {
        using var reader = BindCommand("SELECT name, count FROM sessions ORDER BY name").ExecuteReader();
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
        var meta = TrySessionMeta(sessionId);
        if (meta is null) return []; // absence is not an error
        var from = Math.Max(0, meta.Count - count);
        return ReadWindow(sessionId, from, meta.Count, meta.File);
    }

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count)
    {
        if (count <= 0) return [];
        var meta = TrySessionMeta(sessionId);
        if (meta is null) return [];
        var to = Math.Clamp(beforeOrdinal, 0, meta.Count);
        var from = Math.Max(0, to - count);
        return ReadWindow(sessionId, from, to, meta.File);
    }

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive)
    {
        var meta = TrySessionMeta(sessionId);
        if (meta is null) return []; // absence is not an error
        if (fromOrdinal < 0 || toOrdinalExclusive < fromOrdinal || toOrdinalExclusive > meta.Count)
        {
            throw new InvalidOperationException(
                $"Range [{fromOrdinal}, {toOrdinalExclusive}) outside count={meta.Count} in {sessionId}");
        }
        return ReadWindow(sessionId, fromOrdinal, toOrdinalExclusive, meta.File);
    }

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars)
    {
        if (previewChars < 0)
        {
            throw new InvalidOperationException("previewChars must be non-negative");
        }
        var meta = TrySessionMeta(sessionId);
        if (meta is null) return new WindowPreview(sessionId, 0, []);
        var newest = Math.Min(count, meta.Count);
        var from = meta.Count - newest;
        var rows = ReadMessageRows(sessionId, from, meta.Count);
        if (previewChars <= MessageJson.PreviewCap)
        {
            // Persisted metadata path: no large content is decoded at all.
            var items = new List<MessagePreview>(rows.Count);
            foreach (var row in rows)
            {
                items.Add(new MessagePreview(row.Ordinal, row.Role, MessageJson.DecodePartsMeta(row.Meta, previewChars)));
            }
            return new WindowPreview(sessionId, from, items);
        }
        // Bounded per-row fallback: only the requested window is read and decoded.
        using var stream = new FileStream(
            Path.Combine(_sessionsRoot, SessionDirName(sessionId), meta.File),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        var fullItems = new List<MessagePreview>(rows.Count);
        foreach (var row in rows)
        {
            var line = new byte[row.Length];
            stream.Seek(row.Offset, SeekOrigin.Begin);
            stream.ReadExactly(line);
            var message = MessageJson.DecodeMessage(line);
            fullItems.Add(new MessagePreview(
                row.Ordinal, row.Role, MessageJson.DecodePartsMeta(row.Meta, previewChars, message.Parts)));
        }
        return new WindowPreview(sessionId, from, fullItems);
    }

    /// <summary>Targeted single-result read: exactly one message line is fetched and decoded.</summary>
    public string FetchToolResult(string sessionId, long ordinal, int partIndex)
    {
        var meta = TrySessionMeta(sessionId);
        if (meta is null || ordinal < 0 || ordinal >= meta.Count)
        {
            throw new InvalidOperationException($"No message {sessionId}@{ordinal}");
        }
        using var reader = BindCommand(
            "SELECT offset, length FROM messages WHERE session = $s AND ordinal = $o",
            ("$s", sessionId), ("$o", ordinal)).ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Missing indexed message {sessionId}@{ordinal}");
        }
        using var stream = new FileStream(
            Path.Combine(_sessionsRoot, SessionDirName(sessionId), meta.File),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        var line = new byte[reader.GetInt64(1)];
        stream.Seek(reader.GetInt64(0), SeekOrigin.Begin);
        stream.ReadExactly(line);
        var message = MessageJson.DecodeMessage(line);
        if (partIndex < 0 || partIndex >= message.Parts.Count ||
            message.Parts[partIndex] is not ToolResultPart toolResult)
        {
            throw new InvalidOperationException($"{sessionId}@{ordinal}[{partIndex}] is not a tool result");
        }
        return toolResult.Content;
    }

    // ---- writes ----

    public long AppendMessage(string sessionId, MessageRecord message)
    {
        var meta = TrySessionMeta(sessionId);
        var dir = Path.Combine(_sessionsRoot, SessionDirName(sessionId));
        Directory.CreateDirectory(dir);
        var start = meta?.Bytes ?? 0;
        long bytesEnd;
        Written written;
        using (var stream = new FileStream(
                   Path.Combine(dir, meta?.File ?? FileFor(0)), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
        {
            stream.Seek(start, SeekOrigin.Begin);
            written = AppendLine(stream, message);
            stream.Flush(); // data stage handed to the OS BEFORE the index transaction (OsFlush)
            bytesEnd = stream.Position;
        }
        var ordinal = meta?.Count ?? 0;
        using var transaction = Begin();
        try
        {
            if (meta is null)
            {
                Execute("INSERT INTO sessions(name, count, generation, file, bytes) VALUES ($n, 1, 0, $f, $b)",
                    ("$n", sessionId), ("$f", FileFor(0)), ("$b", bytesEnd));
            }
            else
            {
                Execute("UPDATE sessions SET count = count + 1, bytes = $b WHERE name = $n",
                    ("$b", bytesEnd), ("$n", sessionId));
            }
            InsertMessageRow(sessionId, ordinal, written);
            CommitTx(transaction);
        }
        catch
        {
            RollbackTx(transaction);
            throw;
        }
        return ordinal;
    }

    private void InsertMessageRow(string sessionId, long ordinal, Written written)
    {
        Execute(
            "INSERT INTO messages(session, ordinal, offset, length, role, meta) VALUES ($s, $o, $f, $l, $r, $m)",
            ("$s", sessionId), ("$o", ordinal), ("$f", written.Offset), ("$l", written.Length),
            ("$r", written.Role), ("$m", written.Meta));
    }

    private sealed record DraftState(string Stream, long LastSequence);

    private DraftState? TryDraftMarker(string sessionId)
    {
        using var reader = BindCommand(
            "SELECT stream, last_seq FROM draft_marker WHERE session = $s", ("$s", sessionId)).ExecuteReader();
        return reader.Read() ? new DraftState(reader.GetString(0), reader.GetInt64(1)) : null;
    }

    public void StreamDraft(string sessionId, string streamId, string chunk)
    {
        using var transaction = Begin();
        try
        {
            DraftState active;
            switch (TryDraftMarker(sessionId))
            {
                case null:
                    active = new DraftState(streamId, 0);
                    break;
                case var found when found.Stream != streamId:
                    throw new InvalidOperationException(
                        $"Session {sessionId} already has active draft {found.Stream}; only one draft per session");
                case var found:
                    active = found;
                    break;
            }
            if (active.LastSequence == 0)
            {
                Execute("INSERT INTO draft_marker(session, stream, last_seq) VALUES ($s, $f, 1)",
                    ("$s", sessionId), ("$f", streamId));
                Execute("INSERT INTO draft_batch(session, stream, seq, chunk) VALUES ($s, $f, 1, $c)",
                    ("$s", sessionId), ("$f", streamId), ("$c", chunk));
            }
            else
            {
                var next = active.LastSequence + 1;
                Execute("INSERT INTO draft_batch(session, stream, seq, chunk) VALUES ($s, $f, $q, $c)",
                    ("$s", sessionId), ("$f", streamId), ("$q", next), ("$c", chunk));
                Execute("UPDATE draft_marker SET last_seq = $q WHERE session = $s",
                    ("$q", next), ("$s", sessionId));
            }
            CommitTx(transaction); // each StreamDraft call persists its own batch; nothing is buffered for finalize
        }
        catch
        {
            RollbackTx(transaction);
            throw;
        }
    }

    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage)
    {
        var marker = TryDraftMarker(sessionId);
        if (marker is not { } active)
        {
            throw new InvalidOperationException($"No active draft on session {sessionId}");
        }
        if (active.Stream != streamId)
        {
            throw new InvalidOperationException(
                $"No active draft {streamId} on session {sessionId} (active draft {active.Stream})");
        }
        var meta = TrySessionMeta(sessionId); // a finalized stream may be the session's first message
        var dir = Path.Combine(_sessionsRoot, SessionDirName(sessionId));
        Directory.CreateDirectory(dir);
        Written written;
        long bytesEnd;
        using (var stream = new FileStream(
                   Path.Combine(dir, meta?.File ?? FileFor(0)), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
        {
            stream.Seek(meta?.Bytes ?? 0, SeekOrigin.Begin);
            written = AppendLine(stream, finalMessage);
            stream.Flush(); // data stage handed to the OS BEFORE the index transaction (OsFlush)
            bytesEnd = stream.Position;
        }
        using (var transaction = Begin())
        {
            try
            {
                InsertMessageRow(sessionId, meta?.Count ?? 0, written);
                if (meta is null)
                {
                    Execute("INSERT INTO sessions(name, count, generation, file, bytes) VALUES ($n, 1, 0, $f, $b)",
                        ("$n", sessionId), ("$f", FileFor(0)), ("$b", bytesEnd));
                }
                else
                {
                    Execute("UPDATE sessions SET count = count + 1, bytes = $b WHERE name = $n",
                        ("$b", bytesEnd), ("$n", sessionId));
                }
                Execute("DELETE FROM draft_marker WHERE session = $s", ("$s", sessionId));
                Execute("DELETE FROM draft_batch WHERE session = $s AND stream = $f", ("$s", sessionId), ("$f", streamId));
                CommitTx(transaction);
            }
            catch
            {
                RollbackTx(transaction);
                throw;
            }
        }
        return meta?.Count ?? 0;
    }

    /// <summary>
    /// Generation rewrite: the kept prefix [0, fromOrdinal) is copied byte-identically
    /// (no re-encode), the replacements are appended, and only then is everything
    /// published in one SQLite transaction: new pointer (generation/file/bytes), new
    /// offset rows, count. O(session-bytes) edit cost; no in-place edit; the
    /// superseded file is deleted best-effort after publication (never claimed atomic).
    /// </summary>
    public long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages)
    {
        var meta = TrySessionMeta(sessionId)
            ?? throw new InvalidOperationException($"Unknown session {sessionId}");
        if (fromOrdinal < 0 || fromOrdinal > meta.Count)
        {
            throw new InvalidOperationException(
                $"ReplaceSuffix from={fromOrdinal} outside count={meta.Count} in {sessionId}");
        }
        var fromOffset = OrdinalOffset(sessionId, fromOrdinal, meta);
        var dir = Path.Combine(_sessionsRoot, SessionDirName(sessionId));
        var oldFile = Path.Combine(dir, meta.File);
        var generation = meta.Generation + 1;
        var newFile = FileFor(generation);
        var newFile_ = Path.Combine(dir, newFile);
        if (File.Exists(newFile_)) File.Delete(newFile_); // interrupted earlier attempt, unindexed

        var written = new List<Written>(newMessages.Count);
        long bytesEnd;
        using (var target = new FileStream(newFile_, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (fromOffset > 0)
            {
                using var source = new FileStream(oldFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                CopyExactly(source, target, fromOffset);
            }
            foreach (var message in newMessages)
            {
                written.Add(AppendLine(target, message));
            }
            target.Flush(); // data stage handed to the OS BEFORE the index transaction (OsFlush)
            bytesEnd = target.Position;
        }
        using (var transaction = Begin())
        {
            try
            {
                Execute("DELETE FROM messages WHERE session = $s AND ordinal >= $o", ("$s", sessionId), ("$o", fromOrdinal));
                for (var i = 0; i < written.Count; i++)
                {
                    InsertMessageRow(sessionId, fromOrdinal + i, written[i]);
                }
                Execute("UPDATE sessions SET count = $c, generation = $g, file = $f, bytes = $b WHERE name = $n",
                    ("$c", fromOrdinal + written.Count), ("$g", generation), ("$f", newFile), ("$b", bytesEnd), ("$n", sessionId));
                CommitTx(transaction);
            }
            catch
            {
            RollbackTx(transaction);
                throw;
            }
        }
        try
        {
            File.Delete(oldFile); // publication done; the superseded generation is reclaimable
        }
        catch (IOException)
        {
            // The next Open pass sweeps unreferenced files (never served, never state).
        }
        return fromOrdinal + newMessages.Count;
    }

    private long OrdinalOffset(string sessionId, long ordinal, SessionPtr meta)
    {
        if (ordinal == meta.Count) return meta.Bytes; // count → end of file
        using var reader = BindCommand(
            "SELECT offset FROM messages WHERE session = $s AND ordinal = $o",
            ("$s", sessionId), ("$o", ordinal)).ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Missing indexed message {sessionId}@{ordinal}");
        }
        return reader.GetInt64(0);
    }

    private static void CopyExactly(FileStream source, FileStream target, long bytes)
    {
        var buffer = new byte[64 * 1024];
        while (bytes > 0)
        {
            var chunk = (int)Math.Min(bytes, buffer.Length);
            source.ReadExactly(buffer, 0, chunk);
            target.Write(buffer, 0, chunk);
            bytes -= chunk;
        }
    }

    private static string FileFor(long generation) => $"messages-{generation}.jsonl";

    // ---- assignments ----

    public void CreateAssignment(AssignmentRecord assignment)
    {
        using var transaction = Begin();
        try
        {
            Execute("INSERT INTO assignments(id, target, title, status, result) VALUES ($i, $t, $ti, $st, $r)",
                ("$i", assignment.AssignmentId), ("$t", assignment.TargetSessionId), ("$ti", assignment.Title),
                ("$st", (long)assignment.Status), ("$r", (object?)assignment.ResultText ?? DBNull.Value));
            Execute("INSERT INTO assign_index(target, id, status) VALUES ($t, $i, $st)",
                ("$t", assignment.TargetSessionId), ("$i", assignment.AssignmentId), ("$st", (long)assignment.Status));
            CommitTx(transaction);
        }
        catch (SqliteException)
        {
            RollbackTx(transaction);
            throw new InvalidOperationException($"Assignment {assignment.AssignmentId} already exists");
        }
        catch
        {
            RollbackTx(transaction);
            throw;
        }
    }

    public void SettleAssignment(SettleAssignmentRequest request)
    {
        using var reader = BindCommand(
            "SELECT 1 FROM assignments WHERE id = $i", ("$i", request.AssignmentId)).ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Unknown assignment {request.AssignmentId}");
        }
        using var transaction = Begin();
        try
        {
            Execute("UPDATE assignments SET status = $st, result = $r WHERE id = $i",
                ("$st", (long)AssignmentStatus.Settled), ("$r", (object?)request.ResultText ?? DBNull.Value),
                ("$i", request.AssignmentId));
            Execute("UPDATE assign_index SET status = $st WHERE id = $i",
                ("$st", (long)AssignmentStatus.Settled), ("$i", request.AssignmentId));
            CommitTx(transaction);
        }
        catch
        {
            RollbackTx(transaction);
            throw;
        }
    }

    public AssignmentRecord GetAssignment(string assignmentId)
    {
        using var reader = BindCommand(
            "SELECT id, target, title, status, result FROM assignments WHERE id = $i",
            ("$i", assignmentId)).ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Unknown assignment {assignmentId}");
        }
        return new AssignmentRecord(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            (AssignmentStatus)reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    // ---- deletion ----

    /// <summary>
    /// Final-closure deletion under the frozen invariant: the catalog, message
    /// index, assignment-target index and all stream state for the session are
    /// removed in one SQLite transaction — with open assignments refused under
    /// RequireSettled and cancelled in the SAME transaction under CancelOpen.
    /// Cancelled and settled records survive. The transcript files are deleted
    /// best-effort after publication; a crash before that leaves an
    /// unreferenced orphan that the next Open pass reclaims by content.
    /// </summary>
    public void DeleteSession(string sessionId, DeletePolicy policy)
    {
        if (!ExistsSession(sessionId))
        {
            throw new InvalidOperationException($"Unknown session {sessionId}");
        }
        var open = CountOf(
            "SELECT COUNT(*) FROM assignments WHERE target = $t AND status = $st",
            ("$t", sessionId), ("$st", (long)AssignmentStatus.Open));
        if (policy == DeletePolicy.RequireSettled && open > 0)
        {
            throw new InvalidOperationException(
                $"DeleteSession {sessionId}: {open} open assignment(s) violate the RequireSettled invariant");
        }
        var dir = Path.Combine(_sessionsRoot, SessionDirName(sessionId));
        using (var transaction = Begin())
        {
            try
            {
                if (policy == DeletePolicy.CancelOpen && open > 0)
                {
                    Execute("UPDATE assignments SET status = $st WHERE target = $t AND status = $open",
                        ("$st", (long)AssignmentStatus.Cancelled), ("$t", sessionId), ("$open", (long)AssignmentStatus.Open));
                }
                Execute("DELETE FROM sessions WHERE name = $n", ("$n", sessionId));
                Execute("DELETE FROM messages WHERE session = $s", ("$s", sessionId));
                Execute("DELETE FROM assign_index WHERE target = $t", ("$t", sessionId));
                Execute("DELETE FROM draft_marker WHERE session = $s", ("$s", sessionId));
                Execute("DELETE FROM draft_batch WHERE session = $s", ("$s", sessionId));
                CommitTx(transaction);
            }
            catch
            {
            RollbackTx(transaction);
                throw;
            }
        }
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort; the next Open pass reclaims the orphan.
        }
    }

    public void Dispose()
    {
        _db?.Dispose();
        _db = null;
    }
}