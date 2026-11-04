using System.Text;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Core;

// Bounded set of trace operations, matching the operation catalog in
// STORAGE-EXPERIMENT.md. Ops run in order; the generator produces them alongside
// the oracle model, so expected state stays consistent by construction.

public sealed record ScaleConfig
{
    public int Seed { get; init; } = 7;
    public int SessionCount { get; init; } = 12;
    public int MinMessages { get; init; } = 12;
    public int MaxMessages { get; init; } = 40;
    public int EditSessionCount { get; init; } = 6;
    public int DeleteSessionCount { get; init; } = 2;
    public int AssignmentCount { get; init; } = 10;
    public int StreamSessionCount { get; init; } = 8;
    public int SmallResultCount { get; init; } = 20;
    public int MediumResultKB { get; init; } = 64;
    public int MediumResultCount { get; init; } = 4;
    public int LargeResultKB { get; init; } = 256;
    public int ImageFileCount { get; init; } = 6;
    /// <summary>Named sessions with a UTF-8 CONTENT byte budget (Stage1 tier;
    /// empty for the smoke default). The budget applies to content GENERATED
    /// during the build phase; cycle appends/edits afterwards are part of the
    /// workload but not the budget accounting (actuals are recorded — see
    /// <see cref="GeneratedTrace.SessionContentBytes"/>).</summary>
    public IReadOnlyList<Stage1SessionBudget> LargeSessions { get; init; } = [];
    /// <summary>Stage1 repeated common-workload cycles over the first large
    /// session (0 = off, smoke default).</summary>
    public int RepeatCycles { get; init; } = 0;
    /// <summary>Stage1 tool-result byte cap (UTF-8). Default 256 KiB; a
    /// downsized value lets self-tests exercise many-message budget builds.</summary>
    public int Stage1ResultCapBytes { get; init; } = 256 * 1024;
    public string Notes { get; init; } = "smoke tier (small by design; large tiers are config-only)";
}

public abstract record TraceOp;

public sealed record TraceAppend(string SessionId, MessageRecord Message) : TraceOp;
public sealed record TraceStreamDraft(string SessionId, string StreamId, string Chunk) : TraceOp;
public sealed record TraceFinalize(string SessionId, string StreamId, MessageRecord FinalMessage) : TraceOp;
public sealed record TraceReplaceSuffix(string SessionId, long FromOrdinal, IReadOnlyList<MessageRecord> NewMessages) : TraceOp;
public sealed record TraceDeleteSession(string SessionId, DeletePolicy Policy) : TraceOp;
public sealed record TraceCreateAssignment(string AssignmentId, string TargetSessionId, string Title) : TraceOp;
public sealed record TraceSettle(string AssignmentId, string ResultText) : TraceOp;

public sealed record TraceListSessions : TraceOp;
public sealed record TraceOpenRecent(string SessionId, int Count) : TraceOp;
public sealed record TracePageOlder(string SessionId, long Before, int Count) : TraceOp;
public sealed record TraceReadRange(string SessionId, long From, long To) : TraceOp;
public sealed record TraceWindowPreview(string SessionId, int Count, int PreviewChars) : TraceOp;
public sealed record TraceFetchToolResult(string SessionId, long Ordinal, int PartIndex) : TraceOp;
public sealed record TraceCheckpoint : TraceOp;

// Audit trail for where a supervised kill of the store host may fire. Recorded
// only in this milestone: kill acceptance is validated together with the first
// real persistent adapter, because the reference store is memory-only and a
// kill test against it can prove nothing about durability.
public sealed record TraceCrashMarker(string Note) : TraceOp;

public static class TraceOps
{
    public const string Append = "append";
    public const string StreamDraft = "streamDraft";
    public const string Finalize = "finalize";
    public const string ReplaceSuffix = "replaceSuffix";
    public const string DeleteSession = "deleteSession";
    public const string CreateAssignment = "createAssignment";
    public const string Settle = "settle";
    public const string ListSessions = "listSessions";
    public const string OpenRecent = "openRecent";
    public const string PageOlder = "pageOlder";
    public const string ReadRange = "readRange";
    public const string WindowPreview = "windowPreview";
    public const string FetchToolResult = "fetchToolResult";
    public const string Checkpoint = "checkpoint";
    public const string CrashMarker = "crashMarker";
}

public sealed record AttachmentFixture(string ImageId, string RelativePath, int ByteCount);

/// <summary>One Stage1 named session with a UTF-8 content byte budget.</summary>
public sealed record Stage1SessionBudget(string SessionId, long Utf8Bytes);

