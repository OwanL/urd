using System.Runtime.InteropServices;
using RocksDbSharp;
using System.Text;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.RocksDbStore;

// Family-5 adapter: one RocksDB instance, one column family per purpose,
// ordered composite keys (session, ordinal) so the sorted keyspace is the
// index — no separate index structure.
//
// Atomicity: every mutation is exactly one WriteBatch commit. The suffix edit
// is DeleteRange over the replaced span (explicit exclusive end sentinel — the
// binding rejects the unbounded null-end form) PLUS the reinsertions in the
// same batch, so no stale old-suffix key can ever be observed.
//
// Streaming: every StreamDraft call writes its own batch record through one
// WriteBatch immediately (uniform per-batch write policy — no buffering until
// finalize). FinalizeStream commits the final message, the session count, the
// marker removal and the draft-record range delete in one WriteBatch.
//
// Durability: no fsync in the workload — WriteOptions sync stays false (the
// default), so each commit is handed to the OS through the WAL's buffered file
// I/O (declared tier: OsFlush). No Flush() or CompactRange() calls; compaction
// is an engine-side background observation only, never a design mechanism.
//
// Bounded reads: window operations seek the exact composite start key and stop
// at the window end or the session's key boundary. OpenRecentPreview decodes
// only per-part length metadata plus a bounded leading preview prefix — it
// never builds full content strings, and no operation ever scans a whole
// session into memory.

public sealed class RocksDbTranscriptStore : ITranscriptStore, IStoreStatistics
{
    public const string DefaultColumnFamily = "default";
    private const string MessagesCf = "messages";
    private const string SessionsCf = "sessions";
    private const string AssignmentsCf = "assignments";
    private const string AssignmentIndexCf = "assign-index";
    private const string DraftsCf = "drafts";

    private static readonly string[] FamilyNames =
        [DefaultColumnFamily, MessagesCf, SessionsCf, AssignmentsCf, AssignmentIndexCf, DraftsCf];

    private RocksDb? _db;
    private ColumnFamilyHandle? _messages;
    private ColumnFamilyHandle? _sessions;
    private ColumnFamilyHandle? _assignments;
    private ColumnFamilyHandle? _assignmentIndex;
    private ColumnFamilyHandle? _drafts;

    public StoreDescriptor Describe() => new(
        "RocksDbIndexedRecords",
        AckTier.OsFlush,
        "DbOptions: SetCreateIfMissing(true), SetCreateMissingColumnFamilies(true); all other DBOptions defaults "
        + "(WAL enabled by default, manual_wal_flush=false default). WriteOptions: default (sync=false) — each "
        + "WriteBatch commit is handed to the OS through the WAL's buffered file I/O, never fsynced; no SetSync(true), "
        + "no Flush(), no CompactRange(), no WAL-dir or flush-related overrides anywhere in the adapter. FlushWAL is "
        + "not exposed on the high-level RocksDb API in binding 11.12.0.7641 (reflection-checked in "
        + "STORAGE-BINDING-REVIEW.md). Kill-test verdicts come from the store's actual survival behaviour, not this note.",
        "Family 5 layout: one RocksDB instance; column families per purpose (messages, sessions, assignments, "
        + "assign-index, drafts); composite keys [4BE utf8-name-length][name bytes][8BE big-endian ordinal/sequence] "
        + "so the sorted keyspace is the index; explicit versioned binary payload encoding; tool results stored "
        + "inline in the message value; preview reads return per-part length metadata and a bounded leading prefix, "
        + "never full huge content; suffix edit is one atomic WriteBatch (DeleteRange [session:from, sentinel) plus "
        + "reinsertions); every StreamDraft call persists its own batch; compaction is engine-default background "
        + "behaviour only, its disk/tail effects are recorded as observations, never tuned or hidden.");

