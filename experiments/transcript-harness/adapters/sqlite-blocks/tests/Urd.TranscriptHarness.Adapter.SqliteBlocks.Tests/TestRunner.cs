using Microsoft.Data.Sqlite;
using Urd.TranscriptHarness.Adapter.SqliteBlocks;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.SqliteBlocks.Tests;

// Focused Stage-0 tests for the SqliteBlocks family-2 adapter (in-process,
// contract level; the IPC/child-process path is exercised by the shared runner
// smoke/validate with --store-assembly). Fixtures live in OS temp only.

public static class TestRunner
{
    private static int _failures;
    private static int _passed;

    public static int Main()
    {
        Run("round trip + non-ASCII content, all part kinds", RoundTripNonAscii);
        Run("bounded preview metadata from part rows only", BoundedPreview);
        Run("preview fallback beyond stored prefix reads one block", PreviewFallback);
        Run("streaming batches + finalize + reopen mid-stream", StreamingBatches);
        Run("stream rules: one per session, cross-session id, unknown finalize", StreamRules);
        Run("suffix edits: renumber, truncate, out-of-range", SuffixEdits);
        Run("block-reference invariant + explicit orphan cleanup", BlockCleanup);
        Run("assignments + delete policies", AssignmentsAndDeletes);
        Run("reopen preserves all acknowledged data", ReopenPersistence);
        Run("read semantics: unknown sessions, empty windows, strict bounds", ReadSemantics);
        Run("fetch tool result: targeted exact content, wrong part throws", FetchToolResult);

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
        var dir = Path.Combine(Path.GetTempPath(), "urd-sqlite-blocks-tests", leaf);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return dir;
    }

    private static SqliteBlocksTranscriptStore Open(string dir)
    {
        var store = new SqliteBlocksTranscriptStore();
        store.Open(dir, Path.Combine(dir, "attachments"));
        return store;
    }

    private static void Assert(bool condition, string because)
    {
        if (!condition) throw new InvalidOperationException(because);
    }

    private static void AssertThrows(Action action, string because)
    {
        try { action(); }
        catch (Exception) { return; }
        throw new InvalidOperationException(because);
    }

    private static void AssertEquivalent(Action action, string because)
    {
        try { action(); }
        catch (Exception ex) { throw new InvalidOperationException(because, ex); }
    }

    private static List<MessageRecord> Messages(params MessageRecord[] messages) => [.. messages];