public sealed record GeneratedTrace(
    IReadOnlyList<TraceOp> Ops,
    IReadOnlyList<AttachmentFixture> Attachments,
    /// <summary>Actual UTF-8 content bytes GENERATED for each Stage1 large
    /// session during its budget build (before cycle appends/edits); only
    /// Stage1 sessions appear here. Recorded actuals, never guessed chars.</summary>
    IReadOnlyDictionary<string, long> SessionContentBytes,
    /// <summary>Final logical content bytes of the whole model after all
    /// trace ops (post edits) — the end-state figure, separate from budgets.</summary>
    long TotalContentBytes);

public static class TraceGenerator
{
    public static GeneratedTrace Generate(ScaleConfig config)
    {
        var rng = new Random(config.Seed);
        var model = new OracleModel();
        var ops = new List<TraceOp>();
        var attachments = new List<AttachmentFixture>();

        var sessionIds = Enumerable.Range(0, config.SessionCount).Select(i => $"session-{i}").ToArray();
        for (var i = 0; i < config.ImageFileCount; i++)
        {
            attachments.Add(new AttachmentFixture($"img-{i}", $"{i % 3}/img-{i}.bin", 1024 * (1 + (i % 4))));
        }

        // Tool-result fixtures cycle through a deterministic size ladder.
        var smallTexts = MakeQueue(config.SmallResultCount, () => TextOfrng(rng, 512));
        var mediumTexts = MakeQueue(config.MediumResultCount, () => TextOfrng(rng, config.MediumResultKB * 1024));
        var largeTexts = MakeQueue(1, () => TextOfrng(rng, config.LargeResultKB * 1024));

        var assignmentIndex = 0;
        var deleteTargets = sessionIds.Take(Math.Max(0, Math.Min(config.DeleteSessionCount, sessionIds.Length))).ToHashSet();
        var assignmentTargets = sessionIds
            .Where(s => !deleteTargets.Contains(s))
            .Take(config.AssignmentCount).ToArray();
        var appendsSinceMarker = 0;

        // 1. Session content, append by append; streamed sessions draft some
        //    assistant messages and finalize them.
        for (var i = 0; i < sessionIds.Length; i++)
        {
            var sessionId = sessionIds[i];
            var count = config.MinMessages + rng.Next(config.MaxMessages - config.MinMessages + 1);
            for (var n = 0; n < count; n++)
            {
                ops.AddRange(MessageOps(model, sessionId, rng, smallTexts, mediumTexts, largeTexts,
                    streamed: i < config.StreamSessionCount && n % 5 == 2,
                    attachments, ++appendsSinceMarker));
            }
        }

        // 2. Bounded suffix edits: replace an earlier prompt, deleting the
        //    later continuation (a few extra appends simulate intervening work).
        for (var e = 0; e < Math.Min(config.EditSessionCount, sessionIds.Length); e++)
        {
            var sessionId = sessionIds[(e * 2) % sessionIds.Length];
            var from = model.Count(sessionId) / 2;
            for (var m = 0; m < 3; m++)
            {
                ops.AddRange(MessageOps(model, sessionId, rng, smallTexts, null, null, streamed: false, attachments, ++appendsSinceMarker));
            }
            var replacement = new MessageRecord[]
            {
                new("user", [new TextPart(TextOfrng(rng, 1600))]),
                new("assistant", [new TextPart(TextOfrng(rng, 1400))]),
            };
            ops.Add(new TraceCheckpoint());
            ops.Add(new TraceReplaceSuffix(sessionId, from, replacement));
            ModelOps.ReplaceSuffix(model, sessionId, from, replacement);
            ops.Add(new TraceOpenRecent(sessionId, 4));
        }

        // 3. Assignments: alternate open/settled against surviving sessions.
        foreach (var target in assignmentTargets)
        {
            var id = $"asg-{assignmentIndex++}";
            ops.Add(new TraceCreateAssignment(id, target, $"Inspect {target}"));
            ModelOps.CreateAssignment(model, new AssignmentRecord(id, target, $"Inspect {target}", AssignmentStatus.Open, null));
            if (assignmentIndex % 2 == 1)
            {
                var result = TextOfrng(rng, 220);
                ops.Add(new TraceSettle(id, result));
                ModelOps.SettleAssignment(model, new SettleAssignmentRequest(id, result));
            }
        }

        // 4. Reads across the whole set (plus timed hover/preview samples).
        ops.Add(new TraceListSessions());
        foreach (var sessionId in sessionIds)
        {
            var count = model.Count(sessionId);
            ops.Add(new TraceOpenRecent(sessionId, 50));
            if (count > 40) ops.Add(new TracePageOlder(sessionId, Math.Max(10, count / 2), 20));
            if (count > 9) ops.Add(new TraceReadRange(sessionId, 1, 9));
            if (count > 0) ops.Add(new TraceWindowPreview(sessionId, (int)Math.Min(20, count), 24));
        }
        ops.Add(new TraceCheckpoint());

        // 4b. Stage1 tier (only when configured); smoke default stays identical:
        //     this block runs before the final delete section but never names
        //     the small prefix sessions, so sections 3/5 behaviour is unchanged.
        //     Every Stage1 tool result is bounded to ≤ 256 KiB UTF-8 bytes and
        //     the per-session budget is met with recorded actuals. Large reads
        //     always precede any suffix edit (the fixture is never erased
        //     before exercising large reads).
        var stage1Bytes = new Dictionary<string, long>();
        if (config.Stage1ResultCapBytes <= 0)
        {
            throw new HarnessError("Stage1ResultCapBytes must be positive");
        }
        if (config.LargeSessions.Count > 0)
        {
            foreach (var budget in config.LargeSessions)
            {
                var consumed = 0L;
                while (consumed < budget.Utf8Bytes)
                {
                    var ordinal = model.Count(budget.SessionId);
                    var textPart = new TextPart(TextOfrng(rng, 250 + rng.Next(900)));
                    var call = new ToolCallPart($"call-{budget.SessionId}-{ordinal}-b", "bulkread", "{}");
                    var overhead = ContentUtf8Bytes(textPart) + ContentUtf8Bytes(call);
                    var remaining = budget.Utf8Bytes - consumed;
                    var resultTarget = Math.Clamp(remaining - overhead, 1, config.Stage1ResultCapBytes);
                    var result = new ToolResultPart(call.ToolCallId, BoundedText(rng, resultTarget));
                    var message = new MessageRecord(ordinal % 2 == 0 ? "user" : "assistant",
                        [textPart, call, result]);
                    ops.Add(new TraceAppend(budget.SessionId, message));
                    ModelOps.AppendMessage(model, budget.SessionId, message);
                    consumed += ContentUtf8Bytes([textPart, call, result]);
                }
                stage1Bytes[budget.SessionId] = consumed;
                ops.Add(new TraceCheckpoint());
            }
            // Repeated common workload cycles over the first large session:
            // append a bounded result message, then read paths (recent/preview/
            // page/range/targeted fetch), and a BOUNDED tail-only suffix edit
            // every second cycle — replacing only cycle-appended messages so
            // the built fixture stays intact for every later large read.
            if (config.RepeatCycles > 0)
            {
                var bigSessionId = config.LargeSessions[0].SessionId;
                var cycleStart = model.Count(bigSessionId);
                for (var cycle = 0; cycle < config.RepeatCycles; cycle++)
                {
                    var count = model.Count(bigSessionId);
                    ops.Add(new TraceOpenRecent(bigSessionId, 50));
                    ops.Add(new TraceWindowPreview(bigSessionId, Math.Min(20, (int)count), 24));
                    if (count > 40) ops.Add(new TracePageOlder(bigSessionId, Math.Max(10, count / 2), 20));
                    if (count > 9) ops.Add(new TraceReadRange(bigSessionId, 1, 9));
                    var ordinal = model.Count(bigSessionId);
                    // Cycle messages carry a modest bounded result so the
                    // targeted fetch exercises a non-trivial read without
                    // multiplying the whole budget.
                    var call = new ToolCallPart($"call-cycle-{cycle}", "scan", "{}");
                    var cycleResultTarget = Math.Min(16 * 1024, config.Stage1ResultCapBytes);
                    var cycleMessage = new MessageRecord(ordinal % 2 == 0 ? "user" : "assistant",
                    [
                        new TextPart(TextOfrng(rng, 250 + rng.Next(900))),
                        call,
                        new ToolResultPart(call.ToolCallId, BoundedText(rng, cycleResultTarget)),
                    ]);
                    ops.Add(new TraceAppend(bigSessionId, cycleMessage));
                    ModelOps.AppendMessage(model, bigSessionId, cycleMessage);
                    ops.Add(new TraceFetchToolResult(bigSessionId, ordinal, 2));
                    if (cycle % 2 == 1)
                    {
                        var from = Math.Max(cycleStart, model.Count(bigSessionId) - 2);
                        var replacement = new MessageRecord[]
                        {
                            new("user", [new TextPart(TextOfrng(rng, 1600))]),
                            new("assistant", [new TextPart(TextOfrng(rng, 1400))]),
                        };
                        ops.Add(new TraceReplaceSuffix(bigSessionId, from, replacement));
                        ModelOps.ReplaceSuffix(model, bigSessionId, from, replacement);
                    }
                }
                ops.Add(new TraceCheckpoint());
                if (config.LargeSessions.Count > 1)
                {
                    // One bounded read pass + tail-only edit over the second
                    // named (≈1 MiB) session; same read-before-edit ordering.
                    var midSessionId = config.LargeSessions[1].SessionId;
                    var midCount = model.Count(midSessionId);
                    ops.Add(new TraceOpenRecent(midSessionId, 50));
                    ops.Add(new TraceWindowPreview(midSessionId, Math.Min(20, (int)midCount), 24));
                    if (midCount > 40) ops.Add(new TracePageOlder(midSessionId, Math.Max(10, midCount / 2), 20));
                    if (midCount > 9) ops.Add(new TraceReadRange(midSessionId, 1, 9));
                    ops.Add(new TraceCheckpoint());
                    if (midCount >= 6)
                    {
                        var from = model.Count(midSessionId) - 3;
                        var replacement = new MessageRecord[]
                        {
                            new("user", [new TextPart(TextOfrng(rng, 900))]),
                        };
                        ops.Add(new TraceReplaceSuffix(midSessionId, from, replacement));
                        ModelOps.ReplaceSuffix(model, midSessionId, from, replacement);
                    }
                }
            }
        }

        // 5. Final-closure deletes under the settled-only invariant, then reads.
        for (var d = 0; d < deleteTargets.Count; d++)
        {
            var sessionId = sessionIds[d];
            var id = $"asg-del-{d}";
            var result = TextOfrng(rng, 120);
            ops.Add(new TraceCreateAssignment(id, sessionId, "final report"));
            ops.Add(new TraceSettle(id, result));
            ModelOps.CreateAssignment(model, new AssignmentRecord(id, sessionId, "final report", AssignmentStatus.Open, null));
            ModelOps.SettleAssignment(model, new SettleAssignmentRequest(id, result));
            ops.Add(new TraceCheckpoint());
            ops.Add(new TraceDeleteSession(sessionId, DeletePolicy.RequireSettled));
            ModelOps.DeleteSession(model, sessionId, DeletePolicy.RequireSettled);
        }
        ops.Add(new TraceListSessions());
        ops.Add(new TraceCheckpoint());

        // Final logical content bytes per session, measured on the oracle's
        // end state (post edits) — UTF-8 bytes of every part's content.
        // Stage1 large sessions instead record their budget-build actuals
        // (consumed during generation, before cycle appends/edits): those are
        // the recorded fixture totals the budget describes.
        var finalSessionBytes = new Dictionary<string, long>();
        foreach (var (session, messages) in model.Sessions)
        {
            finalSessionBytes[session] = messages.Sum(MessageContentUtf8Bytes);
        }
        foreach (var (session, buildBytes) in stage1Bytes)
        {
            finalSessionBytes[session] = buildBytes;
        }
        return new GeneratedTrace(ops, attachments, finalSessionBytes, finalSessionBytes.Values.Sum());
    }

