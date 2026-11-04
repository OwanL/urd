using System.Globalization;
using System.Reflection;
using LiteDB;
using LiteDB.Engine;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.LiteDbStore;

// Family 3: LiteDB documents. One LiteDB file; one document per message;
// oversized part content in separately retrievable `blk` documents; per-call
// draft batches persisted as `draft` documents; assignments in `asg`; session
// message counts in the `ses` derived metadata collection (tiny, transactionally
// maintained — the only non-message projection, keeping ListSessionsMeta from
// decoding message documents). BSON is the engine's implicit content format.
// Pinned to LiteDB 5.0.21.
//
// Ordered index: msg/_id and blk/_id primary indexes carry precomputed keys
// "session\x1F<ordinal:D19>" (blk adds "\x1F<partIndex:D4>"), so (session,
// ordinal) windows are B-Tree range scans. Fixed-width D19 keys make
// lexicographic order equal numeric order within a session. CAUTION: LiteDB v5
// sorts string indexes with culture collation (default en-NZ/IgnoreCase),
// which does not agree with ordinal bytes and can leak keys across sessions
// whose ids are ordinal-prefixes of each other — so every range scan and
// range delete also carries an "S = sessionId" equality filter, making window
// isolation collation-independent. Generated session ids never contain \x1F
// (required for key uniqueness under collation-equal stripping).
//
// Every mutation runs inside an explicit LiteDB transaction (BeginTrans/
// Commit/Rollback; v5 transactions are thread-scoped, so there is no
// transaction object). Every StreamDraft call persists exactly one draft batch
// document — no buffering deferral to FinalizeStream. FinalizeStream and
// ReplaceSuffix commit their delete/insert atomically.
//
// Acknowledgement tier (declared, evidenced from the pinned v5.0.21 source):
// OS flush. Commit writes WAL log pages synchronously to the OS
// (DiskService.WriteLogDisk: "write to disk in a sync mode"; FileStream created
// with a one-page 8 KiB buffer), and no fsync/FlushToDisk call exists in the
// commit path — an ack therefore means OS-visible bytes, not power-loss
// durability. Recovery at open replays confirmed WAL transactions
// (WalIndexService.RestoreIndex) and discards never-confirmed pages, so a
// mid-commit process kill resolves to the before- or after-state and never to
// a partial message.

public sealed class LiteDbTranscriptStore : ITranscriptStore, IStoreStatistics
{
    /// <summary>Part content at or above this length lives in its own blk document.</summary>
    internal const int SpillThresholdChars = 16_000;
    /// <summary>Write-time preview prefix bound: previews never decode blk documents.</summary>
    internal const int MaxStoredPreviewChars = 4_096;

    private static readonly string EndDigits = new('9', 19);
    private static readonly string EndPartDigits = new('9', 4);

    private LiteDatabase? _db;
    private ILiteCollection<MsgDoc> _msgs = null!;
    private ILiteCollection<BlkDoc> _blks = null!;
    private ILiteCollection<DraftDoc> _drafts = null!;
    private ILiteCollection<AsgDoc> _asgs = null!;
    private ILiteCollection<SesDoc> _sessions = null!;