    private static int BlockCount(string dir)
    {
        var dataSource = Path.Combine(dir, "transcript.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dataSource }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM blocks";
        return (int)(long)(command.ExecuteScalar() ?? 0L);
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

    private static MessageRecord TextMessage(string role, string content) => MessageRecord.Single(role, new TextPart(content));

    // ---- test cases ----

    private static void RoundTripNonAscii()
    {
        var dir = TempDir("roundtrip");
        var messages = Messages(
            TextMessage("user", "ünïcode ✓ 中文 🚀"),
            MessageRecord.Single("assistant", new ToolCallPart("call-1", "web.search", "{\"q\": \"quotes \\\" and newlines\n\"}")),
            MessageRecord.Single("tool", new ToolResultPart("call-1", "result <>&\u0000-safe")),
            new MessageRecord("user", [new TextPart("multi"), new ImagePart("img-1", "ur.d/att/img1.png")]));
        using (var store = Open(dir))
        {
            var ordinals = new List<long>();
            foreach (var message in messages) ordinals.Add(store.AppendMessage("s1", message));
            Assert(ordinals.SequenceEqual([0, 1, 2, 3]), $"ordinals {string.Join(',', ordinals)} not dense");
        }
        using (var reopened = Open(dir))
        {
            CompareContents(reopened.ReadRange("s1", 0, 4), messages, "reopened range");
            CompareContents(reopened.OpenRecent("s1", 100), messages, "reopened openRecent");
        }
    }

    private static void BoundedPreview()
    {
        var dir = TempDir("preview");
        var big = new string('B', 40_000);
        var messages = Messages(
            TextMessage("user", "hello"),
            MessageRecord.Single("assistant", new ToolCallPart("c1", "toolname", "{}")),
            MessageRecord.Single("tool", new ToolResultPart("c1", big)),
            new MessageRecord("user", [new TextPart("pic"), new ImagePart("img", "sub/img.png")]));
        using (var store = Open(dir))
        {
            foreach (var message in messages) store.AppendMessage("pv", message);
        }
        using (var reopened = Open(dir))
        {
            var preview = reopened.OpenRecentPreview("pv", 10, 24);
            Assert(preview.FromOrdinal == 0, $"from {preview.FromOrdinal}");
            Assert(preview.Items.Count == 4, $"items {preview.Items.Count}");
            for (var i = 0; i < 4; i++)
            {
                var parts = preview.Items[i].Parts;
                for (var p = 0; p < parts.Count; p++)
                {
                    var content = messages[i].Parts[p] switch
                    {
                        TextPart t => t.Content,
                        ToolCallPart c => c.ArgsJson,
                        ToolResultPart r => r.Content,
                        ImagePart im => im.RelativePath,
                        _ => throw new InvalidOperationException(),
                    };
                    var kind = messages[i].Parts[p] switch
                    {
                        TextPart => "text",
                        ToolCallPart => "toolCall",
                        ToolResultPart => "toolResult",
                        ImagePart => "image",
                        _ => throw new InvalidOperationException(),
                    };
                    Assert(parts[p].Kind == kind, $"kind {parts[p].Kind} != {kind}");
                    Assert(parts[p].Preview.Length <= 24, $"preview too long ({parts[p].Preview.Length})");
                    Assert(parts[p].ContentChars == content.Length, $"contentChars mismatch @{i}[{p}]");
                    Assert(parts[p].Preview == content[..Math.Min(24, content.Length)], $"preview content mismatch @{i}[{p}]");
                }
            }
            var toolCallIdParts = preview.Items[1].Parts.Concat(preview.Items[2].Parts).ToList();
            Assert(toolCallIdParts.All(part => part.ToolCallId == "c1"), "toolCallIds not surfaced on call/result parts");
            Assert(preview.Items[0].Parts.All(part => part.ToolCallId is null), "call id leaked onto text part");
        }
    }

    private static void PreviewFallback()
    {
        var dir = TempDir("fallback");
        var big = new string('F', 1000); // > PreviewStoredPrefix (256)
        using (var store = Open(dir))
        {
            store.AppendMessage("fb", MessageRecord.Single("tool", new ToolResultPart("rc", big)));
        }
        using (var reopened = Open(dir))
        {
            var preview = reopened.OpenRecentPreview("fb", 5, 300);
            var part = preview.Items.Single().Parts.Single();
            Assert(part.Preview == big[..300], $"fallback preview len {part.Preview.Length}");
            var small = reopened.OpenRecentPreview("fb", 5, 100);
            Assert(small.Items.Single().Parts.Single().Preview == big[..100], "metadata path cap wrong");
            // Metadata (not content) path: same character cap contract as the reference.
            var capped = reopened.OpenRecentPreview("fb", 5, 24);
            Assert(capped.Items.Single().Parts.Single().Preview == big[..24], "capped preview mismatch");
        }
    }

    private static void StreamingBatches()
    {
        var dir = TempDir("streaming");
        using var store = Open(dir);
        store.StreamDraft("sd", "stream-A", "alpha ");
        store.StreamDraft("sd", "stream-A", "beta ");
        store.StreamDraft("sd", "stream-A", "gamma");
        var finalized = store.FinalizeStream("sd", "stream-A", TextMessage("assistant", "alpha beta gamma done"));
        Assert(finalized == 0, $"finalize ordinal {finalized}");
        var history = store.ReadRange("sd", 0, 1);
        Assert(history.Length == 1 && ((TextPart)history[0].Parts[0]).Content == "alpha beta gamma done",
            "finalize content mismatch");
        AssertEquivalent(() => store.StreamDraft("sd", "stream-A", "x"), "stream reused after finalize: allowed");
    }

    private static void StreamRules()
    {
        var dir = TempDir("stream-rules");
        using var store = Open(dir);
        store.AppendMessage("sr", TextMessage("user", "hi"));
        store.StreamDraft("sr", "stream-1", "a");
        AssertThrows(() => store.StreamDraft("sr", "stream-2", "b"), "second stream in same session not rejected");
        store.StreamDraft("other", "stream-2", "b-ok"); // different session, free id
        AssertThrows(() => store.StreamDraft("sr", "stream-2", "x"), "stream id active elsewhere not rejected");
        AssertThrows(() => store.FinalizeStream("sr", "stream-x", TextMessage("a", "x")), "unknown finalize not rejected");
        // Drafts never appear in windows/sessions even with an active stream.
        var meta = store.ListSessionsMeta();
        Assert(meta.All(m => m.MessageCount == 1), $"draft leaked into counts: {meta.FirstOrDefault(m => m.MessageCount != 1)}");
        Assert(store.OpenRecent("sr", 10).Length == 1, "draft leaked into window");
    }

    private static void SuffixEdits()
    {
        var dir = TempDir("suffix");
        var messages = new List<MessageRecord>
        {
            TextMessage("user", "m0"),
            TextMessage("assistant", "m1"),
            TextMessage("user", "m2"),
            TextMessage("assistant", "m3"),
            TextMessage("user", "m4"),
        };
        using var store = Open(dir);
        foreach (var message in messages) store.AppendMessage("se", message);

        var replacement = Messages(TextMessage("assistant", "new2"), TextMessage("user", "new3"), TextMessage("assistant", "new4"));
        var count = store.ReplaceSuffix("se", 2, replacement);
        Assert(count == 5, $"replace count {count}");
        var expected = new List<MessageRecord> { messages[0], messages[1], replacement[0], replacement[1], replacement[2] };
        CompareContents(store.ReadRange("se", 0, 5), expected, "after replace");

        count = store.ReplaceSuffix("se", 3, []);
        Assert(count == 3, $"truncate count {count}");
        CompareContents(store.ReadRange("se", 0, 3), expected.Take(3).ToList(), "after truncate");

        AssertThrows(() => store.ReplaceSuffix("se", 4, replacement), "out-of-range replace not rejected");
        AssertThrows(() => store.ReplaceSuffix("nope", 0, replacement), "unknown-session replace not rejected");
        var pureAppend = store.ReplaceSuffix("se", 3, [TextMessage("assistant", "appended")]);
        Assert(pureAppend == 4, $"pure append count {pureAppend}");
        Assert(store.OpenRecent("se", 1)[0].Role == "assistant", "pure append content");
    }

    private static void BlockCleanup()
    {
        var dir = TempDir("cleanup");
        const int totalParts = 3;
        var messages = new List<MessageRecord>
        {
            TextMessage("user", "m0"),
            MessageRecord.Single("tool", new ToolResultPart("c1", new string('c', 500))),
            MessageRecord.Single("user", new ImagePart("img", "p/img.png")),
        };
        using (var store = Open(dir))
        {
            foreach (var message in messages) store.AppendMessage("bc", message);
            Assert(BlockCount(dir) == totalParts, $"blocks after appends: {BlockCount(dir)} != {totalParts}");
            // Replace suffix from ordinal 1: the two removed parts' blocks must go.
            store.ReplaceSuffix("bc", 1, [TextMessage("assistant", "replacement")]);
            Assert(BlockCount(dir) == 2, $"blocks after replace: {BlockCount(dir)} != 2");
            // Truncating back to 0 still lists the session with zero messages.
            store.ReplaceSuffix("bc", 0, []);
            Assert(BlockCount(dir) == 0, $"blocks after truncate-all: {BlockCount(dir)}");
            store.AppendMessage("bc", TextMessage("user", "again"));
            Assert(store.ListSessionsMeta().Single(m => m.SessionId == "bc").MessageCount == 1, "zero-message session handling");
        }
        using (var store = Open(dir))
        {
            store.DeleteSession("bc", DeletePolicy.RequireSettled);
        }
        Assert(BlockCount(dir) == 0, "blocks survive session delete (orphan leak)");
        Assert(!Open(dir).ListSessionsMeta().Any(m => m.SessionId == "bc"), "session row survived delete");
    }

    private static void AssignmentsAndDeletes()
    {
        var dir = TempDir("assignments");
        using var store = Open(dir);
        store.AppendMessage("as", TextMessage("user", "content"));
        var record = new AssignmentRecord("asg-1", "as", "verify content", AssignmentStatus.Open, null);
        store.CreateAssignment(record);
        AssertThrows(() => store.CreateAssignment(record with { Title = "dup" }), "duplicate assignment not rejected");
        AssertThrows(() => store.DeleteSession("as", DeletePolicy.RequireSettled), "RequireSettled delete allowed with open assignment");
        store.DeleteSession("as", DeletePolicy.CancelOpen);
        var cancelled = store.GetAssignment("asg-1");
        Assert(cancelled.Status == AssignmentStatus.Cancelled, $"cancelled status {cancelled.Status}");
        Assert(cancelled.TargetSessionId == "as", "cancelled record lost fields");

        store.AppendMessage("as2", TextMessage("user", "more"));
        store.CreateAssignment(new AssignmentRecord("asg-2", "as2", "t", AssignmentStatus.Open, null));
        store.SettleAssignment(new SettleAssignmentRequest("asg-2", "result text"));
        var settled = store.GetAssignment("asg-2");
        Assert(settled.Status == AssignmentStatus.Settled && settled.ResultText == "result text", "settle mismatch");
        store.DeleteSession("as2", DeletePolicy.RequireSettled); // settled -> allowed
        Assert(store.GetAssignment("asg-2").Status == AssignmentStatus.Settled, "settled record must survive delete");
        AssertThrows(() => store.GetAssignment("asg-nope"), "unknown assignment fetch not rejected");
    }

    private static void ReopenPersistence()
    {
        var dir = TempDir("reopen");
        var messages = new List<MessageRecord>
        {
            TextMessage("user", "r0"),
            MessageRecord.Single("tool", new ToolResultPart("c1", new string('z', 10_000))),
        };
        using (var store = Open(dir))
        {
            store.AppendMessage("rp", messages[0]);
            store.AppendMessage("rp", messages[1]);
            store.StreamDraft("rp", "stream-R", "unfinished ");
            store.CreateAssignment(new AssignmentRecord("asg-r", "rp", "t", AssignmentStatus.Open, null));
            store.SettleAssignment(new SettleAssignmentRequest("asg-r", "settled!"));
        }
        using (var reopened = Open(dir))
        {
            CompareContents(reopened.ReadRange("rp", 0, 2), messages, "reopened");
            Assert(reopened.OpenRecent("rp", 1).Length == 1, "reopened openRecent");
            var preview = reopened.OpenRecentPreview("rp", 10, 12);
            Assert(preview.Items.Count == 2 && preview.Items[1].Parts[0].Preview == new string('z', 12), "reopened preview");
            Assert(reopened.FetchToolResult("rp", 1, 0).Length == 10_000, "reopened fetch");
            AssertEquivalent(() => reopened.StreamDraft("rp", "stream-R", "more"), "draft identity lost across reopen");
            Assert(reopened.GetAssignment("asg-r").ResultText == "settled!", "reopened assignment");
        }
    }

    private static void ReadSemantics()
    {
        var dir = TempDir("reads");
        using var store = Open(dir);
        Assert(store.OpenRecent("unknown", 10).Length == 0, "unknown openRecent");
        Assert(store.ReadRange("unknown", 0, 0).Length == 0, "unknown readRange");
        var preview = store.OpenRecentPreview("unknown", 10, 10);
        Assert(preview.Items.Count == 0 && preview.FromOrdinal == 0, "unknown preview");
        Assert(store.PageOlder("unknown", 0, 10).Length == 0, "unknown pageOlder");

        store.AppendMessage("rs", TextMessage("user", "a"));
        AssertThrows(() => store.ReadRange("rs", -1, 1), "negative from not rejected");
        AssertThrows(() => store.ReadRange("rs", 0, 2), "to > count not rejected");
        AssertThrows(() => store.ReadRange("rs", 1, 0), "to < from not rejected");
        Assert(store.ReadRange("rs", 0, 0).Length == 0, "empty range");
        Assert(store.OpenRecent("rs", 0).Length == 0, "zero-count window");
        // PageOlder clamps BEFORE subtracting: before > count yields the tail window, never a throw.
        var page = store.PageOlder("rs", 99, 5);
        Assert(page.Length == 1 && ((TextPart)page[0].Parts[0]).Content == "a", "pageOlder clamp tail content");
    }

    private static void FetchToolResult()
    {
        var dir = TempDir("fetch");
        var big = new string('T', 50_000);
        using var store = Open(dir);
        store.AppendMessage("ft", MessageRecord.Single("tool", new ToolResultPart("c-fetch", big)));
        store.AppendMessage("ft", TextMessage("assistant", "plain"));
        var content = store.FetchToolResult("ft", 0, 0);
        Assert(content == big, "fetch content mismatch");
        AssertThrows(() => store.FetchToolResult("ft", 1, 0), "fetch non-result part not rejected");
        AssertThrows(() => store.FetchToolResult("ft", 0, 1), "fetch missing part not rejected");
        AssertThrows(() => store.FetchToolResult("ft", 5, 0), "fetch unknown ordinal not rejected");
    }
}