    private static List<TraceOp> MessageOps(
        OracleModel model, string sessionId, Random rng,
        Queue<string> smallTexts, Queue<string>? mediumTexts, Queue<string>? largeTexts,
        bool streamed, IReadOnlyList<AttachmentFixture> attachments, int appendsSinceMarker)
    {
        var ops = new List<TraceOp>();
        var ordinal = model.Count(sessionId);
        var parts = new List<MessagePart>
        {
            new TextPart(TextOfrng(rng, 250 + rng.Next(900))),
        };
        var toolResultPartIndices = new List<int>();

        if (smallTexts.TryDequeue(out var small))
        {
            var resultIndex = parts.Count + 1; // call then result
            parts.Add(new ToolCallPart($"call-{sessionId}-{ordinal}-s", "probe", "{\"depth\":1}"));
            parts.Add(new ToolResultPart($"call-{sessionId}-{ordinal}-s", small));
            toolResultPartIndices.Add(resultIndex);
        }
        if (mediumTexts != null && mediumTexts.TryDequeue(out var medium))
        {
            var resultIndex = parts.Count + 1;
            parts.Add(new ToolCallPart($"call-{sessionId}-{ordinal}-m", "scan", "{}"));
            parts.Add(new ToolResultPart($"call-{sessionId}-{ordinal}-m", medium));
            toolResultPartIndices.Add(resultIndex);
        }
        if (largeTexts != null && largeTexts.TryDequeue(out var large))
        {
            var resultIndex = parts.Count + 1;
            parts.Add(new ToolCallPart($"call-{sessionId}-{ordinal}-l", "bulkread", "{}"));
            parts.Add(new ToolResultPart($"call-{sessionId}-{ordinal}-l", large));
            toolResultPartIndices.Add(resultIndex);
        }
        if (ordinal % 8 == 3 && attachments.Count > 0)
        {
            var image = attachments[(int)(ordinal % attachments.Count)];
            parts.Add(new ImagePart(image.ImageId, image.RelativePath));
        }
        var message = new MessageRecord((ordinal + rng.Next(2)) % 2 == 0 ? "user" : "assistant", parts);

        if (streamed)
        {
            var streamId = $"stream-{sessionId}-{ordinal}";
            foreach (var chunk in ChunksOf(message.Parts.OfType<TextPart>().First().Content, 3))
            {
                ops.Add(new TraceStreamDraft(sessionId, streamId, chunk));
            }
            model.ActiveDrafts.Add(streamId); // generation scaffold only
            ops.Add(new TraceFinalize(sessionId, streamId, message));
            ModelOps.FinalizeStream(model, sessionId, streamId, message);
        }
        else
        {
            ops.Add(new TraceAppend(sessionId, message));
            ModelOps.AppendMessage(model, sessionId, message);
        }

        // Timed hover samples right after the message is appended, when the
        // model still holds exactly the content the store just received.
        foreach (var partIndex in toolResultPartIndices)
        {
            ops.Add(new TraceFetchToolResult(sessionId, ordinal, partIndex));
        }

        if (appendsSinceMarker % 25 == 0)
        {
            ops.Add(new TraceCrashMarker($"after session={sessionId} ordinal={ordinal}"));
        }
        return ops;
    }