    // Plugin-load safety net: the shared host loads this assembly via
    // Assembly.LoadFrom, which does not consume the adapter's .deps.json, so
    // LiteDB.dll (and contracts, if not already loaded) resolves from the
    // adapter's own output directory.
    static LiteDbTranscriptStore()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name).Name;
            if (name is not ("LiteDB" or "Urd.TranscriptHarness.Contracts")) return null;
            var dir = Path.GetDirectoryName(typeof(LiteDbTranscriptStore).Assembly.Location);
            if (dir is null) return null;
            var path = Path.Combine(dir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
    }

    public StoreDescriptor Describe() => new(
        "LiteDbDocuments",
        AckTier.OsFlush,
        "LiteDB 5.0.21 defaults: direct connection, WAL journal on, checkpoint pragma 1000 pages " +
        "(auto-checkpoint after a commit when the log exceeds the pragma). Commit writes WAL pages " +
        "synchronously to the OS (FileStream.Write, 8 KiB page buffer) with no fsync/FlushToDisk call in the " +
        "pinned v5.0.21 commit path, so acks are OS-visible, not power-loss durable. Dispose runs Checkpoint() " +
        "(clean close folds the log); process kill leaves WAL pages for RestoreIndex replay at open.",
        "BSON documents: one per message, oversized part content spilled to individually retrievable blk " +
        "documents keyed (session, ordinal, part index), per-call draft batches persisted incrementally, " +
        "assignments and session counts in dedicated collections. Stored preview prefix cap: 4096 chars/part.");

    public void Open(string storeRoot, string attachmentRoot)
    {
        Directory.CreateDirectory(storeRoot);
        // TrimWhitespace=false: v5's default mapper trims leading/trailing
        // whitespace from strings, which would corrupt transcript content.
        var mapper = new BsonMapper
        {
            TrimWhitespace = false,
            SerializeNullValues = true,
        };
        _db = new LiteDatabase($"Filename={Path.Combine(storeRoot, "transcripts.db")};Connection=direct", mapper);
        EnforceOrdinalCollation();
        _msgs = _db.GetCollection<MsgDoc>("msg");
        _blks = _db.GetCollection<BlkDoc>("blk");
        _drafts = _db.GetCollection<DraftDoc>("draft");
        _asgs = _db.GetCollection<AsgDoc>("asg");
        _sessions = _db.GetCollection<SesDoc>("ses");
        _drafts.EnsureIndex(x => x.S);
        _drafts.EnsureIndex(x => x.I);
        _asgs.EnsureIndex(x => x.T);
    }

    /// <summary>Forces byte-ordinal collation on the database. LiteDB v5 sorts
    /// string indexes with a culture collation (default en-NZ/IgnoreCase),
    /// which: (a) rejects inserts of distinct keys that only differ in case or
    /// in embedded separator characters (duplicate _id within the unique
    /// primary index), (b) can leak keys across prefix-ambiguous session ids in
    /// range queries. The pragma is read-only once created, so a one-time
    /// rebuild (recollates all data, no content change) is applied whenever the
    /// stored collation is not already ordinal; later opens see the persisted
    /// pragma and skip the rebuild.</summary>
    private void EnforceOrdinalCollation()
    {
        var current = _db!.Pragma("COLLATION").AsString;
        if (current == Collation.Binary.ToString()) return;
        _db.Rebuild(new RebuildOptions { Collation = Collation.Binary });
        var enforced = _db.Pragma("COLLATION").AsString;
        if (enforced != Collation.Binary.ToString())
        {
            throw new InvalidOperationException($"LiteDB did not apply ordinal collation (rebuild produced '{enforced}')");
        }
    }

    public SessionMeta[] ListSessionsMeta() =>
        [.. _sessions.FindAll()
            .Select(s => new SessionMeta(s.S, s.C))
            .OrderBy(m => m.SessionId, StringComparer.Ordinal)];

    public MessageRecord[] OpenRecent(string sessionId, int count)
    {
        var session = _sessions.FindById(sessionId);
        if (session is null) return [];
        var from = Math.Max(0, session.C - count);
        return Window(sessionId, from, session.C, descending: true);
    }

    public MessageRecord[] PageOlder(string sessionId, long beforeOrdinal, int count)
    {
        var session = _sessions.FindById(sessionId);
        if (session is null) return [];
        var to = Math.Clamp(beforeOrdinal, 0, session.C);
        var from = Math.Max(0, to - count);
        return Window(sessionId, from, to, descending: false);
    }

    public MessageRecord[] ReadRange(string sessionId, long fromOrdinal, long toOrdinalExclusive)
    {
        var session = _sessions.FindById(sessionId);
        if (session is null) return []; // absence is only allowed for unknown sessions
        ValidateBounds(sessionId, session.C, fromOrdinal, toOrdinalExclusive);
        return Window(sessionId, fromOrdinal, toOrdinalExclusive, descending: false);
    }

    public WindowPreview OpenRecentPreview(string sessionId, int count, int previewChars)
    {
        var session = _sessions.FindById(sessionId);
        if (session is null) return new WindowPreview(sessionId, 0, []);
        var from = Math.Max(0, session.C - count);
        // Bounded decode: msg documents never carry spilled content, so every
        // document here is small regardless of large tool results in history.
        var items = new List<MessagePreview>((int)(session.C - from));
        foreach (var doc in MsgWindow(sessionId, from, session.C, descending: true))
        {
            items.Add(new MessagePreview(doc.O, doc.R, [.. doc.P.Select(p => p.PreviewOf(previewChars))]));
        }
        items.Reverse(); // newest-first window served oldest first
        return new WindowPreview(sessionId, from, items);
    }

    public string FetchToolResult(string sessionId, long ordinal, int partIndex)
    {
        var message = RequireMessage(sessionId, ordinal);
        if (partIndex < 0 || partIndex >= message.P.Count)
        {
            throw new InvalidOperationException(
                $"No part {partIndex} in {sessionId}@{ordinal} (parts={message.P.Count})");
        }
        var part = message.P[partIndex];
        if (part.T != PartKind.ToolResult)
        {
            throw new InvalidOperationException(
                $"Part {partIndex} in {sessionId}@{ordinal} is not a tool result");
        }
        return part.L
            ? _blks.FindById(BlkKey(sessionId, ordinal, partIndex))!.C
            : part.C!;
    }

    public long AppendMessage(string sessionId, MessageRecord message) =>
        InTx(() => AppendInTx(sessionId, message));

    public void StreamDraft(string sessionId, string streamId, string chunk) =>
        InTx(() =>
        {
            if (_drafts.Query().Where("S = @0 AND I != @1", sessionId, streamId).FirstOrDefault() is not null)
            {
                throw new InvalidOperationException(
                    $"Session {sessionId} already has an active draft under a different stream id");
            }
            // One persisted batch document per StreamDraft call: never deferred.
            _drafts.Insert(new DraftDoc
            {
                S = sessionId,
                I = streamId,
                Q = _drafts.Count("I = @0", streamId),
                X = chunk,
            });
        });

    public long FinalizeStream(string sessionId, string streamId, MessageRecord finalMessage) =>
        InTx(() =>
        {
            var draft = _drafts.Query().Where("I = @0", streamId).FirstOrDefault()
                ?? throw new InvalidOperationException($"No active draft {streamId} in {sessionId}");
            if (draft.S != sessionId)
            {
                throw new InvalidOperationException($"No active draft {streamId} in {sessionId}");
            }
            _drafts.DeleteMany("I = @0", streamId);
            return AppendInTx(sessionId, finalMessage);
        });

    public long ReplaceSuffix(string sessionId, long fromOrdinal, IReadOnlyList<MessageRecord> newMessages) =>
        InTx(() =>
        {
            var session = RequireSession(sessionId);
            if (fromOrdinal < 0 || fromOrdinal > session.C)
            {
                throw new InvalidOperationException(
                    $"ReplaceSuffix from={fromOrdinal} outside count={session.C} in {sessionId}");
            }
            // History replacement is atomic: removed suffix messages, their
            // spilled blocks, the renumbered reinsertions and the count update
            // commit together, so no old-ordinal keys are ever exposed.
            _msgs.DeleteMany("_id >= @0 AND _id < @1 AND S = @2", Key(sessionId, fromOrdinal), End(sessionId), sessionId);
            _blks.DeleteMany("_id >= @0 AND _id < @1 AND S = @2", BlkKey(sessionId, fromOrdinal, 0), EndBlk(sessionId), sessionId);
            var result = fromOrdinal + newMessages.Count;
            foreach (var message in newMessages)
            {
                AppendInTx(sessionId, message, fromOrdinal++);
            }
            // Cover empty/short replacements: count is authoritative, not a
            // leftover from the pre-replacement tail.
            _sessions.Upsert(new SesDoc { S = sessionId, C = result });
            return result;
        });

    public void CreateAssignment(AssignmentRecord assignment) =>
        InTx(() =>
        {
            if (_asgs.Exists("_id = @0", assignment.AssignmentId))
            {
                throw new InvalidOperationException($"Duplicate assignment id {assignment.AssignmentId}");
            }
            _asgs.Insert(new AsgDoc
            {
                I = assignment.AssignmentId,
                T = assignment.TargetSessionId,
                L = assignment.Title,
                St = Encode(assignment.Status),
                R = assignment.ResultText,
            });
        });

    public void SettleAssignment(SettleAssignmentRequest request) =>
        InTx(() =>
        {
            var assignment = RequireAssignment(request.AssignmentId);
            assignment.St = AssignmentStatusCodes.Settled;
            assignment.R = request.ResultText;
            _asgs.Update(assignment);
        });

    public AssignmentRecord GetAssignment(string assignmentId) =>
        ToRecord(RequireAssignment(assignmentId));

    public void DeleteSession(string sessionId, DeletePolicy policy) =>
        InTx(() =>
        {
            RequireSession(sessionId);
            var open = _asgs.Query()
                .Where("T = @0 AND St = @1", sessionId, AssignmentStatusCodes.Open)
                .ToArray();
            if (open.Length > 0 && policy == DeletePolicy.RequireSettled)
            {
                throw new InvalidOperationException(
                    $"DeleteSession {sessionId}: {open.Length} open assignment(s) violate the RequireSettled invariant");
            }
            foreach (var assignment in open)
            {
                assignment.St = AssignmentStatusCodes.Cancelled;
            }
            if (open.Length > 0) _asgs.Update(open);
            _msgs.DeleteMany("_id >= @0 AND _id < @1 AND S = @2", Key(sessionId, 0), End(sessionId), sessionId);
            _blks.DeleteMany("_id >= @0 AND _id < @1 AND S = @2", BlkKey(sessionId, 0, 0), EndBlk(sessionId), sessionId);
            _drafts.DeleteMany("S = @0", sessionId);
            _sessions.DeleteMany("_id = @0", sessionId);
        });

    public StoreStatistics Sample() => new(GC.GetTotalAllocatedBytes(precise: false));

    public void Dispose()
    {
        if (_db is null) return;
        // Clean close: fold the WAL log into the data file. A killed process
        // instead leaves confirmed WAL pages that replay at open.
        try
        {
            _db.Checkpoint();
        }
        catch (LiteException)
        {
            // A failed checkpoint is harmless: WAL pages remain for replay.
        }
        _db.Dispose();
        _db = null;
    }

    // ----- transaction helper -----
    //
    // LiteDB v5 explicit transactions are per-thread shared state
    // (BeginTrans()/Commit()/Rollback() on LiteDatabase); there is no
    // transaction object. InTx brackets exactly one logical store operation.

    private T InTx<T>(Func<T> operation)
    {
        if (!_db!.BeginTrans()) throw new InvalidOperationException("A transaction is already open on this thread");
        try
        {
            var result = operation();
            _db.Commit();
            return result;
        }
        catch
        {
            _db.Rollback();
            throw;
        }
    }

    private void InTx(Action operation) =>
        InTx<object?>(() =>
        {
            operation();
            return null;
        });

    // ----- shared mutation tail (inside InTx) -----

    private long AppendInTx(string sessionId, MessageRecord message, long? ordinal = null)
    {
        var at = ordinal ?? (_sessions.FindById(sessionId)?.C ?? 0);
        var parts = new List<PartDoc>(message.Parts.Count);
        for (var i = 0; i < message.Parts.Count; i++)
        {
            var part = PartDoc.Of(message.Parts[i]);
            if (part.L)
            {
                _blks.Insert(new BlkDoc { K = BlkKey(sessionId, at, i), S = sessionId, C = ContentOf(message.Parts[i]) });
            }
            parts.Add(part);
        }
        _msgs.Insert(new MsgDoc { K = Key(sessionId, at), S = sessionId, O = at, R = message.Role, P = parts });
        _sessions.Upsert(new SesDoc { S = sessionId, C = at + 1 });
        return at;
    }

    // ----- reads -----

    private IEnumerable<MsgDoc> MsgWindow(string sessionId, long from, long toExclusive, bool descending)
    {
        var query = _msgs.Query()
            .Where("_id >= @0 AND _id < @1 AND S = @2", Key(sessionId, from), Key(sessionId, toExclusive), sessionId);
        if (descending) query = query.OrderBy("_id", Query.Descending);
        return query.ToList();
    }

    /// <summary>Decodes a window, rehydrating spilled large content.</summary>
    private MessageRecord[] Window(string sessionId, long from, long toExclusive, bool descending)
    {
        if (from >= toExclusive) return [];
        var window = MsgWindow(sessionId, from, toExclusive, descending).ToArray();
        if (descending) Array.Reverse(window);
        var records = new MessageRecord[window.Length];
        for (var i = 0; i < window.Length; i++)
        {
            records[i] = window[i].ToRecord();
        }
        return records;
    }

    private MsgDoc RequireMessage(string sessionId, long ordinal)
    {
        var message = _msgs.FindById(Key(sessionId, ordinal));
        if (message is not null) return message;
        throw new InvalidOperationException(_sessions.Exists("_id = @0", sessionId)
            ? $"Unknown message {sessionId}@{ordinal}"
            : $"Unknown session {sessionId}");
    }

    private AsgDoc RequireAssignment(string assignmentId) =>
        _asgs.FindById(assignmentId) ?? throw new InvalidOperationException($"Unknown assignment {assignmentId}");

    private SesDoc RequireSession(string sessionId) =>
        _sessions.FindById(sessionId) ?? throw new InvalidOperationException($"Unknown session {sessionId}");

    private static void ValidateBounds(string sessionId, long count, long from, long to)
    {
        if (from < 0 || to < from || to > count)
        {
            throw new InvalidOperationException($"Range [{from},{to}) outside count={count} in {sessionId}");
        }
    }

    // ----- keys -----

    internal static string Key(string sessionId, long ordinal) =>
        sessionId + '\u001F' + ordinal.ToString("D19", CultureInfo.InvariantCulture);

    private static string End(string sessionId) => sessionId + '\u001F' + EndDigits;

    internal static string BlkKey(string sessionId, long ordinal, int partIndex) =>
        Key(sessionId, ordinal) + '\u001F' + partIndex.ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>Exclusive bound dominating every blk key of the session.</summary>
    private static string EndBlk(string sessionId) => sessionId + '\u001F' + EndDigits + '\u001F' + EndPartDigits;

    private static string ContentOf(MessagePart part) => part switch
    {
        TextPart t => t.Content,
        ToolCallPart c => c.ArgsJson,
        ToolResultPart r => r.Content,
        ImagePart i => i.RelativePath,
        _ => throw new InvalidOperationException($"Unsupported part type {part.GetType().Name}"),
    };

    // ----- assignments -----

    private static AssignmentRecord ToRecord(AsgDoc assignment) =>
        new(assignment.I, assignment.T, assignment.L, Decode(assignment.St), assignment.R);

    private static int Encode(AssignmentStatus status) => status switch
    {
        AssignmentStatus.Open => AssignmentStatusCodes.Open,
        AssignmentStatus.Settled => AssignmentStatusCodes.Settled,
        AssignmentStatus.Cancelled => AssignmentStatusCodes.Cancelled,
        _ => throw new InvalidOperationException($"Unknown assignment status {status}"),
    };

    private static AssignmentStatus Decode(int code) => code switch
    {
        AssignmentStatusCodes.Open => AssignmentStatus.Open,
        AssignmentStatusCodes.Settled => AssignmentStatus.Settled,
        AssignmentStatusCodes.Cancelled => AssignmentStatus.Cancelled,
        _ => throw new InvalidOperationException($"Unknown assignment status code {code}"),
    };
}

