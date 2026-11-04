using System.Text;
using LightningDB;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.LmdbStore;

// Family-4 transcript-storage adapter: one LMDB environment (LightningDB
// 0.23.1, LMDB 1.0 format); the ordered byte-key keyspace IS the index.
//
// Layout (all in one environment, one named DBI per purpose):
//   sessions       sessEnc -> u64 dense message count
//   msgs           sessEnc + BE64 ordinal -> message header record (small:
//                  part metadata + preview prefixes; no full payload text)
//   content        sessEnc + BE64 ordinal + BE32 partIndex -> raw UTF-8 of
//                  that part's full content (spilled out of the header so
//                  preview/metadata reads never decode payloads)
//   assign-id      assignmentIdEnc -> assignment record
//   assign-target  sessEnc(target) + assignmentIdEnc -> empty (derived index)
//   draft-chunks   sessEnc + streamIdEnc + BE64 chunkIndex -> raw UTF-8 batch
//   draft-meta     sessEnc + streamIdEnc -> u64 next chunk index
//
// Session keys use KeyCodec.StringKey: order-preserving and prefix-free, so
// ListSessionsMeta iterates in StringComparer.Ordinal order and per-session
// cursors isolate blocks exactly.
//
// Every mutation is ONE write transaction with LMDB's default commit fsync
// (AckTier.FsyncOrJournal); suffix edits delete the replaced key range and
// reinsert inside the same transaction, so stale old-suffix keys are never
// observable. No weakening env flags; no live-top-generation machinery; no
// in-memory mirror of transcript content (reads always go to the map).

public sealed class LmdbTranscriptStore : ITranscriptStore, IStoreStatistics
{
    private const long MapBytes = 1024L * 1024 * 1024; // 1 GiB address reservation; data file grows lazily
    private const int MaxDatabases = 8;
    private const int PreviewBudgetChars = 256;

    private LightningEnvironment? _env;
    private LightningDatabase? _dbSessions;
    private LightningDatabase? _dbMsgs;
    private LightningDatabase? _dbContent;
    private LightningDatabase? _dbAssignId;
    private LightningDatabase? _dbAssignTarget;
    private LightningDatabase? _dbDraftChunks;
    private LightningDatabase? _dbDraftMeta;

    public StoreDescriptor Describe() => new(
        "LmdbStore",
        AckTier.FsyncOrJournal,
        "LMDB env defaults: commit fsync enabled (no NOSYNC/NOMETASYNC/WRITEMAP); " +
        "MapSize=1073741824 (1 GiB address reservation, not resident; data file grows as pages are written); " +
        $"MaxDatabases={MaxDatabases}; single writer environment; one write transaction per mutation",
        "Family 4: single LMDB env, composite order-preserving byte keys (session UTF-16-unit triplet encoding + BE64 ordinal); " +
        "msg headers hold part metadata + <=256-char preview prefixes; full content in adjacent content records fetched per part " +
        "(no payload decode on preview paths, targeted single-key FetchToolResult); stream drafts persisted as append chunk rows " +
        "per StreamDraft call; suffix edits are cursor range deletion + reinsertion inside one commit");

    // ---- lifecycle ----

    public void Open(string storeRoot, string attachmentRoot)
    {
        if (_env is not null)
        {
            throw new InvalidOperationException("LmdbStore: already open");
        }
        Directory.CreateDirectory(storeRoot);
        _env = new LightningEnvironment(storeRoot)
        {
            MapSize = MapBytes,
            MaxDatabases = MaxDatabases,
        };
        _env.Open();
        using var tx = BeginWrite();
        _dbSessions = tx.OpenDatabase(DbNames.Sessions, Cfg(), closeOnDispose: false);
        _dbMsgs = tx.OpenDatabase(DbNames.Messages, Cfg(), closeOnDispose: false);
        _dbContent = tx.OpenDatabase(DbNames.Content, Cfg(), closeOnDispose: false);
        _dbAssignId = tx.OpenDatabase(DbNames.AssignId, Cfg(), closeOnDispose: false);
        _dbAssignTarget = tx.OpenDatabase(DbNames.AssignTarget, Cfg(), closeOnDispose: false);
        _dbDraftChunks = tx.OpenDatabase(DbNames.DraftChunks, Cfg(), closeOnDispose: false);
        _dbDraftMeta = tx.OpenDatabase(DbNames.DraftMeta, Cfg(), closeOnDispose: false);
        tx.Commit();
    }

