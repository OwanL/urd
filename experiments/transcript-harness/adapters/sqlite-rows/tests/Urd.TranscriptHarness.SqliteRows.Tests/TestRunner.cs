using System.Text;
using Urd.TranscriptHarness.Adapter.SqliteRows;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.SqliteRows.Tests;

// Focused Stage-0 tests for the SqliteRows family-1 adapter. In-process
// (contract level); the IPC/child-process path is exercised by the shared
// runner smoke with --store-assembly. Fixtures live in OS temp only.

public static class TestRunner
{
    private static int _failures;
    private static int _passed;

    public static int Main()
    {
        Run("round trip + non-ASCII content", RoundTripNonAscii);
        Run("bounded preview metadata (never full payload)", BoundedPreview);
        Run("preview fallback beyond stored prefix", PreviewFallbackBeyondStoredPrefix);
        Run("streaming batches + finalize + per-batch persistence", StreamingBatches);
        Run("suffix edits: renumber, truncate, out-of-range, full replace, empty session", SuffixEdits);
        Run("assignments + delete policies", AssignmentsAndDeletes);
        Run("reopen preserves all acknowledged data", ReopenPersistence);
        Run("read semantics: unknown sessions, empty windows, clamps", ReadSemantics);
        Run("draft rules: one per session, cross-session id, finalize unknown", StreamRules);

        Console.WriteLine($"passed={_passed} failed={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action action)
    {
        try
        {
            action();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    private static string TempDir(string leaf)
    {
        var dir = Path.Combine(Path.GetTempPath(), "urd-sqlite-rows-tests", leaf);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return dir;
    }

    private static SqliteRowsTranscriptStore Open(string dir)
    {
        var store = new SqliteRowsTranscriptStore();
        store.Open(dir, Path.Combine(dir, "attachments"));
        return store;
    }

    private static void Assert(bool condition, string because)
    {
        if (!condition) throw new InvalidOperationException(because);
    }

    private static void AssertEquivalent(Action action, string because)
    {
        try { action(); }
        catch (Exception ex) { throw new InvalidOperationException(because, ex); }
    }

    private static void CompareContents(IReadOnlyList<MessageRecord> actual, IReadOnlyList<MessageRecord> expected, string label)
    {
        Assert(actual.Count == expected.Count, $"{label}: {actual.Count} records != expected {expected.Count}");
        for (var i = 0; i < expected.Count; i++)
        {
            Assert(actual[i].Role == expected[i].Role, $"{label}@{i} role mismatch");
            Assert(actual[i].Parts.Count == expected[i].Parts.Count, $"{label}@{i} part count mismatch");
            for (var p = 0; p < expected[i].Parts.Count; p++)
            {
                var ok = (actual[i].Parts[p], expected[i].Parts[p]) switch
                {
                    (TextPart a, TextPart b) => a.Content == b.Content,
                    (ToolCallPart a, ToolCallPart b) => a.ToolCallId == b.ToolCallId && a.Name == b.Name && a.ArgsJson == b.ArgsJson,
                    (ToolResultPart a, ToolResultPart b) => a.ToolCallId == b.ToolCallId && a.Content == b.Content,
                    (ImagePart a, ImagePart b) => a.ImageId == b.ImageId && a.RelativePath == b.RelativePath,
                    _ => false,
                };
                Assert(ok, $"{label}@{i}[{p}] part content mismatch");
            }
        }
    }

    private const string NonAscii = "日常 \u6bcf\u65e5\numlauts äöü — em-dash \U0001F600 emoji combining é\u0301";

    private static readonly string[] Roles = ["user", "assistant"];

    private static void RoundTripNonAscii()
    {
        var dir = TempDir("round-trip");
        using var store = Open(dir);
        var texts = new[]
        {
            NonAscii,
            "",
            new string('x', 1024),
            "tab\t\nnewlines\r\n\u0000 null",
        };
        for (var i = 0; i < texts.Length; i++)
        {
            var ordinal = store.AppendMessage("s-alpha", MessageRecord.Single(Roles[i % 2], new TextPart(texts[i])));
            Assert(ordinal == i, $"append ordinal {ordinal} != {i}");
        }
        store.CreateAssignment(new AssignmentRecord("a-1", "s-alpha", "watch", AssignmentStatus.Open, null));
        var metas = store.ListSessionsMeta();
        Assert(metas.Length == 1 && metas[0].SessionId == "s-alpha" && metas[0].MessageCount == texts.Length,
            "session meta mismatch");

        var fetched = store.OpenRecent("s-alpha", texts.Length);
        CompareContents(fetched, texts.Select((t, i) => MessageRecord.Single(Roles[i % 2], new TextPart(t))).ToArray(),
            "non-ASCII round trip");
        ThrowsBecause(() => store.FetchToolResult("s-alpha", 0, 0), "FetchToolResult on a text part must fail");
    }

    private static void BoundedPreview()
    {
        var dir = TempDir("bounded-preview");
        using var store = Open(dir);
        var large = new string('L', 64 * 1024); // 64 KiB tool result
        store.AppendMessage("s-prev", MessageRecord.Single("user", new TextPart("hello world")));
        Assert(store.AppendMessage("s-prev", new MessageRecord("assistant",
        [
            new ToolCallPart("call-1", "scan", "{}"),
            new ToolResultPart("call-1", large),
            new ImagePart("img-1", "0/img-1.bin"),
            new TextPart("after"),
        ])) == 1, "append ordinal");

        var preview = store.OpenRecentPreview("s-prev", 10, 24);
        Assert(preview.FromOrdinal == 0, $"preview from {preview.FromOrdinal}");
        Assert(preview.Items.Count == 2, "preview item count");
        var m0 = preview.Items[0];
        Assert(m0.Ordinal == 0 && m0.Role == "user", "preview window ordering");
        Assert(m0.Parts.Count == 1 && m0.Parts[0].Kind == "text" && m0.Parts[0].ToolCallId is null &&
               m0.Parts[0].ContentChars == "hello world".Length && m0.Parts[0].Preview == "hello world",
            "preview text part metadata");
        var m1 = preview.Items[1];
        Assert(m1.Ordinal == 1 && m1.Role == "assistant", "preview message metadata");
        Assert(m1.Parts.Count == 4, "preview part count");
        Assert(m1.Parts[0].Kind == "toolCall", "preview first part is the tool call");
        Assert(m1.Parts[1].Kind == "toolResult" && m1.Parts[1].ToolCallId == "call-1" &&
               m1.Parts[1].ContentChars == large.Length, "preview toolResult metadata");
        Assert(m1.Parts[1].Preview.Length <= 24, "preview cap violated");
        Assert(m1.Parts[1].Preview == large[..24], "preview prefix mismatch");
        Assert(m1.Parts[2].Kind == "image" && m1.Parts[2].ContentChars == "0/img-1.bin".Length && m1.Parts[2].ToolCallId is null,
            "preview image metadata (content = relative path, oracle mapping)");
        Assert(m1.Parts[2].Preview == "0/img-1.bin", "image preview = relative path");
        Assert(m1.Parts[3].ContentChars == "after".Length && m1.Parts[3].Preview == "after", "short text preview exact");
        Assert(m1.Parts[0].Kind == "toolCall" && m1.Parts[0].ContentChars == 2 && m1.Parts[0].Preview == "{}",
            "toolCall preview uses args text");
        Assert(preview.Items[0].Ordinal == 0 && preview.Items[0].Role == "user", "preview window ordering");
    }

    private static void PreviewFallbackBeyondStoredPrefix()
    {
        var dir = TempDir("preview-fallback");
        using var store = Open(dir);
        var content = new string('c', 512);
        store.AppendMessage("s-fb", MessageRecord.Single("user", new ToolResultPart("call-1", content)));

        var withinCap = store.OpenRecentPreview("s-fb", 10, 100); // <= stored prefix: pure metadata path
        Assert(withinCap.Items[0].Parts[0].Preview == content[..100], "within-cap preview");

        var beyondCap = store.OpenRecentPreview("s-fb", 10, 300); // > 256: fallback path
        Assert(beyondCap.Items[0].Parts[0].Preview == content[..300], "beyond-cap fallback preview");

        var longerThanContent = store.OpenRecentPreview("s-fb", 10, 2048); // > content length: full content is legal
        Assert(longerThanContent.Items[0].Parts[0].Preview == content, "longer-than-content preview");
    }

    private static void StreamingBatches()
    {
        var dir = TempDir("streaming");
        var storeRoot = Path.Combine(dir, "store");
        using (var store = Open(storeRoot))
        {
            Assert(store.AppendMessage("s-str", MessageRecord.Single("user", new TextPart("prompt"))) == 0, "baseline append");

            // Three distinct StreamDraft calls: three persisted write batches.
            store.StreamDraft("s-str", "stream-1", "batch one ");
            store.StreamDraft("s-str", "stream-1", "batch two ");
            store.StreamDraft("s-str", "stream-1", "batch three");

            // Nothing partial is ever served: the draft is not a message yet.
            Assert(store.ListSessionsMeta()[0].MessageCount == 1, "draft leaked into message history");
            Assert(store.OpenRecent("s-str", 10).Length == 1, "draft visible via reads");

            // Per-batch persistence: the accumulated draft text must already be
            // in the store before finalize (observable in the owned schema).
            var accumulated = ReadDraftText(storeRoot, "s-str", "stream-1");
            Assert(accumulated == "batch one batch two batch three", $"'{accumulated}'");

            var ordinal = store.FinalizeStream("s-str", "stream-1",
                MessageRecord.Single("assistant", new TextPart("batch one batch two batch three")));
            Assert(ordinal == 1, "finalize ordinal");
            var after = store.OpenRecent("s-str", 10);
            Assert(after.Length == 2 && ((TextPart)after[1].Parts[0]).Content == "batch one batch two batch three",
                "finalized message content");
        }
    }

    private static void StreamRules()
    {
        var dir = TempDir("stream-rules");
        using var store = Open(dir);
        store.StreamDraft("s-a", "stream-a", "x");

        var secondStreamForSameSession = new Action(() => store.StreamDraft("s-a", "stream-a2", "y"));
        ThrowsBecause(secondStreamForSameSession, "different streamId while one active must fail");

        var crossSessionId = new Action(() => store.StreamDraft("s-b", "stream-a", "y"));
        ThrowsBecause(crossSessionId, "same streamId active in another session must fail");

        Assert(store.ListSessionsMeta().Length == 0, "draft-only sessions must not appear in session index");

        var finalizeUnknown = new Action(() => store.FinalizeStream("s-a", "no-such-stream", MessageRecord.Single("assistant", new TextPart("x"))));
        ThrowsBecause(finalizeUnknown, "finalize of unknown draft must fail");

        store.FinalizeStream("s-a", "stream-a", MessageRecord.Single("assistant", new TextPart("x")));
        Assert(store.ListSessionsMeta()[0].MessageCount == 1, "finalized draft missing");

        // After finalize the id is free again for a new stream in that session.
        AssertEquivalent(() => store.StreamDraft("s-a", "stream-a2", "next"), "stream id reuse after finalize must be legal");
        store.FinalizeStream("s-a", "stream-a2", MessageRecord.Single("assistant", new TextPart("next")));
    }

    private static void SuffixEdits()
    {
        var dir = TempDir("suffix-edits");
        using var store = Open(dir);
        for (var i = 0; i < 5; i++)
        {
            store.AppendMessage("s-edit", MessageRecord.Single("user", new TextPart($"m{i}")));
        }
        AppendToolResult(store, "s-edit", "call-e", "tail result", Roles);

        // Renumber: replace [2..) with three new messages.
        var replacement = new MessageRecord[]
        {
            new("user", [new TextPart("r0")]),
            new("assistant", [new TextPart("r1")]),
            new("user", [new TextPart("r2")]),
        };
        var count = store.ReplaceSuffix("s-edit", 2, replacement);
        Assert(count == 5, $"replace count {count}");
        var window = store.ReadRange("s-edit", 0, 5);
        Assert(((TextPart)window[2].Parts[0]).Content == "r0", "renumbered replacement");
        Assert(((TextPart)window[4].Parts[0]).Content == "r2", "renumbered replacement tail");
        Assert(!window[3].Parts.OfType<ToolResultPart>().Any(), "old suffix tool result must be deleted with the suffix");

        // Truncation: a shorter replacement truncates.
        count = store.ReplaceSuffix("s-edit", 2, [MessageRecord.Single("user", new TextPart("only"))]);
        Assert(count == 3, $"truncate count {count}");
        Assert(store.ReadRange("s-edit", 0, 3).Length == 3, "truncated session length");

        // Pure append: from == count is legal.
        count = store.ReplaceSuffix("s-edit", 3, [MessageRecord.Single("user", new TextPart("appended"))]);
        Assert(count == 4, $"pure-append count {count}");

        // Full replace from 0, then append continues densely at 0.
        count = store.ReplaceSuffix("s-edit", 0, [MessageRecord.Single("user", new TextPart("fresh"))]);
        Assert(count == 1, $"full-replace count {count}");
        Assert(store.AppendMessage("s-edit", MessageRecord.Single("user", new TextPart("next"))) == 1, "dense tail after full replace");

        // Out-of-range and unknown-session errors.
        ThrowsBecause(() => store.ReplaceSuffix("s-edit", 5, [MessageRecord.Single("user", new TextPart("x"))]), "from > count must fail");
        ThrowsBecause(() => store.ReplaceSuffix("s-edit", -1, [MessageRecord.Single("user", new TextPart("x"))]), "from < 0 must fail");
        ThrowsBecause(() => store.ReplaceSuffix("s-unknown", 0, [MessageRecord.Single("user", new TextPart("x"))]), "unknown-session edit must fail");

        // Truncate to zero: session stays (count 0) — deletion is separate.
        store.ReplaceSuffix("s-edit", 0, []);
        var meta = store.ListSessionsMeta().Single(m => m.SessionId == "s-edit");
        Assert(meta.MessageCount == 0, "truncated-to-zero session must stay listed with count 0");
        Assert(store.AppendMessage("s-edit", MessageRecord.Single("user", new TextPart("rebirth"))) == 0, "dense 0 after empty");

        // Edit truncated suffix (0-length session) — allowed, pure append semantics.
        AssertEquivalent(() => store.ReplaceSuffix("s-edit", 1, [MessageRecord.Single("user", new TextPart("y"))]), "from == count must be legal");
    }

    private static void AssignmentsAndDeletes()
    {
        var dir = TempDir("assignments");
        using var store = Open(dir);
        store.AppendMessage("s-del", MessageRecord.Single("user", new TextPart("payload with results")));
        AppendToolResult(store, "s-del", "call-d", "payload tool result", Roles);
        store.CreateAssignment(new AssignmentRecord("asg-open", "s-del", "open work", AssignmentStatus.Open, null));
        store.CreateAssignment(new AssignmentRecord("asg-settled", "s-del", "done work", AssignmentStatus.Open, null));
        store.CreateAssignment(new AssignmentRecord("asg-other", "s-other", "other session", AssignmentStatus.Open, null));
        store.AppendMessage("s-other", MessageRecord.Single("user", new TextPart("keep me")));
        store.SettleAssignment(new SettleAssignmentRequest("asg-settled", "settled text"));

        var got = store.GetAssignment("asg-settled");
        Assert(got.Status == AssignmentStatus.Settled && got.ResultText == "settled text", "settled state");
        Assert(store.GetAssignment("asg-open").Status == AssignmentStatus.Open, "open state");

        ThrowsBecause(() => store.CreateAssignment(store.GetAssignment("asg-open") with { Status = AssignmentStatus.Settled }),
            "duplicate assignment id must fail");
        ThrowsBecause(() => store.SettleAssignment(new SettleAssignmentRequest("no-such", "x")), "unknown settle must fail");
        ThrowsBecause(() => store.GetAssignment("no-such"), "unknown get must fail");

        // RequireSettled with an open assignment: deletion refused, data intact.
        var requireDelete = new Action(() => store.DeleteSession("s-del", DeletePolicy.RequireSettled));
        ThrowsBecause(requireDelete, "RequireSettled with open assignment must fail");
        Assert(store.ListSessionsMeta().Any(m => m.SessionId == "s-del"), "failed delete must keep the session");
        Assert(store.FetchToolResult("s-del", 1, 1) == "payload tool result", "failed delete must keep payloads");

        // CancelOpen: deletion cancels open assignments in the same logical operation.
        store.DeleteSession("s-del", DeletePolicy.CancelOpen);
        Assert(!store.ListSessionsMeta().Any(m => m.SessionId == "s-del"), "deleted session must vanish");
        var cancelled = store.GetAssignment("asg-open");
        Assert(cancelled.Status == AssignmentStatus.Cancelled, "CancelOpen must cancel open assignments");
        Assert(store.GetAssignment("asg-settled").Status == AssignmentStatus.Settled
            && store.GetAssignment("asg-settled").ResultText == "settled text", "settled assignments are history, untouched");

        // CancelOpen (s-other has one open assignment): deletion cancels in the
        // same logical operation and removes the payloads.
        store.DeleteSession("s-other", DeletePolicy.CancelOpen);
        Assert(!store.ListSessionsMeta().Any(m => m.SessionId == "s-other"), "deleted s-other must vanish");
        Assert(store.GetAssignment("asg-other").Status == AssignmentStatus.Cancelled, "s-other open assignment cancelled");

        // RequireSettled delete of a session whose assignments are all settled proceeds.
        store.SettleAssignment(new SettleAssignmentRequest("asg-settled", "settled again"));
        store.CreateAssignment(new AssignmentRecord("asg-keep", "s-keep", "work", AssignmentStatus.Open, null));
        store.AppendMessage("s-keep", MessageRecord.Single("user", new TextPart("payload")));
        store.SettleAssignment(new SettleAssignmentRequest("asg-keep", "done"));
        store.DeleteSession("s-keep", DeletePolicy.RequireSettled);
        Assert(!store.ListSessionsMeta().Any(m => m.SessionId == "s-keep"), "settled-only delete proceeds");

        ThrowsBecause(() => store.DeleteSession("s-unknown", DeletePolicy.RequireSettled), "unknown-session delete must fail");

        // Dangling-open invariant: only cancelled/settled records reference deleted sessions now.
        foreach (var assignment in new[] { "asg-open", "asg-settled", "asg-other" })
        {
            _ = store.GetAssignment(assignment);
        }
    }

    private static void ReopenPersistence()
    {
        var dir = TempDir("reopen");
        var storeRoot = Path.Combine(dir, "store");
        var large = new string('W', 300 * 1024);
        using (var store = Open(storeRoot))
        {
            for (var i = 0; i < 6; i++)
            {
                var hasLargeResult = i == 2;
                store.AppendMessage("s-a", new MessageRecord(i % 2 == 0 ? "user" : "assistant",
                    hasLargeResult
                        ? [new ToolCallPart("call-r", "probe", "{\"x\":1}"), new ToolResultPart("call-r", large)]
                        : [new TextPart($"msg-{i} {NonAscii}")]));
            }
            store.ReplaceSuffix("s-a", 4, [MessageRecord.Single("user", new TextPart("edited"))]);
            store.CreateAssignment(new AssignmentRecord("asg-1", "s-a", "title", AssignmentStatus.Open, null));
            store.SettleAssignment(new SettleAssignmentRequest("asg-1", "result"));
            // Leave an active draft mid-stream.
            store.StreamDraft("s-a", "stream-open", "draft part 1 ");
            store.StreamDraft("s-a", "stream-open", "draft part 2");
        }

        using (var reopened = Open(storeRoot))
        {
            var meta = reopened.ListSessionsMeta().Single(m => m.SessionId == "s-a");
            Assert(meta.MessageCount == 5, $"reopened count {meta.MessageCount} != 5");
            var window = reopened.ReadRange("s-a", 0, 5);
            Assert(window[0].Parts is [TextPart m0] && m0.Content.Contains('日'), "reopened non-ASCII message 0");
            Assert(((ToolResultPart)window[2].Parts[1]).Content == large, "reopened large tool result");
            Assert(window[4].Parts is [TextPart] && window[4].Parts[0] is TextPart && ((TextPart)window[4].Parts[0]).Content == "edited",
                "reopened replacement content");

            var assignment = reopened.GetAssignment("asg-1");
            Assert(assignment.Status == AssignmentStatus.Settled && assignment.ResultText == "result", "reopened assignment");

            // The active draft survived reopen and finalize completes it densely.
            var ordinal = reopened.FinalizeStream("s-a", "stream-open", MessageRecord.Single("assistant",
                new TextPart("draft part 1 draft part 2")));
            Assert(ordinal == 5, "finalize after reopen");
            Assert(reopened.ListSessionsMeta().Single(m => m.SessionId == "s-a").MessageCount == 6, "count after finalize");
            var tail = reopened.OpenRecent("s-a", 1);
            Assert(tail.Length == 1 && tail[0].Parts is [TextPart tailText] && tailText.Content == "draft part 1 draft part 2",
                "finalized draft content after reopen");

            // Bounded preview still exact after reopen.
            var preview = reopened.OpenRecentPreview("s-a", 5, 24);
            Assert(preview.Items[^1].Ordinal == 5, "preview ordinal after reopen");
            Assert(preview.Items.Count == 5, "preview item count after reopen");
        }
    }

    private static void ReadSemantics()
    {
        var dir = TempDir("read-semantics");
        using var store = Open(dir);
        Assert(store.OpenRecent("no-such", 10).Length == 0, "unknown-session OpenRecent is an empty window");
        Assert(store.PageOlder("no-such", 10, 5).Length == 0, "unknown-session PageOlder is an empty window");
        Assert(store.ReadRange("no-such", 0, 10).Length == 0, "unknown-session ReadRange is an empty window");
        var preview = store.OpenRecentPreview("no-such", 10, 24);
        Assert(preview.Items.Count == 0 && preview.FromOrdinal == 0, "unknown-session preview is an empty window");

        for (var i = 0; i < 6; i++)
        {
            store.AppendMessage("s-r", MessageRecord.Single("user", new TextPart($"m{i}")));
        }
        Assert(store.OpenRecent("s-r", 2)[0].Parts is [TextPart first] && first.Content == "m4", "newest window oldest-first");
        Assert(store.PageOlder("s-r", 4, 3).Length == 3 && ((TextPart)store.PageOlder("s-r", 4, 3)[0].Parts[0]).Content == "m1",
            "pageOlder window");
        var clamped = store.PageOlder("s-r", 100, 3);
        Assert(clamped.Length == 3 && ((TextPart)clamped[0].Parts[0]).Content == "m3", "pageOlder clamps to history (reference parity)");
        ThrowsBecause(() => store.ReadRange("s-r", 2, 10), "ReadRange end beyond count must fail (reference parity)");
        ThrowsBecause(() => store.ReadRange("s-r", 4, 2), "ReadRange reversed window must fail");
        ThrowsBecause(() => store.FetchToolResult("s-r", 100, 0), "ordinal beyond history must fail");
        ThrowsBecause(() => store.FetchToolResult("s-r", 0, 0), "FetchToolResult on non-toolResult part must fail");
    }

    private static void AppendToolResult(ITranscriptStore store, string sessionId, string callId, string content, string[] roles)
    {
        store.AppendMessage(sessionId, new MessageRecord(
            roles[hash(sessionId) % 2],
            [new ToolCallPart(callId, "probe", "{}"), new ToolResultPart(callId, content)]));

        static int hash(string text)
        {
            var value = 0;
            foreach (var ch in text) value = value * 31 + ch;
            return Math.Abs(value);
        }
    }

    private static void ThrowsBecause(Action action, string because)
    {
        var threw = false;
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        Assert(threw, because);
    }

    private static string ReadDraftText(string storeRoot, string sessionId, string streamId)
    {
        // Read-only peek into the OWNED schema to observe per-batch persistence.
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(storeRoot, "transcript.db"),
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
        }.ToString();
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT draft_text FROM streams WHERE session_id = @s AND stream_id = @id";
        command.Parameters.AddWithValue("@s", sessionId);
        command.Parameters.AddWithValue("@id", streamId);
        var result = command.ExecuteScalar();
        Assert(result is string, "draft row must exist before finalize (per-batch persistence)");
        return (string?)result ?? "";
    }
}