    private static Queue<T> MakeQueue<T>(int count, Func<T> make) => new(Enumerable.Range(0, count).Select(_ => make()));

    /// <summary>Per-part content in UTF-8 bytes (the recorded unit: actual
    /// generated payload bytes, not characters). Image parts count by their
    /// stable relative-path reference; the fixture bytes live on disk.</summary>
    private static long ContentUtf8Bytes(MessagePart part) => part switch
    {
        TextPart text => Encoding.UTF8.GetByteCount(text.Content),
        ToolCallPart call => Encoding.UTF8.GetByteCount(call.ArgsJson),
        ToolResultPart result => Encoding.UTF8.GetByteCount(result.Content),
        ImagePart image => Encoding.UTF8.GetByteCount(image.RelativePath),
        _ => throw new HarnessError($"Unknown part type {part.GetType().Name}"),
    };

    private static long ContentUtf8Bytes(IReadOnlyList<MessagePart> parts) => parts.Sum(ContentUtf8Bytes);

    private static long MessageContentUtf8Bytes(OracleModel.ExpectedMessage message) => ContentUtf8Bytes(message.Parts);

    /// <summary>
    /// Stage1 bounded tool-result text: UTF-8 byte count of the returned text
    /// is at most <paramref name="byteTarget"/> (never above), measured on the
    /// actual returned string. The alphabet's multi-byte symbols make UTF-8
    /// bytes longer than the string length, so the trim is proportional and
    /// slicing stays safe (the alphabet holds no surrogate pairs).
    /// </summary>
    private static string BoundedText(Random rng, long byteTarget)
    {
        var text = TextOfrng(rng, (int)Math.Min(int.MaxValue, Math.Max(1, byteTarget) * 3));
        var bytes = Encoding.UTF8.GetByteCount(text);
        return bytes <= byteTarget ? text : text[..(int)(text.Length * byteTarget / bytes)];
    }

    /// <summary>Stage1 tool-result byte cap: every generated result stays
    /// under <see cref="ScaleConfig.Stage1ResultCapBytes"/> (default 256 KiB).</summary>
    public const int Stage1ResultCapBytes = 256 * 1024;

    private static IEnumerable<string> ChunksOf(string text, int pieces)
    {
        var size = (text.Length / pieces) + 1;
        for (var i = 0; i < text.Length; i += size) yield return text[i..Math.Min(text.Length, i + size)];
    }

    private const string Alphabet = "abcdefghij klmnopqrstuvwxyzABCDEF 0123456789\n\u00e4\u00f6\u00fc \u2014 \u6bcf\u65e5 \u4e16\u754c";

    private static string TextOfrng(Random rng, int length)
    {
        var sb = new StringBuilder(length);
        for (var i = 0; i < length; i++) sb.Append(Alphabet[rng.Next(Alphabet.Length)]);
        return sb.ToString();
    }
}
// No extension helpers: keep generator code explicit.