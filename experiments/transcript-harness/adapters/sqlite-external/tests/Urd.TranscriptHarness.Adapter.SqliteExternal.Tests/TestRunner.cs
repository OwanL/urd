using Microsoft.Data.Sqlite;
using Urd.TranscriptHarness.Adapter.SqliteExternal;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.SqliteExternal.Tests;

// Focused Stage-0 tests for the SqliteExternal family-7 adapter: SQLite
// metadata authority + immutable per-part external payload files with the
// declared two-store ordering (publish = payload write/flush before the
// metadata commit; removal = payload delete after the commit; Open() orphan
// sweep). Payload-publication invariants are verified directly against the
// database and the payload directory. Fixtures are synthetic, OS temp only;
// shared smoke/validate exercise the IPC/host path.

public static class TestRunner
{
    /// <summary>The adapter's documented write-time preview cap (declared internal
    /// in the adapter; mirrored here so tests need no InternalsVisibleTo seam).
    ///</summary>
    private const int PreviewCapWrittenInSource = 256;

    private static int _failures;
    private static int _passed;

    public static int Main()
    {
        Run("round trip + previews + targeted fetch (all part kinds)", RoundTrip);
        Run("payload publication invariant: every part row has exactly one live payload file", PublicationInvariant);
        Run("replace suffix: obsolete payloads deleted after commit; reinserts published", ReplaceSuffixPayloads);
        Run("delete session: payload files removed after commit; assignment history stays", DeleteSessionPayloads);
        Run("startup orphan sweep removes unreferenced payloads", OrphanSweep);
        Run("bounded preview stays metadata-only within the stored cap", PreviewCap);
        Run("missing payload behind a committed pointer fails loudly", MissingPayloadFails);
        Run("streaming batches + finalize + identity rules", Streaming);
        Run("assignments: invariants, policies, duplicates", AssignmentsAndDeletes);
        Run("reopen preserves acknowledged state", ReopenPersistence);
        Run("read semantics: unknown sessions, empty windows, strict bounds", ReadSemantics);

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
        var dir = Path.Combine(Path.GetTempPath(), "urd-sqlite-external-tests", leaf);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SqliteExternalTranscriptStore Open(string dir)
    {
        var store = new SqliteExternalTranscriptStore();
        store.Open(dir, Path.Combine(dir, "attachments"));
        return store;
    }

    private static string DbPath(string dir) => Path.Combine(dir, "transcript.db");

    private static string PayloadsDirOf(string dir) => Path.Combine(dir, "payloads");

    private static void Assert(bool condition, string because)
    {
        if (!condition) throw new InvalidOperationException(because);
    }

    private static void AssertThrows(Action action, string because)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(because);
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

    /// <summary>Direct SQLite read of (payload_id) part rows, in order.</summary>
    private static List<(long Ordinal, int PartIndex, string Kind, string PayloadId, long ContentChars)> PartRows(string dir)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = DbPath(dir), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ordinal, part_index, kind, payload_id, content_chars FROM parts ORDER BY ordinal, part_index";
        var rows = new List<(long, int, string, string, long)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        }
        return rows;
    }

    private static void AssertPublicationInvariant(string dir)
    {
        // After every acknowledged mutation, parts == live payload files exactly:
        // every part row's payload file exists on disk; every file is referenced.
        var rows = PartRows(dir);
        var files = Directory.GetFiles(PayloadsDirOf(dir));
        Assert(files.Length == rows.Count,
            $"payload file count {files.Length} != part row count {rows.Count}");
        foreach (var (_, _, _, payloadId, _) in rows)
        {
            Assert(File.Exists(Path.Combine(PayloadsDirOf(dir), payloadId)), $"payload file {payloadId} missing");
        }
        var referenced = rows.Select(r => r.PayloadId).ToHashSet();
        foreach (var file in files)
        {
            Assert(referenced.Contains(Path.GetFileName(file)), $"unreferenced payload {file}");
        }
        // Immutability/addressing shape: safe generated IDs, no user content.
        foreach (var name in files)
        {
            Assert(Path.GetFileName(name).StartsWith("pl-", StringComparison.Ordinal) && Path.GetFileName(name).Length == 35,
                $"payload id not the declared generated shape: {name}");
        }
    }

    // ---- test cases ----

    private static void RoundTrip()
    {
        var dir = TempDir("roundtrip");
        var messages = new List<MessageRecord>
        {
            TextMessage("user", "ünïcode ✓ 中文 🚀"),
            MessageRecord.Single("assistant", new ToolCallPart("c1", "grep", new string('{', 50_000))),
            MessageRecord.Single("tool", new ToolResultPart("c1", new string('r', 40_000))),
            new MessageRecord("user", [new TextPart("pic"), new ImagePart("img", "att/img.png")]),
        };
        using (var store = Open(dir))
        {
            Assert(store.AppendMessage("rt", messages[0]) == 0, "append 0");
            Assert(store.AppendMessage("rt", messages[1]) == 1, "append 1");
            Assert(store.AppendMessage("rt", messages[2]) == 2, "append 2");
            Assert(store.AppendMessage("rt", messages[3]) == 3, "append 3");
        }
        using var reopened = Open(dir);
        CompareContents(reopened.ReadRange("rt", 0, 4), messages, "reopened");
        Assert(reopened.FetchToolResult("rt", 2, 0).Length == 40_000, "fetch full content");
        var preview = reopened.OpenRecentPreview("rt", 10, 24);
        Assert(preview.Items.Count == 4, $"preview items {preview.Items.Count}");
        Assert(preview.Items[1].Parts[0].Preview.Length == 24, "toolCall preview bounded");
        Assert(preview.Items[2].Parts[0].Preview == new string('r', 24), "toolResult preview content");
        var imagePreview = preview.Items[3].Parts[1];
        Assert(imagePreview.Kind == "image" && imagePreview.Preview == "att/img.png" && imagePreview.ToolCallId is null,
            "image preview maps relative path, toolCallId null");
        AssertPublicationInvariant(dir);
    }

    private static void PublicationInvariant()
    {
        var dir = TempDir("invariant");
        using (var store = Open(dir))
        {
            store.AppendMessage("inv", MessageRecord.Single("tool", new ToolResultPart("c1", new string('x', 15_000))));
            store.StreamDraft("inv", "s1", "draft");
            store.FinalizeStream("inv", "s1", TextMessage("assistant", "final"));
        }
        AssertPublicationInvariant(dir);
    }

    private static void ReplaceSuffixPayloads()
    {
        var dir = TempDir("suffix");
        using var store = Open(dir);
        store.AppendMessage("se", TextMessage("user", "m0"));
        store.AppendMessage("se", TextMessage("user", "m1"));
        var obsolete = PartRows(dir).Last().PayloadId;
        AssertThrows(() => store.ReplaceSuffix("se", 5, [TextMessage("user", "x")]), "out-of-range replace");
        Assert(store.ReplaceSuffix("se", 1, [TextMessage("assistant", "new1"), TextMessage("user", "new2")]) == 3, "replace");
        Assert(!File.Exists(Path.Combine(PayloadsDirOf(dir), obsolete)), "obsolete payload still on disk after commit");
        Assert(store.ReplaceSuffix("se", 2, []) == 2, "truncate");
        AssertPublicationInvariant(dir);
        CompareContents(store.ReadRange("se", 0, 2),
            [TextMessage("user", "m0"), TextMessage("assistant", "new1")], "after edits");
    }

    private static void DeleteSessionPayloads()
    {
        var dir = TempDir("delete");
        using var store = Open(dir);
        store.AppendMessage("ds", TextMessage("user", "content"));
        store.CreateAssignment(new AssignmentRecord("asg-ds", "ds", "t", AssignmentStatus.Open, null));
        store.SettleAssignment(new SettleAssignmentRequest("asg-ds", "ok"));
        store.DeleteSession("ds", DeletePolicy.RequireSettled);
        Assert(Directory.GetFiles(PayloadsDirOf(dir)).Length == 0, "payload files survived session deletion");
        var record = store.GetAssignment("asg-ds");
        Assert(record.Status == AssignmentStatus.Settled, "assignment history must survive session deletion");
        Assert(!store.ListSessionsMeta().Any(m => m.SessionId == "ds"), "deleted session still listed");
        AssertPublicationInvariant(dir);
    }

    private static void OrphanSweep()
    {
        var dir = TempDir("sweep");
        string livePayload;
        using (var store = Open(dir))
        {
            store.AppendMessage("sw", TextMessage("user", "live"));
            livePayload = PartRows(dir).Select(r => r.PayloadId).Single();
        }
        // Simulate the two declared crash states: published-but-uncommitted
        // and removal-committed-but-delete-interrupted (both leave an
        // unreferenced file). Inject by copying the live payload under fresh IDs.
        var orphan1 = Path.Combine(PayloadsDirOf(dir), "pl-" + Guid.NewGuid().ToString("N"));
        var orphan2 = Path.Combine(PayloadsDirOf(dir), "pl-" + Guid.NewGuid().ToString("N"));
        File.Copy(Path.Combine(PayloadsDirOf(dir), livePayload), orphan1);
        File.WriteAllText(orphan2, "torn partial write simulation");
        using (var store = Open(dir))
        {
            Assert(File.Exists(Path.Combine(PayloadsDirOf(dir), livePayload)), "sweep removed a LIVE payload");
            Assert(!File.Exists(orphan1) && !File.Exists(orphan2), "sweep left unreferenced payloads behind");
        }
    }

    private static void PreviewCap()
    {
        var dir = TempDir("preview");
        var big = new string('B', 30_000);
        using (var store = Open(dir))
        {
            store.AppendMessage("pc", MessageRecord.Single("tool", new ToolResultPart("c1", big)));
        }
        using var store2 = Open(dir);
        for (var chars = 1; chars <= PreviewCapWrittenInSource; chars *= 2)
        {
            var preview = store2.OpenRecentPreview("pc", 1, chars);
            var (kind, contentChars, toolCallId, text) = preview.Items[0].Parts[0];
            Assert(kind == "toolResult" && contentChars == 30_000 && toolCallId == "c1" && text == big[..chars],
                $"preview at cap {chars} mismatched");
        }
        // Beyond the stored cap: documented fallback reads that one payload
        // file and yields the exact longer prefix.
        var beyond = store2.OpenRecentPreview("pc", 1, PreviewCapWrittenInSource + 10);
        Assert(beyond.Items[0].Parts[0].Preview == big[..(PreviewCapWrittenInSource + 10)],
            "beyond-cap fallback mismatched");
    }

    private static void MissingPayloadFails()
    {
        var dir = TempDir("missing");
        using (var store = Open(dir))
        {
            store.AppendMessage("mp", MessageRecord.Single("tool", new ToolResultPart("c1", "content")));
            var payload = PartRows(dir).Select(r => r.PayloadId).Single();
            File.Delete(Path.Combine(PayloadsDirOf(dir), payload)); // external interference only
        }
        using var store2 = Open(dir);
        AssertThrows(() => store2.FetchToolResult("mp", 0, 0), "missing payload must fail loudly");
        AssertThrows(() => store2.OpenRecent("mp", 1), "missing payload must fail loudly in windows too");
    }

    private static void Streaming()
    {
        var dir = TempDir("streaming");
        using (var store = Open(dir))
        {
            store.StreamDraft("sd", "stream-A", "alpha ");
            store.StreamDraft("sd", "stream-A", "beta ");
            Assert(store.FinalizeStream("sd", "stream-A", TextMessage("assistant", "alpha beta")) == 0, "finalize");
            CompareContents(store.ReadRange("sd", 0, 1), [TextMessage("assistant", "alpha beta")], "finalized");
        }
        using (var store = Open(dir))
        {
            // Draft batches survive reopen; an unfinished draft stays alive.
            store.StreamDraft("sr", "stream-R", "part1 ");
            store.StreamDraft("sr", "stream-R", "part2");
        }
        using (var store = Open(dir))
        {
            store.StreamDraft("sr", "stream-R", "more"); // identity survived reopen: legal append
            Assert(store.FinalizeStream("sr", "stream-R", TextMessage("assistant", "part1 part2more")) == 0, "finalize after reopen");
            // Identity rules: one live draft per session; cross-session ids rejected.
            store.StreamDraft("sd", "s2", "x");
            AssertThrows(() => store.StreamDraft("sd", "s3", "second live draft in one session"),
                "only one live draft per session");
            AssertThrows(() => store.StreamDraft("sr2", "s2", "cross session"), "cross-session stream id");
            AssertThrows(() => store.FinalizeStream("sr2", "s2", TextMessage("assistant", "x")), "unknown draft finalize");
            Assert(store.ReadRange("sr", 0, 1) is [var finalized]
                   && ((TextPart)finalized.Parts[0]).Content == "part1 part2more",
                "reopen continuation content");
        }
        AssertPublicationInvariant(dir);
    }

    private static void AssignmentsAndDeletes()
    {
        var dir = TempDir("assign");
        using var store = Open(dir);
        store.AppendMessage("dp", TextMessage("user", "content"));
        store.CreateAssignment(new AssignmentRecord("asg-1", "dp", "t", AssignmentStatus.Open, null));
        AssertThrows(() => store.CreateAssignment(new AssignmentRecord("asg-1", "dp", "t", AssignmentStatus.Open, null)),
            "duplicate assignment id not rejected");
        AssertThrows(() => store.DeleteSession("dp", DeletePolicy.RequireSettled), "RequireSettled invariant violated");
        AssertThrows(() => store.GetAssignment("asg-missing"), "unknown assignment read");
        store.DeleteSession("dp", DeletePolicy.CancelOpen);
        Assert(store.GetAssignment("asg-1").Status == AssignmentStatus.Cancelled, "CancelOpen must cancel");
        AssertThrows(() => store.DeleteSession("never", DeletePolicy.RequireSettled), "unknown delete not rejected");
        AssertThrows(() => store.SettleAssignment(new SettleAssignmentRequest("asg-missing", "x")), "unknown settle");
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
            foreach (var message in messages) store.AppendMessage("rp", message);
            store.StreamDraft("rp", "stream-R", "unfinished ");
            store.CreateAssignment(new AssignmentRecord("asg-r", "rp", "t", AssignmentStatus.Open, null));
            store.SettleAssignment(new SettleAssignmentRequest("asg-r", "settled!"));
        }
        using var reopened = Open(dir);
        CompareContents(reopened.ReadRange("rp", 0, 2), messages, "reopened");
        var preview = reopened.OpenRecentPreview("rp", 10, 12);
        Assert(preview.Items[1].Parts[0].Preview == new string('z', 12), "reopened preview");
        Assert(reopened.FetchToolResult("rp", 1, 0).Length == 10_000, "reopened fetch");
        Assert(reopened.GetAssignment("asg-r").ResultText == "settled!", "reopened assignment");
        AssertPublicationInvariant(dir);
    }

    private static void ReadSemantics()
    {
        var dir = TempDir("reads");
        using var store = Open(dir);
        Assert(store.OpenRecent("unknown", 10).Length == 0, "unknown openRecent");
        Assert(store.ReadRange("unknown", 0, 0).Length == 0, "unknown readRange");
        Assert(store.OpenRecentPreview("unknown", 10, 10).Items.Count == 0, "unknown preview");
        Assert(store.PageOlder("unknown", 0, 10).Length == 0, "unknown pageOlder");

        store.AppendMessage("rs", TextMessage("user", "a"));
        AssertThrows(() => store.ReadRange("rs", -1, 1), "negative from not rejected");
        AssertThrows(() => store.ReadRange("rs", 0, 2), "to > count not rejected");
        AssertThrows(() => store.ReadRange("rs", 1, 0), "to < from not rejected");
        Assert(store.ReadRange("rs", 0, 0).Length == 0, "empty range");
        Assert(store.OpenRecent("rs", 0).Length == 0, "zero-count window");
        // PageOlder clamps BEFORE subtracting: before > count yields the tail.
        var page = store.PageOlder("rs", 99, 5);
        Assert(page.Length == 1 && ((TextPart)page[0].Parts[0]).Content == "a", "pageOlder clamp tail content");
        // Zero-message session after truncation stays listed.
        store.AppendMessage("rs", TextMessage("user", "b"));
        store.ReplaceSuffix("rs", 0, []);
        Assert(store.ListSessionsMeta().Any(m => m.SessionId == "rs" && m.MessageCount == 0), "zero-message session lost");
        AssertPublicationInvariant(dir);
    }
}