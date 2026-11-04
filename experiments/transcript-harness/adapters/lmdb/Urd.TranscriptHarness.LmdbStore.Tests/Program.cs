using System.Text;
using Urd.TranscriptHarness.Contracts;
using Urd.TranscriptHarness.LmdbStore;

// Focused correctness suite for the family-4 LMDB adapter (Stage 0), run
// in-process against the real native lmdb.dll bundled with LightningDB.
// Cases: append/read/preview/huge result/Unicode ordering/assignments/delete/
// reopen/stream-draft persistence. Crash and kill mechanics, shared traces,
// IPC and durable-tier verdicts belong to the shared runner and are
// intentionally NOT re-implemented here.

internal static class Program
{
    private static int _failures;

    public static int Main()
    {
        Case("append/read round trip incl. tool call+result and image reference", BasicRoundTrip);
        Case("read boundaries: unknown sessions are empty, clamps and range validation", ReadBoundaries);
        Case("previews stay metadata-bounded on huge tool results", PreviewBoundedOnHugeResult);
        Case("preview longer than stored budget falls back to targeted content fetch", PreviewFallback);
        Case("replaceSuffix: renumbering, truncation, whole session, invalid bounds", ReplaceSuffix);
        Case("Unicode session keys order like StringComparer.Ordinal and do not overlap", UnicodeOrdering);
        Case("assignments: create/get/settle, duplicates, unknown ids", Assignments);
        Case("deleteSession enforces the assignment invariant and removes payloads", DeleteSessionInvariants);
        Case("stream drafts persist per call and finalize transactionally, incl. across reopen", StreamDrafts);
        Case("reopen preserves acknowledged content (fresh in-process instance)", ReopenPersistence);
        Case("mixed scales in one store: many small sessions plus one large history", MixedScales);

        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"FAILED: {_failures} case(s)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Case(string name, Action body)
    {
        try
        {
            body();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine($"FAIL {name}: {ex}");
        }
    }

    // ---- helpers ----

    private static string TempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), "urd-lmdb-adapter-tests",
            $"{label}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static (LmdbTranscriptStore Store, string Root) OpenStore(string label)
    {
        var root = TempDir(label);
        var store = new LmdbTranscriptStore();
        store.Open(root, Path.Combine(root, "attachments"));
        return (store, root);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"assert failed: {message}");
    }

    private static void AssertOrderingEquals(string[] actual, string[] expected)
    {
        Assert(actual.Length == expected.Length, $"length {actual.Length} != {expected.Length}");
        for (var i = 0; i < actual.Length; i++)
        {
            Assert(actual[i] == expected[i], $"position {i}: {actual[i]} != {expected[i]}");
        }
    }

    private static void AssertThrows(Action body, string contains)
    {
        try
        {
            body();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains(contains, StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidOperationException($"expected InvalidOperationException containing '{contains}'");
    }

    private static MessageRecord Text(string role, string content) => MessageRecord.Single(role, new TextPart(content));

    private static MessageRecord ToolCallMessage(int id) =>
        new("assistant", [new ToolCallPart($"call-{id}", "read_file", $"{{\"path\":\"file-{id}.txt\"}}")]);

    private static MessageRecord ToolResultMessage(int id, string content) =>
        new("tool", [new ToolResultPart($"call-{id}", content)]);

    private static string RepeatText(string unit, int repeats) => string.Concat(Enumerable.Repeat(unit, repeats));

    private static void AssertEqual(MessageRecord[] actual, params MessageRecord[] expected)
    {
        Assert(actual.Length == expected.Length, $"count {actual.Length} != {expected.Length}");
        for (var i = 0; i < actual.Length; i++)
        {
            Assert(actual[i].Role == expected[i].Role, $"role @{i}");
            Assert(actual[i].Parts.Count == expected[i].Parts.Count, $"part count @{i}");
            for (var p = 0; p < actual[i].Parts.Count; p++)
            {
                Assert(actual[i].Parts[p].Equals(expected[i].Parts[p]), $"part {p} content @{i}");
            }
        }
    }

    // ---- cases ----

    private static void BasicRoundTrip()
    {
        var (store, root) = OpenStore("basic");
        try
        {
            var id0 = store.AppendMessage("s1", Text("user", "Hello world"));
            var id1 = store.AppendMessage("s1", ToolCallMessage(0));
            var id2 = store.AppendMessage("s1", ToolResultMessage(0, "the tool result body"));
            var withImage = new MessageRecord("assistant",
                [new ImagePart("img-1", "0/img-1.bin"), new TextPart("and some text")]);
            var id3 = store.AppendMessage("s1", withImage);
            Assert((id0, id1, id2, id3) == (0L, 1L, 2L, 3L), "dense ordinals");
            AssertEqual(store.OpenRecent("s1", 50),
                Text("user", "Hello world"),
                ToolCallMessage(0),
                ToolResultMessage(0, "the tool result body"),
                withImage);
            AssertEqual(store.ReadRange("s1", 1, 3), ToolCallMessage(0), ToolResultMessage(0, "the tool result body"));
            AssertEqual(store.PageOlder("s1", 2, 2), Text("user", "Hello world"), ToolCallMessage(0));
            AssertEqual(store.OpenRecent("s1", 2), ToolResultMessage(0, "the tool result body"), withImage);
            Assert(store.FetchToolResult("s1", 2, 0) == "the tool result body", "fetch tool result");
            var meta = store.ListSessionsMeta().Single();
            Assert(meta.SessionId == "s1" && meta.MessageCount == 4, "single session, count 4");
            Assert(store.Describe().AckTier == AckTier.FsyncOrJournal, "ack tier");
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ReadBoundaries()
    {
        var (store, root) = OpenStore("bounds");
        try
        {
            Assert(store.OpenRecent("missing", 5).Length == 0, "unknown session: empty window");
            Assert(store.PageOlder("missing", 3, 5).Length == 0, "unknown session page older");
            Assert(store.ReadRange("missing", 0, 2).Length == 0, "unknown session read range");
            Assert(store.OpenRecentPreview("missing", 3, 8).Items.Count == 0, "unknown session preview");
            for (var i = 0; i < 5; i++) store.AppendMessage("s", Text("user", $"m{i}"));

            Assert(store.OpenRecent("s", 0).Length == 0, "count 0 -> empty");
            Assert(store.OpenRecent("s", 99).Length == 5, "count beyond total returns all");
            AssertEqual(store.PageOlder("s", 99, 2), Text("user", "m3"), Text("user", "m4"));
            AssertEqual(store.PageOlder("s", 0, 3), []); // before=0 -> empty
            AssertEqual(store.PageOlder("s", 5, 3), Text("user", "m2"), Text("user", "m3"), Text("user", "m4"));
            AssertEqual(store.ReadRange("s", 0, 5),
                Text("user", "m0"), Text("user", "m1"), Text("user", "m2"), Text("user", "m3"), Text("user", "m4"));
            AssertThrows(() => store.ReadRange("s", 0, 6), "outside count");
            AssertThrows(() => store.ReadRange("s", -1, 2), "outside count");
            AssertThrows(() => store.ReadRange("s", 3, 2), "outside count");
            AssertThrows(() => store.FetchToolResult("s", 9, 0), "count=5");
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PreviewBoundedOnHugeResult()
    {
        var (store, root) = OpenStore("preview");
        try
        {
            var huge = RepeatText("abcdefgh\u00e9\u20ac\ud83d\ude00", 1_400_000); // well over 8 MiB, astral chars
            store.AppendMessage("s", Text("user", "ask"));
            store.AppendMessage("s", ToolCallMessage(0));
            store.AppendMessage("s", ToolResultMessage(0, huge));
            store.AppendMessage("s", Text("assistant", "done"));

            var before = GC.GetTotalAllocatedBytes(precise: false);
            var preview = store.OpenRecentPreview("s", 3, 24);
            var delta = GC.GetTotalAllocatedBytes(precise: false) - before;

            Assert(preview.SessionId == "s", "preview session id");
            Assert(preview.FromOrdinal == 1, "preview from ordinal");
            Assert(preview.Items.Count == 3, "preview item count");
            var toolCallItem = preview.Items[0];
            var resultItem = preview.Items[1];
            Assert(toolCallItem.Parts[0].Kind == "toolCall", "tool call kind");
            Assert(toolCallItem.Parts[0].ToolCallId == "call-0", "tool call id");
            Assert(toolCallItem.Parts[0].ContentChars == "{\"path\":\"file-0.txt\"}".Length,
                "tool call args length");
            Assert(resultItem.Parts[0].Kind == "toolResult", "tool result kind");
            Assert(resultItem.Parts[0].ContentChars == huge.Length, "full content char length reported");
            Assert(resultItem.Parts[0].Preview.Length == 24, "preview capped at previewChars");
            Assert(resultItem.Parts[0].Preview == huge[..24], "preview is exact leading prefix");
            Assert(preview.Items[2].Parts[0].Preview == "done", "small text preview");

            // Evidence that the header-only preview path avoids decoding the
            // huge payload: the whole window's managed allocation stays far
            // below the ~16 MiB UTF-8 materialization of the result.
            Assert(delta < 2_000_000, $"preview allocation delta {delta} too high (payload likely decoded)");
            Assert(store.FetchToolResult("s", 2, 0) == huge, "full fetch returns identical content");

            // Full reads still return payloads (range reads may carry them).
            AssertEqual(store.ReadRange("s", 2, 3), ToolResultMessage(0, huge));
            AssertEqual(store.OpenRecent("s", 1), Text("assistant", "done"));
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PreviewFallback()
    {
        var (store, root) = OpenStore("preview-fallback");
        try
        {
            var content = RepeatText("x", 500) + "TAIL";
            store.AppendMessage("s", ToolResultMessage(0, content));
            Assert(store.OpenRecentPreview("s", 1, 400).Items[0].Parts[0].Preview == content[..400],
                "exact prefix via targeted content fetch (> 256-char budget)");
            Assert(store.OpenRecentPreview("s", 1, content.Length).Items[0].Parts[0].Preview == content,
                "preview >= length returns full content");
            Assert(store.OpenRecentPreview("s", 1, 5).Items[0].Parts[0].Preview == content[..5], "small preview");
            Assert(store.OpenRecentPreview("s", 1, 0).Items[0].Parts[0].Preview == "", "zero preview chars");
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ReplaceSuffix()
    {
        var (store, root) = OpenStore("replace");
        try
        {
            store.AppendMessage("s", Text("user", "prompt"));
            store.AppendMessage("s", Text("assistant", "answer"));
            store.AppendMessage("s", Text("user", "followup"));
            store.AppendMessage("s", Text("assistant", "continuation"));

            // Replace from 1 with a longer replacement; result renumbered densely.
            var newCount = store.ReplaceSuffix("s", 1, [Text("assistant", "replacement"), Text("user", "more"), Text("user", "extra")]);
            Assert(newCount == 4, "longer replacement count");
            AssertEqual(store.ReadRange("s", 0, 4),
                Text("user", "prompt"), Text("assistant", "replacement"), Text("user", "more"), Text("user", "extra"));

            // Shorter replacement truncates.
            newCount = store.ReplaceSuffix("s", 2, [Text("user", "short")]);
            Assert(newCount == 3, "shorter replacement count");
            AssertEqual(store.ReadRange("s", 0, 3),
                Text("user", "prompt"), Text("assistant", "replacement"), Text("user", "short"));

            // from == count appends without replacement.
            newCount = store.ReplaceSuffix("s", 3, [Text("user", "appended")]);
            Assert(newCount == 4, "append-via-replace count");
            AssertEqual(store.ReadRange("s", 3, 4), Text("user", "appended"));

            // Whole session replacement to empty truncates the session.
            Assert(store.ReplaceSuffix("s", 0, []) == 0, "empty replacement count");
            AssertEqual(store.ReadRange("s", 0, 0), []); // session now empty

            // Invalid bounds error.
            AssertThrows(() => store.ReplaceSuffix("s", 1, []), "outside count=0");
            AssertThrows(() => store.ReplaceSuffix("missing", 0, []), "Unknown session");
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void UnicodeOrdering()
    {
        var ids = new[]
        {
            "session-b", "session-a", "aa", "a", "ab", "a\u007f", "a\u0080", "z\u00e9", "\u20ac",
            "\ud83d\ude00", "\ud83d\ude00x", "\u07ff", "\ufff0", "s\ud83c\udf38", "\u0000embedded\u0000",
        };
        var (store, root) = OpenStore("unicode");
        try
        {
            foreach (var id in ids)
            {
                store.AppendMessage(id, Text("user", $"content-of-{id}"));
            }
            var expected = ids.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var actual = store.ListSessionsMeta().Select(m => m.SessionId).ToArray();
            AssertOrderingEquals(actual, expected);

            // Block isolation: editing sibling sessions must stay inside their block.
            store.ReplaceSuffix("a", 0, [Text("user", "rewritten-a")]);
            Assert(store.OpenRecent("aa", 5)[0].Parts.OfType<TextPart>().Single().Content == "content-of-aa",
                "'aa' untouched by 'a' edit");
            Assert(store.OpenRecent("ab", 5)[0].Parts.OfType<TextPart>().Single().Content == "content-of-ab",
                "'ab' untouched by 'a' edit");
            Assert(store.OpenRecent("a", 5)[0].Parts.OfType<TextPart>().Single().Content == "rewritten-a", "'a' edited");
            store.DeleteSession("\ud83d\ude00x", DeletePolicy.RequireSettled);
            Assert(null == store.ListSessionsMeta().FirstOrDefault(m => m.SessionId == "\ud83d\ude00x"),
                "astral-suffix session deleted");
            Assert(store.OpenRecent("\ud83d\ude00", 5)[0].Parts.OfType<TextPart>().Single().Content == "content-of-\ud83d\ude00",
                "'\U0001F600' untouched by '\U0001F600x' deletion");
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Assignments()
    {
        var (store, root) = OpenStore("assign");
        try
        {
            store.AppendMessage("s", Text("user", "hi"));
            var record = new AssignmentRecord("a-1", "s", "do the thing", AssignmentStatus.Open, null);
            store.CreateAssignment(record);
            Assert(store.GetAssignment("a-1") == record, "get returns record");
            AssertThrows(() => store.CreateAssignment(record with { Title = "again" }), "Duplicate assignment id");

            store.SettleAssignment(new SettleAssignmentRequest("a-1", "the result"));
            var settled = store.GetAssignment("a-1");
            Assert(settled.Status == AssignmentStatus.Settled && settled.ResultText == "the result", "settled state");

            AssertThrows(() => store.SettleAssignment(new SettleAssignmentRequest("missing", "x")), "Unknown assignment");
            AssertThrows(() => store.GetAssignment("missing"), "Unknown assignment");
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void DeleteSessionInvariants()
    {
        var (store, root) = OpenStore("delete");
        try
        {
            store.AppendMessage("s", Text("user", "keep"));
            store.AppendMessage("s", Text("user", "drop"));
            store.CreateAssignment(new AssignmentRecord("a-keep", "s", "settled work", AssignmentStatus.Settled, "done"));
            store.CreateAssignment(new AssignmentRecord("a-open", "s", "open work", AssignmentStatus.Open, null));

            AssertThrows(() => store.DeleteSession("s", DeletePolicy.RequireSettled), "RequireSettled invariant");
            Assert(store.GetAssignment("a-open").Status == AssignmentStatus.Open, "require-settled leaves assignment open");

            store.DeleteSession("s", DeletePolicy.CancelOpen);
            Assert(store.ListSessionsMeta().Length == 0, "session gone");
            var cancelled = store.GetAssignment("a-open");
            Assert(cancelled.Status == AssignmentStatus.Cancelled && cancelled.TargetSessionId == "s", "open assignment cancelled");
            var kept = store.GetAssignment("a-keep");
            Assert(kept.Status == AssignmentStatus.Settled, "settled assignment record kept");
            AssertThrows(() => store.DeleteSession("s", DeletePolicy.CancelOpen), "unknown session");
            // Final-closure delete removes payloads and metadata; a later append recreates the session.
            var recreated = store.AppendMessage("s", Text("user", "recreate"));
            Assert(recreated == 0, "recreated session starts dense at 0");
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void StreamDrafts()
    {
        var (store, root) = OpenStore("stream");
        try
        {
            store.AppendMessage("s", Text("user", "ask"));
            store.StreamDraft("s", "stream-1", "alpha ");
            store.StreamDraft("s", "stream-1", "beta ");
            // One active draft per session.
            AssertThrows(() => store.StreamDraft("s", "stream-2", "other"), "already has active draft");
            store.StreamDraft("s", "stream-1", "gamma");
            var ordinal = store.FinalizeStream("s", "stream-1", Text("assistant", "the completed stream"));
            Assert(ordinal == 1, "finalize ordinal (drafted content must not reserve an ordinal)");
            AssertEqual(store.OpenRecent("s", 2), Text("user", "ask"), Text("assistant", "the completed stream"));
            AssertThrows(() => store.FinalizeStream("s", "stream-1", Text("assistant", "again")), "No active draft");

            // A stream registered with empty batches is still an active draft.
            store.StreamDraft("other-session", "empty-stream", "");
            AssertThrows(() => store.StreamDraft("other-session", "another-id", "x"), "already has active draft");

            // Non-finalized drafts persist across reopen (each StreamDraft call
            // is one persisted batch; nothing partial is ever served).
            store.StreamDraft("persist", "p-stream", "first\n");
            store.Dispose();
            var reopened = new LmdbTranscriptStore();
            reopened.Open(root, Path.Combine(root, "attachments"));
            AssertThrows(() => reopened.StreamDraft("persist", "p-other", "x"), "already has active draft");
            var finalizeOrdinal = reopened.FinalizeStream("persist", "p-stream", Text("assistant", "finalized-after-reopen"));
            Assert(finalizeOrdinal == 0, "draft finalize after reopen assigns ordinal 0");
            AssertEqual(reopened.OpenRecent("persist", 1), Text("assistant", "finalized-after-reopen"));
            reopened.Dispose();
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ReopenPersistence()
    {
        var (store, root) = OpenStore("reopen");
        var huge = RepeatText("re", 400_000) + "\u00e9\u20ac\ud83d\ude00";
        try
        {
            store.AppendMessage("s1", Text("user", "first"));
            store.AppendMessage("s1", ToolResultMessage(1, huge));
            store.AppendMessage("s2", Text("user", "other session"));
            store.ReplaceSuffix("s1", 0, [Text("user", "rewritten first"), Text("assistant", "extra")]);
            store.CreateAssignment(new AssignmentRecord("a-1", "s1", "work", AssignmentStatus.Settled, "ok"));
            AssertThrows(() => store.DeleteSession("gone", DeletePolicy.RequireSettled), "unknown session");
        }
        finally
        {
            store.Dispose();
        }
        var reopened = new LmdbTranscriptStore();
        reopened.Open(root, Path.Combine(root, "attachments"));
        try
        {
            var metas = reopened.ListSessionsMeta();
            Assert(metas.Length == 2, "session set after reopen");
            Assert(metas[0].SessionId == "s1" && metas[0].MessageCount == 2, "s1 meta");
            Assert(metas[1].SessionId == "s2" && metas[1].MessageCount == 1, "s2 meta");
            AssertEqual(reopened.ReadRange("s1", 0, 2), Text("user", "rewritten first"), Text("assistant", "extra"));
            Assert(reopened.OpenRecent("s2", 1)[0].Parts.OfType<TextPart>().Single().Content == "other session",
                "other session intact after reopen");
            Assert(reopened.GetAssignment("a-1").Status == AssignmentStatus.Settled, "assignment survives reopen");
            Assert(reopened.GetAssignment("a-1").Status == AssignmentStatus.Settled, "assignment survives reopen");
        }
        finally
        {
            reopened.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void MixedScales()
    {
        var (store, root) = OpenStore("mixed");
        try
        {
            var largeResult = RepeatText("y", 64_000);
            store.AppendMessage("big", ToolResultMessage(0, largeResult));
            for (var i = 1; i < 800; i++)
            {
                store.AppendMessage("big", i % 3 == 0 ? ToolResultMessage(i, largeResult) : Text("user", $"m{i}"));
            }
            foreach (var id in new[] { "small-1", "small-2", "small-3", "\u00e9-small", "small-0" })
            {
                store.AppendMessage(id, ToolResultMessage(1, "small"));
                store.AppendMessage(id, Text("user", "tail"));
            }

            var metas = store.ListSessionsMeta().ToDictionary(m => m.SessionId, m => m.MessageCount, StringComparer.Ordinal);
            Assert(metas.Count == 6 && metas["big"] == 800 && metas["small-1"] == 2, "mixed counts");
            // big session: ordinal 797 = text (797 %% 3 != 0), 798 = tool result, 799 = text
            AssertEqual(store.OpenRecent("big", 3),
                Text("user", "m797"), ToolResultMessage(798, largeResult), Text("user", "m799"));
            AssertEqual(store.PageOlder("big", 10, 2), Text("user", "m8"), ToolResultMessage(9, largeResult));
            var preview = store.OpenRecentPreview("big", 5, 12);
            Assert(preview.FromOrdinal == 795 && preview.Items.Count == 5, "big-session preview window");
            Assert(store.FetchToolResult("big", 798, 0) == largeResult, "targeted fetch at scale");

            var dataFile = new FileInfo(Path.Combine(root, "data.mdb"));
            Console.WriteLine($"  info: data.mdb after ≈{dataFile.Length / 1024.0:F0} KiB (1 GiB map reservation is address-space only)");
            Assert(dataFile.Length < 100L * 1024 * 1024, "data file grows with data, not map size");
        }
        finally
        {
            store.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}