    public void Open(string storeRoot, string attachmentRoot)
    {
        BootstrapNativeLibraryOnce();
        Directory.CreateDirectory(storeRoot);
        DisposeDb();

        var dbOptions = new DbOptions()
            .SetCreateIfMissing(true)
            .SetCreateMissingColumnFamilies(true);
        // Every existing column family (always including the default family)
        // must be provided at open: list first, then add ours that are missing.
        var existing = File.Exists(Path.Combine(storeRoot, "CURRENT"))
            ? RocksDb.ListColumnFamilies(dbOptions, storeRoot).ToArray()
            : [];
        var families = new ColumnFamilies();
        foreach (var name in existing) families.Add(name, new ColumnFamilyOptions());
        foreach (var name in FamilyNames)
        {
            if (!existing.Contains(name)) families.Add(name, new ColumnFamilyOptions());
        }

        var db = RocksDb.Open(dbOptions, storeRoot, families);
        foreach (var name in FamilyNames)
        {
            if (name == DefaultColumnFamily) continue;
            if (!db.TryGetColumnFamily(name, out var handle) || handle is null)
            {
                throw new InvalidOperationException($"RocksDB did not expose required column family '{name}'");
            }
            switch (name)
            {
                case MessagesCf: _messages = handle; break;
                case SessionsCf: _sessions = handle; break;
                case AssignmentsCf: _assignments = handle; break;
                case AssignmentIndexCf: _assignmentIndex = handle; break;
                case DraftsCf: _drafts = handle; break;
            }
        }
        if (_messages is null || _sessions is null || _assignments is null || _assignmentIndex is null || _drafts is null)
        {
            throw new InvalidOperationException("RocksDB did not expose all required column families");
        }
        _db = db;
    }

    public SessionMeta[] ListSessionsMeta()
    {
        var db = RequireDb();
        var metas = new List<SessionMeta>();
        using var iterator = db.NewIterator(_sessions);
        for (iterator.SeekToFirst(); iterator.Valid(); iterator.Next())
        {
            var key = iterator.Key();
            metas.Add(new SessionMeta(
                Encoding.UTF8.GetString(key, 4, key.Length - 4),
                RecordCodec.DecodeCount(iterator.Value())));
        }
        return [.. metas];
    }