    public void Dispose()
    {
        _dbSessions?.Dispose(); _dbSessions = null;
        _dbMsgs?.Dispose(); _dbMsgs = null;
        _dbContent?.Dispose(); _dbContent = null;
        _dbAssignId?.Dispose(); _dbAssignId = null;
        _dbAssignTarget?.Dispose(); _dbAssignTarget = null;
        _dbDraftChunks?.Dispose(); _dbDraftChunks = null;
        _dbDraftMeta?.Dispose(); _dbDraftMeta = null;
        _env?.Dispose();
        _env = null;
    }

    // ---- reads ----

    public SessionMeta[] ListSessionsMeta()
    {
        using var tx = ReadTx();
        return ListSessionsMetaIn(tx);
    }

    public MessageRecord[] OpenRecent(string sessionId, int count)
    {
        using var tx = ReadTx();
        var sessionKey = KeyCodec.StringKey(sessionId);
        var total = CountOf(tx, sessionKey);
        if (total is null)
        {
            return [];
        }
        var from = Math.Max(0, total.Value - count); // newest `count`, oldest first; count<=0 mirrors reference (empty)
        return ReadOrdinals(tx, sessionKey, from, total.Value);
    }

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count)
    {
        using var tx = ReadTx();
        var sessionKey = KeyCodec.StringKey(sessionId);
        var total = CountOf(tx, sessionKey);
        if (total is null)
        {
            return [];
        }
        var to = Math.Clamp(beforeOrdinal, 0, total.Value);
        var from = Math.Max(0, to - count);
        if (from > to)
        {
            return [];
        }
        return ReadOrdinals(tx, sessionKey, from, to);
    }

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive)
    {
        using var tx = ReadTx();
        var sessionKey = KeyCodec.StringKey(sessionId);
        var total = CountOf(tx, sessionKey);
        if (total is null)
        {
            return [];
        }
        if (fromOrdinal < 0 || toOrdinalExclusive < fromOrdinal || toOrdinalExclusive > total)
        {
            throw new InvalidOperationException(
                $"Range [{fromOrdinal},{toOrdinalExclusive}) outside count={total} in {sessionId}");
        }
        return ReadOrdinals(tx, sessionKey, fromOrdinal, toOrdinalExclusive);
    }

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars)
    {
        using var tx = ReadTx();
        var sessionKey = KeyCodec.StringKey(sessionId);
        var total = CountOf(tx, sessionKey);
        if (total is null)
        {
            return new WindowPreview(sessionId, 0, []);
        }
        var from = Math.Max(0, total.Value - count);
        var items = new List<MessagePreview>();
        for (var ordinal = Math.Max(from, 0); ordinal < total; ordinal++)
        {
            var header = ReadHeader(tx, sessionKey, ordinal);
            if (header is null)
            {
                throw new InvalidOperationException($"LmdbStore: missing message header {ordinal} in {sessionId}");
            }
            var partPreviews = new List<PartPreview>(header.Parts.Length);
            for (var partIndex = 0; partIndex < header.Parts.Length; partIndex++)
            {
                var meta = header.Parts[partIndex];
                partPreviews.Add(new PartPreview(
                    PreviewKindOf(meta.Kind),
                    meta.ContentChars,
                    meta.Kind is PartKind.ToolCall or PartKind.ToolResult ? meta.ToolCallId : null,
                    PreviewTextOf(tx, sessionKey, ordinal, partIndex, meta, previewChars)));
            }
            items.Add(new MessagePreview(ordinal, header.Role, partPreviews));
        }
        return new WindowPreview(sessionId, Math.Max(from, 0), items);
    }

    public string FetchToolResult(string sessionId, long ordinal, int partIndex)
    {
        using var tx = ReadTx();
        var sessionKey = KeyCodec.StringKey(sessionId);
        var total = CountOf(tx, sessionKey)
            ?? throw new InvalidOperationException($"Unknown session {sessionId}");
        if (ordinal < 0 || ordinal >= total)
        {
            throw new InvalidOperationException(
                $"No part {partIndex} in {sessionId}@{ordinal} (count={total})");
        }
        var header = ReadHeader(tx, sessionKey, ordinal)
            ?? throw new InvalidOperationException($"LmdbStore: missing message header {ordinal} in {sessionId}");
        var meta = partIndex >= 0 && partIndex < header.Parts.Length
            ? header.Parts[partIndex]
            : throw new InvalidOperationException(
                $"No part {partIndex} in {sessionId}@{ordinal} (count={total}, parts={header.Parts.Length})");
        return meta.Kind == PartKind.ToolResult
            ? ReadContentString(tx, sessionKey, ordinal, partIndex)
            : throw new InvalidOperationException(
                $"Part {partIndex} in {sessionId}@{ordinal} is {meta.Kind}, not a tool result");
    }

    // ---- mutations: exactly one write transaction per call ----

    public long AppendMessage(string sessionId, MessageRecord message)
    {
        var sessionKey = KeyCodec.StringKey(sessionId);
        using var tx = BeginWrite();
        var ordinal = CountOf(tx, sessionKey) ?? 0;
        PutMessage(tx, sessionKey, ordinal, message);
        tx.Put(DbSessions, sessionKey, StoreCodec.EncodeSessionCount(ordinal + 1));
        tx.Commit();
        return ordinal;
    }

    public void StreamDraft(string sessionId, string streamId, string chunk)
    {
        var sessionKey = KeyCodec.StringKey(sessionId);
        using var tx = BeginWrite();
        var active = ActiveStreamId(tx, sessionKey);
        if (active is { } existing && existing != streamId)
        {
            throw new InvalidOperationException(
                $"Session {sessionId} already has active draft {existing}; only one draft per session");
        }
        var next = DraftNextIndex(tx, sessionKey, streamId) ?? 0;
        Check(tx.Put(DbDraftChunks, KeyCodec.DraftChunkKey(sessionKey, streamId, next), Encoding.UTF8.GetBytes(chunk)),
            $"StreamDraft chunk {next}");
        Check(tx.Put(DbDraftMeta, KeyCodec.DraftMetaKey(sessionKey, streamId), StoreCodec.EncodeDraftNextIndex(next + 1)),
            "StreamDraft meta");
        // Every StreamDraft call is one store write batch (chunk row + meta) committed here.
        tx.Commit();
    }

    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage)
    {
        var sessionKey = KeyCodec.StringKey(sessionId);
        using var tx = BeginWrite();
        if (DraftNextIndex(tx, sessionKey, streamId) is null)
        {
            throw new InvalidOperationException($"No active draft {streamId} in {sessionId}");
        }
        // Commit the accumulated draft exactly once: delete the chunk rows and
        // the meta row, then insert the final message, all in one transaction.
        DeleteBySessionPrefix(tx, DbDraftChunks, sessionKey);
        Check(tx.Delete(DbDraftMeta, KeyCodec.DraftMetaKey(sessionKey, streamId)), "FinalizeStream meta delete");
        var ordinal = CountOf(tx, sessionKey) ?? 0;
        PutMessage(tx, sessionKey, ordinal, finalMessage);
        tx.Put(DbSessions, sessionKey, StoreCodec.EncodeSessionCount(ordinal + 1));
        tx.Commit();
        return ordinal;
    }

    public long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages)
    {
        var sessionKey = KeyCodec.StringKey(sessionId);
        using var tx = BeginWrite();
        var count = CountOf(tx, sessionKey)
            ?? throw new InvalidOperationException($"Unknown session {sessionId}");
        if (fromOrdinal < 0 || fromOrdinal > count)
        {
            throw new InvalidOperationException(
                $"ReplaceSuffix from={fromOrdinal} outside count={count} in {sessionId}");
        }
        // Atomic suffix edit: cursor-delete the replaced message-key range and
        // its content rows, then reinsert the new suffix, in one transaction.
        DeleteBySessionPrefix(tx, DbMsgs, sessionKey, fromOrdinal);
        DeleteBySessionPrefix(tx, DbContent, sessionKey, fromOrdinal);
        for (var i = 0; i < newMessages.Count; i++)
        {
            PutMessage(tx, sessionKey, fromOrdinal + i, newMessages[i]);
        }
        var newCount = fromOrdinal + newMessages.Count;
        tx.Put(DbSessions, sessionKey, StoreCodec.EncodeSessionCount(newCount));
        tx.Commit();
        return newCount;
    }

    public void CreateAssignment(AssignmentRecord assignment)
    {
        var idKey = KeyCodec.AssignmentIdKey(assignment.AssignmentId);
        using var tx = BeginWrite();
        if (tx.Get(DbAssignId, idKey).Item1 == MDBResultCode.Success)
        {
            throw new InvalidOperationException($"Duplicate assignment id {assignment.AssignmentId}");
        }
        Check(tx.Put(DbAssignId, idKey, StoreCodec.EncodeAssignment(assignment)), "CreateAssignment record");
        Check(tx.Put(DbAssignTarget, KeyCodec.AssignmentTargetKey(KeyCodec.StringKey(assignment.TargetSessionId), assignment.AssignmentId), Empty),
            "CreateAssignment index");
        tx.Commit();
    }

    public void SettleAssignment(SettleAssignmentRequest request)
    {
        var idKey = KeyCodec.AssignmentIdKey(request.AssignmentId);
        using var tx = BeginWrite();
        var assignment = RequireAssignment(tx, request.AssignmentId);
        Check(tx.Put(DbAssignId, idKey, StoreCodec.EncodeAssignment(assignment with
        {
            Status = AssignmentStatus.Settled,
            ResultText = request.ResultText,
        })), "SettleAssignment record");
        // The target index key depends only on the (unchanged) target session.
        tx.Commit();
    }

    public AssignmentRecord GetAssignment(string assignmentId)
    {
        using var tx = ReadTx();
        return RequireAssignment(tx, assignmentId);
    }

    public void DeleteSession(string sessionId, DeletePolicy policy)
    {
        var sessionKey = KeyCodec.StringKey(sessionId);
        using var tx = BeginWrite();
        if (CountOf(tx, sessionKey) is null)
        {
            throw new InvalidOperationException($"DeleteSession of unknown session {sessionId}");
        }

        // Assignment records live on (mirroring the reference store); open ones
        // are resolved per policy, and the derived target index shrinks by all
        // entries of the deleted target.
        var openIds = new List<(string AssignmentId, AssignmentRecord Record)>();
        using (var cursor = tx.CreateCursor(DbAssignTarget))
        {
            var positioned = cursor.SetRange(sessionKey) == MDBResultCode.Success;
            while (positioned)
            {
                var current = cursor.GetCurrent();
                if (current.Item1 != MDBResultCode.Success || current.Item2.CopyToNewArray() is not { } key
                    || !KeyCodec.StartsWith(key, sessionKey))
                {
                    break;
                }
                var (assignmentId, _) = KeyCodec.ParseStringKey(key, sessionKey.Length);
                var record = RequireAssignment(tx, assignmentId);
                if (record.Status == AssignmentStatus.Open)
                {
                    openIds.Add((assignmentId, record));
                }
                Check(cursor.Delete(), "DeleteSession index delete");
                var step = cursor.Next();
                positioned = step.Item1 == MDBResultCode.Success;
            }
        }
        if (openIds.Count > 0 && policy == DeletePolicy.RequireSettled)
        {
            throw new InvalidOperationException(
                $"DeleteSession {sessionId}: {openIds.Count} open assignment(s) violate the RequireSettled invariant");
        }
        foreach (var open in openIds)
        {
            Check(tx.Put(DbAssignId, KeyCodec.AssignmentIdKey(open.AssignmentId),
                StoreCodec.EncodeAssignment(open.Record with { Status = AssignmentStatus.Cancelled })), "DeleteSession cancel");
        }

        DeleteBySessionPrefix(tx, DbMsgs, sessionKey); // message headers, incl. every payload reference
        DeleteBySessionPrefix(tx, DbContent, sessionKey); // spilled content payloads
        DeleteBySessionPrefix(tx, DbDraftChunks, sessionKey); // any persisted stream drafts
        DeleteBySessionPrefix(tx, DbDraftMeta, sessionKey);
        Check(tx.Delete(DbSessions, sessionKey), "DeleteSession meta delete");
        tx.Commit();
    }

    public StoreStatistics Sample() => new(GC.GetTotalAllocatedBytes(precise: false));

    // ---- LMDB plumbing ----

    private static class DbNames
    {
        public const string Sessions = "sessions";
        public const string Messages = "msgs";
        public const string Content = "content";
        public const string AssignId = "assign-id";
        public const string AssignTarget = "assign-target";
        public const string DraftChunks = "draft-chunks";
        public const string DraftMeta = "draft-meta";
    }

    private static readonly byte[] Empty = [];

    private LightningTransaction BeginWrite() => Env().BeginTransaction();

    private LightningTransaction ReadTx() => Env().BeginTransaction(TransactionBeginFlags.ReadOnly);

    private LightningEnvironment Env() =>
        _env ?? throw new InvalidOperationException("LmdbStore: call Open(storeRoot, attachmentRoot) first");

    // Local aliases keep the dispatch code readable; each is resolved against the disposed-checked field set.
    private LightningDatabase DbSessions => _dbSessions ?? throw NotOpen();
    private LightningDatabase DbMsgs => _dbMsgs ?? throw NotOpen();
    private LightningDatabase DbContent => _dbContent ?? throw NotOpen();
    private LightningDatabase DbAssignId => _dbAssignId ?? throw NotOpen();
    private LightningDatabase DbAssignTarget => _dbAssignTarget ?? throw NotOpen();
    private LightningDatabase DbDraftChunks => _dbDraftChunks ?? throw NotOpen();
    private LightningDatabase DbDraftMeta => _dbDraftMeta ?? throw NotOpen();

    private static InvalidOperationException NotOpen() => new("LmdbStore: call Open(storeRoot, attachmentRoot) first");

    private static DatabaseConfiguration Cfg() => new() { Flags = DatabaseOpenFlags.Create };

    private static void Check(MDBResultCode code, string what)
    {
        if (code != MDBResultCode.Success)
        {
            throw new InvalidOperationException($"LmdbStore: {what} failed with {code}");
        }
    }

    /// <summary>Deletes every entry whose key starts with sessionKey (optionally only at or after the given ordinal).</summary>
    private static long DeleteBySessionPrefix(LightningTransaction tx, LightningDatabase db, byte[] sessionKey, long fromOrdinal = 0)
    {
        long deleted = 0;
        byte[] start = fromOrdinal == 0 ? sessionKey : [.. sessionKey, .. KeyCodec.Int64Key(fromOrdinal)];
        using var cursor = tx.CreateCursor(db);
        var probe = cursor.SetRange(start);
        while (probe == MDBResultCode.Success)
        {
            var current = cursor.GetCurrent();
            if (current.Item1 != MDBResultCode.Success)
            {
                break;
            }
            var key = current.Item2.CopyToNewArray();
            if (!KeyCodec.StartsWith(key, sessionKey))
            {
                break;
            }
            Check(cursor.Delete(), "range delete");
            deleted++;
            if (cursor.Next().Item1 != MDBResultCode.Success)
            {
                break;
            }
        }
        return deleted;
    }

    private long? CountOf(LightningTransaction tx, byte[] sessionKey)
    {
        var get = tx.Get(DbSessions, sessionKey);
        return get.Item1 switch
        {
            MDBResultCode.Success => StoreCodec.DecodeSessionCount(get.Item3.AsSpan()),
            MDBResultCode.NotFound => null,
            _ => throw new InvalidOperationException($"LmdbStore: session count read failed with {get.Item1}"),
        };
    }

    private HeaderRecord? ReadHeader(LightningTransaction tx, byte[] sessionKey, long ordinal)
    {
        var get = tx.Get(DbMsgs, KeyCodec.MessageKey(sessionKey, ordinal));
        return get.Item1 switch
        {
            MDBResultCode.Success => StoreCodec.DecodeMessageHeader(get.Item3.AsSpan()),
            MDBResultCode.NotFound => null,
            _ => throw new InvalidOperationException($"LmdbStore: message header read failed with {get.Item1}"),
        };
    }

    private string ReadContentString(LightningTransaction tx, byte[] sessionKey, long ordinal, int partIndex)
    {
        var get = tx.Get(DbContent, KeyCodec.ContentKey(sessionKey, ordinal, partIndex));
        return get.Item1 switch
        {
            MDBResultCode.Success => Encoding.UTF8.GetString(get.Item3.AsSpan()),
            MDBResultCode.NotFound =>
                throw new InvalidOperationException(
                    $"LmdbStore: missing content {ordinal}/{partIndex} (inconsistent store)"),
            _ => throw new InvalidOperationException($"LmdbStore: content read failed with {get.Item1}"),
        };
    }

    private MessageRecord[] ReadOrdinals(LightningTransaction tx, byte[] sessionKey, long from, long toExclusive)
    {
        var messages = new List<MessageRecord>();
        for (var ordinal = from; ordinal < toExclusive; ordinal++)
        {
            var header = ReadHeader(tx, sessionKey, ordinal)
                ?? throw new InvalidOperationException($"LmdbStore: missing message header {ordinal}");
            messages.Add(DecodeMessage(tx, sessionKey, ordinal, header));
        }
        return [.. messages];
    }

    private MessageRecord DecodeMessage(LightningTransaction tx, byte[] sessionKey, long ordinal, HeaderRecord header)
    {
        var parts = new List<MessagePart>(header.Parts.Length);
        for (var partIndex = 0; partIndex < header.Parts.Length; partIndex++)
        {
            var meta = header.Parts[partIndex];
            parts.Add(meta.Kind switch
            {
                PartKind.Text => new TextPart(ReadContentString(tx, sessionKey, ordinal, partIndex)),
                PartKind.ToolCall => new ToolCallPart(
                    meta.ToolCallId ?? "", meta.Name ?? "", ReadContentString(tx, sessionKey, ordinal, partIndex)),
                PartKind.ToolResult => new ToolResultPart(
                    meta.ToolCallId ?? "", ReadContentString(tx, sessionKey, ordinal, partIndex)),
                PartKind.Image => new ImagePart(meta.ImageId ?? "", meta.RelativePath ?? ""),
                _ => throw new InvalidOperationException($"LmdbStore: corrupt part kind {meta.Kind}"),
            });
        }
        return new MessageRecord(header.Role, parts);
    }

    private void PutMessage(LightningTransaction tx, byte[] sessionKey, long ordinal, MessageRecord message)
    {
        var encodedParts = new List<MessagePartEncoding>(message.Parts.Count);
        for (var partIndex = 0; partIndex < message.Parts.Count; partIndex++)
        {
            var part = message.Parts[partIndex];
            (PartKind Kind, string Content, string? ToolCallId, string? Name, string? ImageId, string? RelativePath) mapped;
            switch (part)
            {
                case TextPart t: mapped = (PartKind.Text, t.Content, null, null, null, null); break;
                case ToolCallPart c: mapped = (PartKind.ToolCall, c.ArgsJson, c.ToolCallId, c.Name, null, null); break;
                case ToolResultPart r: mapped = (PartKind.ToolResult, r.Content, r.ToolCallId, null, null, null); break;
                case ImagePart i: mapped = (PartKind.Image, i.RelativePath, null, null, i.ImageId, i.RelativePath); break;
                default: throw new InvalidOperationException($"LmdbStore: unknown part type {part.GetType().Name}");
            }
            var preview = mapped.Content.Length > 0
                ? mapped.Content[..Math.Min(PreviewBudgetChars, mapped.Content.Length)]
                : ""; // leading prefix only; image kind previews its relative path
            encodedParts.Add(new MessagePartEncoding(
                mapped.Kind, mapped.Content.Length, preview, mapped.ToolCallId, mapped.Name, mapped.ImageId,
                mapped.RelativePath, mapped.Content));
            if (mapped.Kind is PartKind.Text or PartKind.ToolCall or PartKind.ToolResult)
            {
                Check(tx.Put(DbContent, KeyCodec.ContentKey(sessionKey, ordinal, partIndex), Encoding.UTF8.GetBytes(mapped.Content)),
                    "message content put");
            }
        }
        Check(tx.Put(DbMsgs, KeyCodec.MessageKey(sessionKey, ordinal),
            StoreCodec.EncodeMessageHeader(message.Role, encodedParts)), "message header put");
    }

    private static string PreviewKindOf(PartKind kind) => kind switch
    {
        PartKind.Text => "text",
        PartKind.ToolCall => "toolCall",
        PartKind.ToolResult => "toolResult",
        PartKind.Image => "image",
        _ => "unknown",
    };

    /// <summary>
    /// Exact leading content prefix of at most previewChars characters. Reads
    /// only the stored prefix from the header; the full content record is
    /// fetched (targeted) only when the caller explicitly asks for a preview
    /// longer than the stored budget.
    /// </summary>
    private string PreviewTextOf(LightningTransaction tx, byte[] sessionKey, long ordinal, int partIndex, PartMeta meta, int previewChars)
    {
        var take = Math.Min(previewChars, (int)Math.Min(meta.ContentChars, int.MaxValue));
        if (take <= 0)
        {
            return "";
        }
        if (take <= meta.StoredPreview.Length)
        {
            return meta.StoredPreview[..take];
        }
        if (meta.Kind == PartKind.Image)
        {
            return (meta.RelativePath ?? "")[..take];
        }
        return ReadContentString(tx, sessionKey, ordinal, partIndex)[..take];
    }

    private string? ActiveStreamId(LightningTransaction tx, byte[] sessionKey)
    {
        using var cursor = tx.CreateCursor(DbDraftMeta);
        if (cursor.SetRange(sessionKey) != MDBResultCode.Success)
        {
            return null;
        }
        var current = cursor.GetCurrent();
        if (current.Item1 != MDBResultCode.Success)
        {
            return null;
        }
        var key = current.Item2.CopyToNewArray();
        if (!KeyCodec.StartsWith(key, sessionKey))
        {
            return null;
        }
        return KeyCodec.ParseStringKey(key, sessionKey.Length).Value;
    }

    private long? DraftNextIndex(LightningTransaction tx, byte[] sessionKey, string streamId)
    {
        var get = tx.Get(DbDraftMeta, KeyCodec.DraftMetaKey(sessionKey, streamId));
        return get.Item1 switch
        {
            MDBResultCode.Success => StoreCodec.DecodeDraftNextIndex(get.Item3.AsSpan()),
            MDBResultCode.NotFound => null,
            _ => throw new InvalidOperationException($"LmdbStore: draft meta read failed with {get.Item1}"),
        };
    }

    private AssignmentRecord RequireAssignment(LightningTransaction tx, string assignmentId)
    {
        var get = tx.Get(DbAssignId, KeyCodec.AssignmentIdKey(assignmentId));
        return get.Item1 switch
        {
            MDBResultCode.Success => StoreCodec.DecodeAssignment(assignmentId, get.Item3.AsSpan()),
            MDBResultCode.NotFound => throw new InvalidOperationException($"Unknown assignment {assignmentId}"),
            _ => throw new InvalidOperationException($"LmdbStore: assignment read failed with {get.Item1}"),
        };
    }

    private SessionMeta[] ListSessionsMetaIn(LightningTransaction tx)
    {
        var metas = new List<SessionMeta>();
        using var cursor = tx.CreateCursor(DbSessions);
        while (true)
        {
            if (cursor.Next().Item1 != MDBResultCode.Success)
            {
                break;
            }
            var current = cursor.GetCurrent();
            if (current.Item1 != MDBResultCode.Success)
            {
                break;
            }
            var key = current.Item2.CopyToNewArray();
            metas.Add(new SessionMeta(KeyCodec.ParseStringKey(key).Value, StoreCodec.DecodeSessionCount(current.Item3.AsSpan())));
        }
        // Sessions iterated in LMDB key order, which equals StringComparer.Ordinal
        // order by the KeyCodec encoding property.
        return [.. metas];
    }
}