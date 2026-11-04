using System.Diagnostics;
using Urd.TranscriptHarness.Contracts;
using Urd.TranscriptHarness.LiteDbStore;

namespace Urd.TranscriptHarness.LiteDbStore.Tests;

// Focused adapter-level tests (not the shared runner; the harness smoke covers
// end-to-end oracle behaviour). Run from the repo with:
//   dotnet run --project experiments/transcript-harness/adapters/litedb/...Tests -c Release

internal static class Program
{
    private static int _failures;

    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--crash-child")
        {
            CrashChild(args[1]); // never returns; disposes nothing
            return 0;
        }
        var root = Path.Combine(Path.GetTempPath(), "urd-litedb-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            CrashReopen(root);        // child process exits without Dispose first
            BasicRoundTrip(root);     // reopen, unicode, all part kinds, windows
            LargeResultSpill(root);   // oversized tool result: preview bounded, fetch exact
            DraftBatches(root);       // per-call batches persisted, conflict + finalize rules
            ReplaceSuffix(root);      // suffix replacement, truncation, bounds
            BoundsAndUnknownSessions(root);
            Assignments(root);
            CollationIdents(root);      // case/delimiter/prefix session id isolation
            Statistics(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
        if (_failures > 0)
        {
            Console.Error.WriteLine($"FAILED: {_failures} check(s)");
            return 1;
        }
        Console.WriteLine("PASS: all focused checks");
        return 0;
    }

    private static LiteDbTranscriptStore Open(string root) =>
        new LiteDbTranscriptStore().Also(s => s.Open(root, Path.Combine(root, "att")));

    private static void CrashReopen(string root)
    {
        var crashRoot = Path.Combine(root, "crash");
        Directory.CreateDirectory(crashRoot);
        var exe = AppContext.BaseDirectory;
        var psi = new ProcessStartInfo(
            Environment.ProcessPath!,
            $"\"{Path.Combine(exe, "Urd.TranscriptHarness.LiteDbStore.Tests.dll")}\" --crash-child \"{crashRoot}\"");
        using var child = Process.Start(psi)!;
        child.WaitForExit();
        Check(child.ExitCode == 200, "crash child exited with kill code", $"exit={child.ExitCode}");
        using var store = Open(crashRoot);
        var metas = store.ListSessionsMeta();
        Check(metas.Length == 1 && metas[0].SessionId == "sCrash" && metas[0].MessageCount == 2, "crash: WAL replay restored commits without Dispose",
            $"metas=[{(metas.Length > 0 ? $"{metas[0].SessionId}:{metas[0].MessageCount}" : "")}]");
        var messages = store.ReadRange("sCrash", 0, 2);
        Check(messages.Length == 2 && messages[1].Parts[0] is TextPart t && t.Content.Contains("🚀"),
            "crash: content matches after replay", $"count={messages.Length}");
    }

    private static void CrashChild(string crashRoot)
    {
        var store = Open(crashRoot);
        store.AppendMessage("sCrash", new MessageRecord("user", [new TextPart("before kill ✅")]));
        store.AppendMessage("sCrash", new MessageRecord("assistant", [new TextPart("after 🚀 kill")]));
        Environment.Exit(200); // simulate process kill: no Dispose, WAL log left behind
    }

    private static void BasicRoundTrip(string root)
    {
        var messages = new List<MessageRecord>
        {
            new("user", [new TextPart("🚀 unicode क्षेत्र مرحبا 🇯🇵")]),
            new("assistant",
            [
                new ToolCallPart("tc-1", "search", """{"q":"α","n":7}"""),
                new TextPart("result follows"),
            ]),
            new("tool", [new ToolResultPart("tc-1", "42 rows")]),
            new("user", [new ImagePart("img-1", "attachments/img-1.bin")]),
        };
        using (var store = Open(root))
        {
            for (var i = 0; i < messages.Count; i++)
            {
                var ordinal = store.AppendMessage("sBasic", messages[i]);
                Check(ordinal == i, "append returns dense ordinal", $"got {ordinal}, want {i}");
            }
            var immediate = store.ReadRange("sBasic", 0, messages.Count);
            Check(DeepEqual(immediate, messages), "in-memory round trip");
        }
        using (var reopened = Open(root))
        {
            var metas = reopened.ListSessionsMeta();
            Check(metas.Length == 1 && metas[0].SessionId == "sBasic" && metas[0].MessageCount == 4,
                "reopen session counts", $"got {metas.Length} sessions");
            Check(DeepEqual(reopened.ReadRange("sBasic", 0, 4), messages), "reopen full-content equality");
            Check(DeepEqual(reopened.OpenRecent("sBasic", 2), [.. messages[2..]]), "OpenRecent tail");
            Check(DeepEqual(reopened.PageOlder("sBasic", 1, 5), [messages[0]]), "PageOlder clamps");
            var preview = reopened.OpenRecentPreview("sBasic", 10, 5);
            var expected = "🚀 unicode क्षेत्र مرحبا 🇯🇵"[..5];
            Check(preview.FromOrdinal == 0 && preview.Items.Count == 4
                && preview.Items[0].Parts[0].Preview == expected, "preview capped at request chars",
                $"[{preview.Items[0].Parts[0].Preview}] want [{expected}]");
            Check(preview.Items[1].Parts[0].Kind == "toolCall" && preview.Items[1].Parts[0].ToolCallId == "tc-1",
                "preview part metadata", $"{preview.Items[1].Parts[0].Kind}");
            Check(reopened.FetchToolResult("sBasic", 2, 0) == "42 rows", "FetchToolResult inline");
        }
    }

    private static void LargeResultSpill(string root)
    {
        var large = new string('λ', 50_037) + new string('Ω', 500);
        var withLarge = new MessageRecord("tool", [new ToolResultPart("tc-big", large)]);
        using (var store = Open(root))
        {
            store.AppendMessage("sLarge", withLarge);
        }
        using (var reopened = Open(root))
        {
            var messages = reopened.ReadRange("sLarge", 0, 1);
            Check(messages[0].Parts[0] is ToolResultPart r && r.Content.Length == large.Length,
                "large result round trip through blk spill", $"{messages[0].Parts[0].GetType().Name}");
            var same = string.Equals(((ToolResultPart)messages[0].Parts[0]).Content, large, StringComparison.Ordinal);
            Check(same, "large result content exact", "");
            var fetched = reopened.FetchToolResult("sLarge", 0, 0);
            Check(fetched.Length == large.Length && string.Equals(fetched, large, StringComparison.Ordinal),
                "FetchToolResult returns spilled content exactly", $"{fetched.Length}");
            var preview = reopened.OpenRecentPreview("sLarge", 5, 24);
            var part = preview.Items[0].Parts[0];
            Check(part.ContentChars == large.Length && part.Preview == new string('λ', 24),
                "large-result preview bounded and exact prefix", $"len={part.Preview.Length}");
        }
    }

    private static void DraftBatches(string root)
    {
        using (var store = Open(root))
        {
            store.StreamDraft("sDraft", "w1", "a");
            store.StreamDraft("sDraft", "w1", "b");
            store.StreamDraft("sDraft", "w1", "c");
            CheckThrows(() => store.StreamDraft("sDraft", "w2", "x"), "draft id conflict throws");
            var ordinal = store.FinalizeStream("sDraft", "w1", new MessageRecord("assistant", [new TextPart("final")]));
            Check(ordinal == 0, "finalize returns tail ordinal", $"{ordinal}");
            CheckThrows(() => store.FinalizeStream("sDraft", "w1", new MessageRecord("assistant", [new TextPart("again")])),
                "finalize of unknown stream throws");
            Check(store.ListSessionsMeta().Single(s => s.SessionId == "sDraft").MessageCount == 1,
                "finalize appended exactly one message");
        }
        using (var reopened = Open(root))
        {
            Check(reopened.ReadRange("sDraft", 0, 1)[0].Parts[0] is TextPart f && f.Content == "final",
                "draft state does not leak into messages after reopen");
        }
    }

    private static void ReplaceSuffix(string root)
    {
        var replacement = new List<MessageRecord>
        {
            new("assistant", [new ToolCallPart("tc-r", "fix", "{}")]),
            new("tool", [new ToolResultPart("tc-r", new string('R', 17_000))]), // spilled within replacement
            new("user", [new TextPart("thanks ✨")]),
        };
        using (var store = Open(root))
        {
            store.AppendMessage("sReplace", new MessageRecord("user", [new TextPart("q")]));
            store.AppendMessage("sReplace", new MessageRecord("assistant", [new TextPart("wrong")]));
            Check(store.ReplaceSuffix("sReplace", 1, replacement) == 4, "replace returns new count");
            CheckThrows(() => store.ReplaceSuffix("sReplace", 99, replacement), "replace beyond count throws");
        }
        using (var reopened = Open(root))
        {
            var all = reopened.ReadRange("sReplace", 0, 4);
            Check(all.Length == 4 && all[0].Parts[0] is TextPart && all[1].Parts[0] is ToolCallPart,
                "prefix retained, suffix replaced");
            Check(reopened.FetchToolResult("sReplace", 2, 0).Length == 17_000,
                "replacement spilled content retrievable");
            Check(reopened.ReplaceSuffix("sReplace", 0, []) == 0,
                "empty replacement truncates session");
            Check(reopened.ListSessionsMeta().Single(s => s.SessionId == "sReplace").MessageCount == 0,
                "truncted count metadata");
        }
    }

    private static void BoundsAndUnknownSessions(string root)
    {
        using var store = Open(root);
        Check(store.OpenRecent("unknown", 3).Length == 0, "OpenRecent unknown session empty");
        Check(store.PageOlder("unknown", 3, 2).Length == 0, "PageOlder unknown session empty");
        Check(store.ReadRange("unknown", 0, 1).Length == 0, "ReadRange unknown session empty");
        CheckThrows(() => store.ReadRange("sBasic", 0, 99), "ReadRange above count throws");
        CheckThrows(() => store.ReadRange("sBasic", 2, 1), "ReadRange inverted bounds throws");
        CheckThrows(() => store.ReadRange("sBasic", -1, 1), "ReadRange negative start throws");
        CheckThrows(() => store.FetchToolResult("sBasic", 0, 0), "unknown message throws");
        Check(store.OpenRecentPreview("unknown", 3, 24).Items.Count == 0, "preview of unknown session empty");
        CheckThrows(() => store.DeleteSession("unknown", DeletePolicy.RequireSettled), "delete unknown session throws");
    }

    private static void Assignments(string root)
    {
        using (var store = Open(root))
        {
            store.AppendMessage("sAsg", new MessageRecord("user", [new TextPart("task")]));
            store.CreateAssignment(new AssignmentRecord("asg-1", "sAsg", "do it", AssignmentStatus.Open, null));
            CheckThrows(() => store.CreateAssignment(new AssignmentRecord("asg-1", "sAsg", "again", AssignmentStatus.Open, null)),
                "duplicate assignment id throws");
            CheckThrows(() => store.DeleteSession("sAsg", DeletePolicy.RequireSettled),
                "RequireSettled invariant blocks delete with open assignment");
            store.DeleteSession("sAsg", DeletePolicy.CancelOpen);
            var cancelled = store.GetAssignment("asg-1");
            Check(cancelled.Status == AssignmentStatus.Cancelled,
                "CancelOpen cancels assignments instead of removing", $"{cancelled.Status}");
            Check(store.OpenRecent("sAsg", 5).Length == 0 && store.ListSessionsMeta().All(s => s.SessionId != "sAsg"),
                "deleted session data gone");
            CheckThrows(() => store.GetAssignment("asg-none"), "unknown assignment throws");
            CheckThrows(() => store.SettleAssignment(new SettleAssignmentRequest("asg-none", "done")),
                "settle unknown throws");
            store.AppendMessage("sAsg", new MessageRecord("user", [new TextPart("again after delete")]));
            store.CreateAssignment(new AssignmentRecord("asg-2", "sAsg", "again", AssignmentStatus.Open, null));
            store.SettleAssignment(new SettleAssignmentRequest("asg-2", "done ✓"));
            Check(store.GetAssignment("asg-2").Status == AssignmentStatus.Settled && store.GetAssignment("asg-2").ResultText == "done ✓",
                "settle persists");
            store.DeleteSession("sAsg", DeletePolicy.RequireSettled);
            Check(store.ListSessionsMeta().All(s => s.SessionId != "sAsg"), "settled session deletable");
        }
        using (var reopened = Open(root))
        {
            var settled = reopened.GetAssignment("asg-2");
            Check(settled.Status == AssignmentStatus.Settled, "assignment persists across reopen");
        }
    }

    private static void Statistics(string root)
    {
        using var store = Open(root);
        Check(store.Sample().ManagedAllocatedBytes > 0, "statistics sample reports managed allocation");
        Check(store.Describe().StoreName is { Length: > 0 }, "descriptor reports store name");
    }

    private static void CollationIdents(string root)
    {
        // Regression: LiteDB v5's default culture collation (en-NZ/IgnoreCase)
        // made distinct session ids collide in the unique _id index (case
        // differences, embedded separator characters) and could leak keys
        // across prefix-ambiguous ids in ranges. The adapter now enforces
        // byte-ordinal collation at Open, so all of these behave like the
        // reference.
        var ids = new[] { "CaseA", "casea", "CASEA", "ab", "a\u001Fb", "session-1", "session-10", "session-11" };
        var storeRoot = Path.Combine(root, "idents");
        using (var store = Open(storeRoot))
        {
            foreach (var id in ids)
            {
                store.AppendMessage(id, new MessageRecord("user", [new TextPart($"content-of-{id}")]));
                store.AppendMessage(id, new MessageRecord("assistant", [new TextPart($"reply-{id}")]));
                var metas = store.ListSessionsMeta().ToDictionary(m => m.SessionId, m => m.MessageCount, StringComparer.Ordinal);
                Check(metas.TryGetValue(id, out var count) && count == 2, $"collation distinct id survives write {ToLabel(id)}", count.ToString());
            }
            Check(store.ListSessionsMeta().Length == ids.Length, $"collation all {ids.Length} sessions indexed");
        }

        // Reopen must keep the persisted ordinal collation and full isolation.
        using (var store = Open(storeRoot))
        {
            foreach (var id in ids)
            {
                var range = store.ReadRange(id, 0, 2);
                Check(range.Length == 2
                    && range[0].Role == "user"
                    && ((TextPart)range[0].Parts[0]).Content == $"content-of-{id}"
                    && ((TextPart)range[1].Parts[0]).Content == $"reply-{id}",
                    $"collation isolated range after reopen {ToLabel(id)}", range.Length.ToString());
                Check(store.OpenRecent(id, 1).Length == 1, $"collation open recent {ToLabel(id)}");
            }
            // Cross-session spot checks: no range of one id returns another's content.
            var rangeA = store.ReadRange("CaseA", 0, 2);
            Check(rangeA.All(r => ((TextPart)r.Parts[0]).Content.Contains("of-CaseA") || ((TextPart)r.Parts[0]).Content.Contains("reply-CaseA")),
                "collation case-differing siblings isolated");
            var rangeDelim = store.ReadRange("ab", 0, 2);
            Check(!rangeDelim.Any(r => ((TextPart)r.Parts[0]).Content.Contains("a\u001Fb")),
                "collation delimiter-bearing sibling not leaked into plain id");
            store.DeleteSession("CaseA", DeletePolicy.RequireSettled);
            var metasAfterDelete = store.ListSessionsMeta().ToDictionary(m => m.SessionId, StringComparer.Ordinal);
            Check(!metasAfterDelete.ContainsKey("CaseA") && metasAfterDelete.ContainsKey("casea"),
                "collation deleted sibling stays deleted");
        }
    }

    private static string ToLabel(string id) => id.Replace("\u001F", "<sep>");

    // ----- helpers -----

    private static bool DeepEqual(MessageRecord[] actual, IReadOnlyList<MessageRecord> expected)
    {
        if (actual.Length != expected.Count) return false;
        for (var i = 0; i < actual.Length; i++)
        {
            if (actual[i].Role != expected[i].Role
                || actual[i].Parts.Count != expected[i].Parts.Count
                || !actual[i].Parts.Select(PartText).SequenceEqual(expected[i].Parts.Select(PartText), StringComparer.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static string PartText(MessagePart part) => part switch
    {
        TextPart t => $"t:{t.Content}",
        ToolCallPart c => $"c:{c.ToolCallId}|{c.Name}|{c.ArgsJson}",
        ToolResultPart r => $"r:{r.ToolCallId}|{r.Content}",
        ImagePart i => $"i:{i.ImageId}|{i.RelativePath}",
        _ => throw new InvalidOperationException("unexpected part type"),
    };

    private static void CheckThrows(Action action, string name)
    {
        try
        {
            action();
            Check(false, "expected throw", name);
        }
        catch (Exception)
        {
            Check(true, name);
        }
    }

    private static void Check(bool condition, string name, string detail = "")
    {
        if (condition)
        {
            Console.WriteLine($"PASS {name}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"FAIL-detail {name} {(detail.Length > 0 ? $"[{detail}]" : "")}");
        }
    }
}

file static class StoreExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}