    public MessageRecord[] OpenRecent(string sessionId, int count)
    {
        if (count <= 0) return [];
        var metaCount = TrySessionCount(sessionId);
        if (metaCount is null) return []; // absence is not an error
        var from = Math.Max(0, metaCount.Value - count);
        return ReadWindow(sessionId, from, metaCount.Value);
    }

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count)
    {
        if (count <= 0) return [];
        var metaCount = TrySessionCount(sessionId);
        if (metaCount is null) return [];
        var to = Math.Clamp(beforeOrdinal, 0, metaCount.Value);
        var from = Math.Max(0, to - count);
        return ReadWindow(sessionId, from, to);
    }

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive)
    {
        var metaCount = TrySessionCount(sessionId);
        if (metaCount is null) return []; // absence is not an error
        ValidateRangeBounds(sessionId, metaCount.Value, fromOrdinal, toOrdinalExclusive);
        return ReadWindow(sessionId, fromOrdinal, toOrdinalExclusive);
    }

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars) =>
        CollectPreviewWindow(sessionId, count, previewChars);

    public string FetchToolResult(string sessionId, long ordinal, int partIndex)
    {
        var db = RequireDb();
        // Targeted single-result read: exactly one message is read and decoded.
        var data = db.Get(StoreKeys.Message(sessionId, ordinal), _messages)
            ?? throw new InvalidOperationException($"No message {sessionId}@{ordinal}");
        var parts = RecordCodec.DecodeMessage(data).Parts;
        if (partIndex < 0 || partIndex >= parts.Count)
        {
            throw new InvalidOperationException(
                $"No part {partIndex} in {sessionId}@{ordinal} (count={parts.Count})");
        }
        return parts[partIndex] is ToolResultPart toolResult
            ? toolResult.Content
            : throw new InvalidOperationException(
                $"Part {partIndex} in {sessionId}@{ordinal} is {parts[partIndex].GetType().Name}, not a tool result");
    }

    public long AppendMessage(string sessionId, MessageRecord message)
    {
        var db = RequireDb();
        var count = TrySessionCount(sessionId) ?? 0; // mutations create their target session
        using var batch = new WriteBatch();
        batch.Put(StoreKeys.Message(sessionId, count), RecordCodec.EncodeMessage(message), _messages);
        batch.Put(StoreKeys.SessionMeta(sessionId), RecordCodec.EncodeCount(count + 1), _sessions);
        db.Write(batch);
        return count;
    }

    public void StreamDraft(string sessionId, string streamId, string chunk)
    {
        var db = RequireDb();
        var markerRaw = db.Get(StoreKeys.DraftMarker(sessionId), _drafts);
        if (markerRaw is null)
        {
            // Register the stream and write the first batch in one commit, so
            // stream identity is observable even before a non-empty batch.
            using var batch = new WriteBatch();
            batch.Put(StoreKeys.DraftMarker(sessionId), RecordCodec.EncodeStreamMarker(streamId, 0), _drafts);
            batch.Put(StoreKeys.DraftBatch(sessionId, streamId, 0), Encoding.UTF8.GetBytes(chunk), _drafts);
            db.Write(batch);
            return;
        }
        var (activeStream, lastSequence) = RecordCodec.DecodeStreamMarker(markerRaw);
        if (activeStream != streamId)
        {
            throw new InvalidOperationException(
                $"Session {sessionId} already has active draft {activeStream}; only one draft per session");
        }
        using var append = new WriteBatch();
        append.Put(
            StoreKeys.DraftBatch(sessionId, streamId, lastSequence + 1),
            Encoding.UTF8.GetBytes(chunk),
            _drafts);
        append.Put(StoreKeys.DraftMarker(sessionId), RecordCodec.EncodeStreamMarker(streamId, lastSequence + 1), _drafts);
        db.Write(append);
    }

    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage)
    {
        var db = RequireDb();
        var markerRaw = db.Get(StoreKeys.DraftMarker(sessionId), _drafts)
            ?? throw new InvalidOperationException($"No active draft {streamId} in {sessionId}");
        var (activeStream, lastSequence) = RecordCodec.DecodeStreamMarker(markerRaw);
        if (activeStream != streamId)
        {
            throw new InvalidOperationException($"No active draft {streamId} in {sessionId}");
        }
        var count = TrySessionCount(sessionId) ?? 0;
        using var batch = new WriteBatch();
        batch.Put(StoreKeys.Message(sessionId, count), RecordCodec.EncodeMessage(finalMessage), _messages);
        batch.Put(StoreKeys.SessionMeta(sessionId), RecordCodec.EncodeCount(count + 1), _sessions);
        batch.Delete(StoreKeys.DraftMarker(sessionId), _drafts);
        // Remove every persisted batch record of this stream in the same commit.
        var streamPrefix = StoreKeys.DraftBatchPrefix(sessionId, streamId);
        var streamEnd = StoreKeys.SentinelAfter(streamPrefix);
        batch.DeleteRange(streamPrefix, (ulong)streamPrefix.Length, streamEnd, (ulong)streamEnd.Length, _drafts);
        db.Write(batch);
        return count;
    }

    public long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages)
    {
        var db = RequireDb();
        var count = TrySessionCount(sessionId)
            ?? throw new InvalidOperationException($"Unknown session {sessionId}");
        if (fromOrdinal < 0 || fromOrdinal > count)
        {
            throw new InvalidOperationException($"ReplaceSuffix from={fromOrdinal} outside count={count} in {sessionId}");
        }
        using var batch = new WriteBatch();
        var sessionPrefix = StoreKeys.NameKey(sessionId);
        var start = StoreKeys.Message(sessionId, fromOrdinal);
        var end = StoreKeys.SentinelAfter(sessionPrefix);
        batch.DeleteRange(start, (ulong)start.Length, end, (ulong)end.Length, _messages);
        for (var i = 0; i < newMessages.Count; i++)
        {
            batch.Put(StoreKeys.Message(sessionId, fromOrdinal + i), RecordCodec.EncodeMessage(newMessages[i]), _messages);
        }
        batch.Put(StoreKeys.SessionMeta(sessionId), RecordCodec.EncodeCount(fromOrdinal + newMessages.Count), _sessions);
        db.Write(batch);
        return fromOrdinal + newMessages.Count; // dense final count like the reference
    }

    public void CreateAssignment(AssignmentRecord assignment)
    {
        var db = RequireDb();
        if (db.Get(StoreKeys.AssignmentRecordKey(assignment.AssignmentId), _assignments) is not null)
        {
            throw new InvalidOperationException($"Duplicate assignment id {assignment.AssignmentId}");
        }
        using var batch = new WriteBatch();
        batch.Put(StoreKeys.AssignmentRecordKey(assignment.AssignmentId), RecordCodec.EncodeAssignment(assignment), _assignments);
        byte[] indexStatus = [RecordCodec.StatusByteOf(assignment.Status)];
        batch.Put(StoreKeys.AssignmentIndex(assignment.TargetSessionId, assignment.AssignmentId), indexStatus, _assignmentIndex);
        db.Write(batch);
    }

    public void SettleAssignment(SettleAssignmentRequest request)
    {
        var db = RequireDb();
        var current = ReadAssignment(db, request.AssignmentId);
        var updated = current with { Status = AssignmentStatus.Settled, ResultText = request.ResultText };
        using var batch = new WriteBatch();
        batch.Put(StoreKeys.AssignmentRecordKey(request.AssignmentId), RecordCodec.EncodeAssignment(updated), _assignments);
        byte[] settledStatus = [RecordCodec.StatusByteOf(AssignmentStatus.Settled)];
        batch.Put(StoreKeys.AssignmentIndex(current.TargetSessionId, request.AssignmentId), settledStatus, _assignmentIndex);
        db.Write(batch);
    }

    public AssignmentRecord GetAssignment(string assignmentId)
    {
        var db = RequireDb();
        return ReadAssignment(db, assignmentId);
    }

    public void DeleteSession(string sessionId, DeletePolicy policy)
    {
        var db = RequireDb();
        if (TrySessionCount(sessionId) is null)
        {
            throw new InvalidOperationException($"DeleteSession of unknown session {sessionId}");
        }
        var sessionPrefix = StoreKeys.NameKey(sessionId);
        var openIds = OpenAssignmentIds(db, sessionPrefix);
        if (openIds.Count > 0 && policy == DeletePolicy.RequireSettled)
        {
            throw new InvalidOperationException(
                $"DeleteSession {sessionId}: {openIds.Count} open assignment(s) violate the RequireSettled invariant");
        }
        using var batch = new WriteBatch();
        // One atomic batch: message records, session metadata, assignment index
        // entries and draft records all go away together; open assignments are
        // flipped to Cancelled (their records survive as required by the
        // oracle), so no dangling open reference is left.
        var suffix = StoreKeys.SentinelAfter(sessionPrefix);
        batch.DeleteRange(sessionPrefix, (ulong)sessionPrefix.Length, suffix, (ulong)suffix.Length, _messages);
        batch.DeleteRange(sessionPrefix, (ulong)sessionPrefix.Length, suffix, (ulong)suffix.Length, _sessions);
        batch.DeleteRange(sessionPrefix, (ulong)sessionPrefix.Length, suffix, (ulong)suffix.Length, _assignmentIndex);
        batch.DeleteRange(sessionPrefix, (ulong)sessionPrefix.Length, suffix, (ulong)suffix.Length, _drafts);
        foreach (var id in openIds)
        {
            var record = ReadAssignment(db, id);
            batch.Put(
                StoreKeys.AssignmentRecordKey(id),
                RecordCodec.EncodeAssignment(record with { Status = AssignmentStatus.Cancelled }),
                _assignments);
        }
        db.Write(batch);
    }

    public StoreStatistics Sample() => new(GC.GetTotalAllocatedBytes(precise: false));

    public void Dispose()
    {
        DisposeDb();
    }

    // ---- internals ----

    private void DisposeDb()
    {
        if (_db is null) return;
        // No explicit CF-handle teardown in this binding: handles live with the db.
        _messages = _sessions = _assignments = _assignmentIndex = _drafts = null;
        _db.Dispose();
        _db = null;
    }

    private RocksDb RequireDb() => _db ?? throw new InvalidOperationException("Store is not open");

    private long? TrySessionCount(string sessionId)
    {
        var raw = RequireDb().Get(StoreKeys.SessionMeta(sessionId), _sessions);
        return raw is null ? null : RecordCodec.DecodeCount(raw);
    }

    private static void ValidateRangeBounds(string sessionId, long metaCount, long from, long toExclusive)
    {
        if (from < 0 || toExclusive < from || toExclusive > metaCount)
        {
            throw new InvalidOperationException($"Range [{from},{toExclusive}) outside count={metaCount} in {sessionId}");
        }
    }

    private MessageRecord[] ReadWindow(string sessionId, long fromOrdinal, long toOrdinalExclusive)
    {
        var db = RequireDb();
        var sessionPrefix = StoreKeys.NameKey(sessionId);
        var messages = new List<MessageRecord>((int)Math.Min(toOrdinalExclusive - fromOrdinal, int.MaxValue));
        using var iterator = db.NewIterator(_messages);
        iterator.Seek(StoreKeys.Message(sessionId, fromOrdinal));
        while (iterator.Valid())
        {
            var key = iterator.Key();
            if (!StoreKeys.HasPrefix(key, sessionPrefix)) break;
            if (StoreKeys.ReadOrdinal(key, sessionPrefix.Length) >= toOrdinalExclusive) break;
            messages.Add(RecordCodec.DecodeMessage(iterator.Value()));
            iterator.Next();
        }
        return [.. messages];
    }

    private WindowPreview CollectPreviewWindow(string sessionId, int count, int previewChars)
    {
        var db = RequireDb();
        var metaCount = TrySessionCount(sessionId);
        var items = new List<MessagePreview>();
        if (metaCount is { } total && count > 0)
        {
            var from = Math.Max(0, total - Math.Min(count, total));
            var sessionPrefix = StoreKeys.NameKey(sessionId);
            using var iterator = db.NewIterator(_messages);
            iterator.Seek(StoreKeys.Message(sessionId, from));
            var to = from + Math.Min(count, total - from);
            while (iterator.Valid())
            {
                var key = iterator.Key();
                if (!StoreKeys.HasPrefix(key, sessionPrefix)) break;
                if (StoreKeys.ReadOrdinal(key, sessionPrefix.Length) >= to) break;
                var ordinal = StoreKeys.ReadOrdinal(key, sessionPrefix.Length);
                var decoded = RecordCodec.DecodeMessage(iterator.Value(), fullContent: false, previewChars);
                items.Add(new MessagePreview(ordinal, decoded.Role, decoded.Previews!));
                iterator.Next();
            }
            return new WindowPreview(sessionId, from, items);
        }
        return new WindowPreview(sessionId, 0, items);
    }

    private AssignmentRecord ReadAssignment(RocksDb db, string assignmentId)
    {
        var raw = db.Get(StoreKeys.AssignmentRecordKey(assignmentId), _assignments)
            ?? throw new InvalidOperationException($"Unknown assignment {assignmentId}");
        return RecordCodec.DecodeAssignment(raw);
    }

    private List<string> OpenAssignmentIds(RocksDb db, ArraySegment<byte> sessionKeyPrefix)
    {
        var ids = new List<string>();
        using var iterator = db.NewIterator(_assignmentIndex);
        var seekTarget = sessionKeyPrefix.Array!.AsSpan(sessionKeyPrefix.Offset, sessionKeyPrefix.Count).ToArray();
        iterator.Seek(seekTarget);
        while (iterator.Valid())
        {
            var key = iterator.Key();
            if (!StoreKeys.HasPrefix(key, sessionKeyPrefix)) break;
            if (iterator.Value().Length == 1 && iterator.Value()[0] == RecordCodec.StatusByteOf(AssignmentStatus.Open))
            {
                ids.Add(StoreKeys.ReadNameAfterPrefixedKey(key, sessionKeyPrefix));
            }
            iterator.Next();
        }
        return ids;
    }

    /// <summary>
    /// Loads the packaged native rocksdb.dll explicitly when running as a
    /// plugin: the binding resolves its native dependency through the default
    /// app context, which does not include a plugin directory. Loading the
    /// module by full path registers it under its own name, so the binding's
    /// later native resolution binds to it. In normal app deployments the file
    /// is next to the app and this is a no-op (already loaded).
    /// </summary>
    private static void BootstrapNativeLibraryOnce()
    {
        var bootstrap = Path.Combine(
            Path.GetDirectoryName(typeof(RocksDbTranscriptStore).Assembly.Location) ?? ".",
            "runtimes", "win-x64", "native", "rocksdb.dll");
        if (File.Exists(bootstrap) && NativeLibrary.TryLoad(bootstrap, out _))
        {
            return;
        }
        var besideAssembly = Path.Combine(
            Path.GetDirectoryName(typeof(RocksDbTranscriptStore).Assembly.Location) ?? ".", "rocksdb.dll");
        if (File.Exists(besideAssembly)) NativeLibrary.TryLoad(besideAssembly, out _);
    }
}