internal static class AssignmentStatusCodes
{
    public const int Open = 0;
    public const int Settled = 1;
    public const int Cancelled = 2;
}

internal static class PartKind
{
    public const int Text = 0;
    public const int ToolCall = 1;
    public const int ToolResult = 2;
    public const int Image = 3;
}

// Document POCOs. Short uppercase BSON field names; the pinned v5 default
// mapper preserves property-name casing (verified experimentally).

internal sealed class MsgDoc
{
    [BsonId] public string K { get; set; } = "";   // ordered key session\x1F<ordinal:D19>
    public string S { get; set; } = "";            // session id
    public long O { get; set; }                    // ordinal (redundant with key, read convenience)
    public string R { get; set; } = "";            // role
    public List<PartDoc> P { get; set; } = [];     // parts

    public MessageRecord ToRecord() => new(R, [.. P.Select(p => p.ToPart())]);
}

internal sealed class PartDoc
{
    public int T { get; set; }                     // kind (PartKind)
    public string? I { get; set; }                 // toolCallId / image id
    public string? M { get; set; }                 // tool call name
    public long N { get; set; }                    // full-content char length
    public string P { get; set; } = "";            // stored leading-prefix preview (≤ MaxStoredPreviewChars)
    public string? C { get; set; }                 // inline content (null when spilled to blk)
    public bool L { get; set; }                    // spilled into blk

