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
    public const string FetchToolResult = "fetchToolResult";
    public const string Checkpoint = "checkpoint";
    public const string CrashMarker = "crashMarker";
}

public sealed record AttachmentFixture(string ImageId, string RelativePath, int ByteCount);

public sealed record GeneratedTrace(IReadOnlyList<TraceOp> Ops, IReadOnlyList<AttachmentFixture> Attachments);

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

        // 4. Reads across the whole set.
        ops.Add(new TraceListSessions());
        foreach (var sessionId in sessionIds)
        {
            var count = model.Count(sessionId);
            ops.Add(new TraceOpenRecent(sessionId, 50));
            if (count > 40) ops.Add(new TracePageOlder(sessionId, Math.Max(10, count / 2), 20));
            if (count > 9) ops.Add(new TraceReadRange(sessionId, 1, 9));
        }
        ops.Add(new TraceCheckpoint());

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

        return new GeneratedTrace(ops, attachments);
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

        if (smallTexts.TryDequeue(out var small))
        {
            parts.Add(new ToolCallPart($"call-{sessionId}-{ordinal}-s", "probe", "{\"depth\":1}"));
            parts.Add(new ToolResultPart($"call-{sessionId}-{ordinal}-s", small));
        }
        if (mediumTexts != null && mediumTexts.TryDequeue(out var medium))
        {
            parts.Add(new ToolCallPart($"call-{sessionId}-{ordinal}-m", "scan", "{}"));
            parts.Add(new ToolResultPart($"call-{sessionId}-{ordinal}-m", medium));
        }
        if (largeTexts != null && largeTexts.TryDequeue(out var large))
        {
            parts.Add(new ToolCallPart($"call-{sessionId}-{ordinal}-l", "bulkread", "{}"));
            parts.Add(new ToolResultPart($"call-{sessionId}-{ordinal}-l", large));
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

        if (appendsSinceMarker % 25 == 0)
        {
            ops.Add(new TraceCrashMarker($"after session={sessionId} ordinal={ordinal}"));
        }
        return ops;
    }

    private static Queue<T> MakeQueue<T>(int count, Func<T> make) => new(Enumerable.Range(0, count).Select(_ => make()));

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