    private static string? ToolCallIdOf(int kind, string? id) =>
        kind is PartKind.ToolCall or PartKind.ToolResult ? id : null;

    public PartPreview PreviewOf(int previewChars)
    {
        var take = Math.Clamp(previewChars, 0, Math.Min(LiteDbTranscriptStore.MaxStoredPreviewChars, P.Length));
        return new PartPreview(KindName, N, ToolCallIdOf(T, I), P[..take]);
    }

    public MessagePart ToPart() => T switch
    {
        PartKind.Text => new TextPart(C!),
        PartKind.ToolCall => new ToolCallPart(I!, M!, C!),
        PartKind.ToolResult => new ToolResultPart(I!, C!),
        PartKind.Image => new ImagePart(I!, C!),
        _ => throw new InvalidOperationException($"Unknown part kind {T}"),
    };

    private string KindName => T switch
    {
        PartKind.Text => "text",
        PartKind.ToolCall => "toolCall",
        PartKind.ToolResult => "toolResult",
        PartKind.Image => "image",
        _ => "unknown",
    };

    public static PartDoc Of(MessagePart part) => part switch
    {
        TextPart t => new PartDoc
        {
            T = PartKind.Text,
            N = t.Content.Length,
            C = t.Content,
            P = t.Content[..Math.Min(t.Content.Length, LiteDbTranscriptStore.MaxStoredPreviewChars)],
            L = t.Content.Length >= LiteDbTranscriptStore.SpillThresholdChars,
        },
        ToolCallPart c => new PartDoc
        {
            T = PartKind.ToolCall,
            I = c.ToolCallId,
            M = c.Name,
            N = c.ArgsJson.Length,
            C = c.ArgsJson,
            P = c.ArgsJson[..Math.Min(c.ArgsJson.Length, LiteDbTranscriptStore.MaxStoredPreviewChars)],
            L = c.ArgsJson.Length >= LiteDbTranscriptStore.SpillThresholdChars,
        },
        ToolResultPart r => new PartDoc
        {
            T = PartKind.ToolResult,
            I = r.ToolCallId,
            N = r.Content.Length,
            C = r.Content,
            P = r.Content[..Math.Min(r.Content.Length, LiteDbTranscriptStore.MaxStoredPreviewChars)],
            L = r.Content.Length >= LiteDbTranscriptStore.SpillThresholdChars,
        },
        ImagePart i => new PartDoc { T = PartKind.Image, I = i.ImageId, N = i.RelativePath.Length, C = i.RelativePath, P = i.RelativePath, L = false },
        _ => throw new InvalidOperationException($"Unsupported part type {part.GetType().Name}"),
    };
}

internal sealed class BlkDoc
{
    [BsonId] public string K { get; set; } = "";   // blk key session\x1F<ordinal:D19>\x1F<part:D4>
    public string S { get; set; } = "";            // session id (range/delete isolation filter)
    public string C { get; set; } = "";            // full content
}

internal sealed class DraftDoc
{
    public ObjectId Id { get; set; } = ObjectId.Empty;   // auto id (Empty → LiteDB generates)
    public string S { get; set; } = "";            // session id
    public string I { get; set; } = "";            // stream id
    public long Q { get; set; }                    // batch sequence within the stream
    public string X { get; set; } = "";            // this batch's persisted text
}

internal sealed class AsgDoc
{
    [BsonId] public string I { get; set; } = "";   // assignment id
    public string T { get; set; } = "";            // target session id
    public string L { get; set; } = "";            // title
    public int St { get; set; }                    // status code
    public string? R { get; set; }                 // result text
}

internal sealed class SesDoc
{
    [BsonId] public string S { get; set; } = "";   // session id
    public long C { get; set; }                    // message count (dense tail)
}