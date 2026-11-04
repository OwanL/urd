using Microsoft.Data.Sqlite;
using Urd.TranscriptHarness.Adapter.SqlitePerSession;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.SqlitePerSession.Tests;

// Focused Stage-0 tests for the SqlitePerSession family-8 adapter: per-session
// storage semantics, coordination-db assignment authority, and the recoverable
// deletion protocol (intent marker + file/companion removal + recovery at
// Open()). In-process, contract level; shared smoke/validate exercise the
// IPC/host path. Fixtures are synthetic, OS temp only.

public static class TestRunner
{
    private static int _failures;
    private static int _passed;

    public static int Main()
    {
        Run("round trip + non-ASCII content across separate files", RoundTripSeparateFiles);
        Run("bounded preview + fetch tool result (delegated, per session)", PreviewAndFetch);
        Run("streaming batches + finalize + reopen mid-stream", StreamingBatches);
        Run("stream identity survives reopen mid-stream", StreamingReopenMidStream);
        Run("suffix edit inside the session file (renumber, truncate)", SuffixEdits);
        Run("assignments live in the coordinator and outlive their target", AssignmentAuthority);
        Run("delete policies resolve through the coordinator", DeletePolicies);
        Run("no WAL/SHM companions left after deletion", CompanionCleanup);
        Run("interrupted deletion resumes at Open() (marker + file)", DeletionRecoveryFilePresent);
        Run("interrupted deletion resumes at Open() (marker only)", DeletionRecoveryFileGone);
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
        var dir = Path.Combine(Path.GetTempPath(), "urd-sqlite-per-session-tests", leaf);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return dir;
    }

    private static SqlitePerSessionTranscriptStore Open(string dir)
    {
        var store = new SqlitePerSessionTranscriptStore();
        store.Open(dir, Path.Combine(dir, "attachments"));
        return store;
    }

    private static string CoordinatorPath(string dir) => Path.Combine(dir, "coordination.db");

    private static string SessionsRoot(string dir) => Path.Combine(dir, "sessions");

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

    private static MessageRecord TextMessage(string role, string content) => MessageRecord.Single(role, new TextPart(content));

    private static bool SessionDirPresent(string dir, string sessionId)
    {
        // Directory names are "s-<escaped>.<hash>"; locate by in-file identity.
        foreach (var entry in Directory.EnumerateDirectories(SessionsRoot(dir), "s-*"))
        {
            var dbPath = Path.Combine(entry, "transcript.db");
            if (!File.Exists(dbPath)) continue;
            var (id, _) = ReadSummary(dbPath);
            if (id == sessionId) return true;
        }
        return false;
    }

    private static (string? SessionId, long Count) ReadSummary(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (SELECT session_id FROM sessions LIMIT 1), (SELECT COALESCE(MAX(ordinal) + 1, 0) FROM messages)";
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1)) : (null, 0);
    }

    private static SqliteConnection Coordinator(string dir)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = CoordinatorPath(dir) }.ToString());
        connection.Open();
        return connection;
    }

    // ---- test cases ----

    private static void RoundTripSeparateFiles()
    {
        var dir = TempDir("roundtrip");
        var messages = new List<MessageRecord>
        {
            TextMessage("user", "ünïcode ✓ 中文 🚀"),
            MessageRecord.Single("tool", new ToolResultPart("c1", new string('r', 10_000))),
            new MessageRecord("user", [new TextPart("pic"), new ImagePart("img", "att/img.png")]),
        };
        using (var store = Open(dir))
        {
            Assert(store.AppendMessage("alpha", messages[0]) == 0, "alpha appends from 0");
            Assert(store.AppendMessage("beta", messages[1]) == 0, "beta appends from 0");
            Assert(store.AppendMessage("alpha", messages[2]) == 1, "dense ordinal in alpha");
        }
        using (var reopened = Open(dir))
        {
            CompareContents(reopened.ReadRange("alpha", 0, 2), messages[0..1].Concat(messages[2..3]).ToList(), "alpha");
            CompareContents(reopened.ReadRange("beta", 0, 1), [messages[1]], "beta");
            var index = reopened.ListSessionsMeta().ToDictionary(m => m.SessionId, m => m.MessageCount);
            Assert(index.Count == 2, $"sessions listed {index.Count}");
            Assert(index["alpha"] == 2 && index["beta"] == 1, "counts per session file");
            Assert(Directory.GetDirectories(SessionsRoot(dir)).Length == 2, "exactly one dir per session");
        }
    }

    private static void PreviewAndFetch()
    {
        var dir = TempDir("preview");
        var big = new string('B', 40_000);
        using (var store = Open(dir))
        {
            store.AppendMessage("pv", TextMessage("user", "hello"));
            store.AppendMessage("pv", MessageRecord.Single("tool", new ToolResultPart("c1", big)));
        }
        using (var store = Open(dir))
        {
            var preview = store.OpenRecentPreview("pv", 10, 24);
            Assert(preview.Items.Count == 2, $"items {preview.Items.Count}");
            var bigPart = preview.Items[1].Parts.Single();
            Assert(bigPart.Kind == "toolResult" && bigPart.Preview.Length == 24 && bigPart.Preview == big[..24],
                "bounded preview mismatch");
            Assert(store.FetchToolResult("pv", 1, 0) == big, "fetch full content mismatch");
            AssertThrows(() => store.FetchToolResult("pv", 0, 0), "fetch non-result part not rejected");
        }
    }

    private static void StreamingBatches()
    {
        var dir = TempDir("streaming");
        using (var store = Open(dir))
        {
            store.StreamDraft("sd", "stream-A", "alpha ");
            store.StreamDraft("sd", "stream-A", "beta ");
            var finalized = store.FinalizeStream("sd", "stream-A", TextMessage("assistant", "alpha beta"));
            Assert(finalized == 0, $"finalize ordinal {finalized}");
            VerifyReads(dir, store, "sd", TextMessage("assistant", "alpha beta"));
        }
        using (var store = Open(dir))
        {
            VerifyReads(dir, store, "sd", TextMessage("assistant", "alpha beta"));
        }
    }

    private static void StreamingReopenMidStream()
    {
        var dir = TempDir("stream-reopen");
        using (var store = Open(dir))
        {
            store.StreamDraft("sr", "stream-R", "part1 ");
            store.StreamDraft("sr", "stream-R", "part2");
        }
        using (var store = Open(dir))
        {
            AssertEquivalent(() => store.StreamDraft("sr", "stream-R", "more"), "draft identity lost across reopen");
            var finalized = store.FinalizeStream("sr", "stream-R", TextMessage("assistant", "part1 part2"));
            Assert(finalized == 0, "reopen finalize");
        }
    }

    private static void SuffixEdits()
    {
        var dir = TempDir("suffix");
        var messages = new List<MessageRecord> { TextMessage("user", "m0"), TextMessage("assistant", "m1"), TextMessage("user", "m2") };
        using var store = Open(dir);
        foreach (var message in messages) store.AppendMessage("se", message);

        Assert(store.ReplaceSuffix("se", 1, [TextMessage("assistant", "new1")]) == 2, "replace count");
        CompareContents(store.ReadRange("se", 0, 2), [messages[0], TextMessage("assistant", "new1")], "after replace");

        Assert(store.ReplaceSuffix("se", 1, []) == 1, "truncate count");
        CompareContents(store.ReadRange("se", 0, 1), [messages[0]], "after truncate");

        AssertThrows(() => store.ReplaceSuffix("se", 2, messages[..1]), "out-of-range replace not rejected");
        AssertThrows(() => store.ReplaceSuffix("nope", 0, messages[..1]), "unknown-session replace not rejected");
    }

    private static void AssignmentAuthority()
    {
        var dir = TempDir("assign");
        using (var store = Open(dir))
        {
            store.AppendMessage("as", TextMessage("user", "content"));
            store.CreateAssignment(new AssignmentRecord("asg-1", "as", "title", AssignmentStatus.Open, null));
            store.SettleAssignment(new SettleAssignmentRequest("asg-1", "done"));
            Assert(store.GetAssignment("asg-1").ResultText == "done", "settle round trip");
            AssertThrows(() => store.GetAssignment("asg-missing"), "unknown assignment not rejected");
        }
        using (var store = Open(dir))
        {
            store.DeleteSession("as", DeletePolicy.RequireSettled); // file gone; coordinator keeps history
            var record = store.GetAssignment("asg-1");
            Assert(record.Status == AssignmentStatus.Settled && record.TargetSessionId == "as",
                "assignment record must outlive its target session");
            Assert(!SessionDirPresent(dir, "as"), "session file still present after delete");
        }
    }

    private static void DeletePolicies()
    {
        var dir = TempDir("policies");
        using var store = Open(dir);
        store.AppendMessage("dp", TextMessage("user", "content"));
        store.CreateAssignment(new AssignmentRecord("asg-dp", "dp", "t", AssignmentStatus.Open, null));
        AssertThrows(() => store.DeleteSession("dp", DeletePolicy.RequireSettled),
            "RequireSettled must fail while an open assignment targets the session");
        Assert(SessionDirPresent(dir, "dp"), "session disappeared under RequireSettled");
        store.SettleAssignment(new SettleAssignmentRequest("asg-dp", "ok"));
        store.DeleteSession("dp", DeletePolicy.RequireSettled); // settled -> deletable
        Assert(store.GetAssignment("asg-dp").Status == AssignmentStatus.Settled, "settled record preserved");

        store.AppendMessage("dp2", TextMessage("user", "content2"));
        store.CreateAssignment(new AssignmentRecord("asg-dp2", "dp2", "t", AssignmentStatus.Open, null));
        store.DeleteSession("dp2", DeletePolicy.CancelOpen);
        Assert(store.GetAssignment("asg-dp2").Status == AssignmentStatus.Cancelled, "CancelOpen must cancel");
        Assert(!SessionDirPresent(dir, "dp2"), "cancelled session file must be removed");

        AssertThrows(() => store.DeleteSession("never", DeletePolicy.RequireSettled), "unknown delete not rejected");
    }

    private static void CompanionCleanup()
    {
        var dir = TempDir("companions");
        using (var store = Open(dir))
        {
            store.AppendMessage("cc", TextMessage("user", "content"));
            store.DeleteSession("cc", DeletePolicy.RequireSettled);
        }
        var sessionDirs = Directory.GetDirectories(SessionsRoot(dir), "s-*");
        Assert(sessionDirs.Length == 0, $"leftover session file count {sessionDirs.Length}");
    }

    private static void DeletionRecoveryFilePresent()
    {
        // Simulate a kill between deletion steps 1 and 2: intent marker
        // committed, session file still on disk. A subsequent Open() must
        // remove the file and clear the marker before serving anything.
        var dir = TempDir("recovery-present");
        var sessionId = "rec";
        using (var store = Open(dir))
        {
            store.AppendMessage(sessionId, TextMessage("user", "content"));
        }
        using (var coordinator = Coordinator(dir))
        {
            using var command = coordinator.CreateCommand();
            command.CommandText = "INSERT INTO pending_deletions(session_id) VALUES(@s)";
            command.Parameters.AddWithValue("@s", sessionId);
            command.ExecuteNonQuery();
        }
        using (var store = Open(dir))
        {
            Assert(!SessionDirPresent(dir, sessionId), "recovery left the session file in place");
            Assert(store.ListSessionsMeta().All(m => m.SessionId != sessionId), "recovered session still listed");
        }
        using (var coordinator = Coordinator(dir))
        {
            using var command = coordinator.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pending_deletions";
            Assert((long)(command.ExecuteScalar() ?? 0L) == 0, "recovery left an intent marker behind");
        }
        // The coordinator's assignment history still serves; a NEW session with
        // the same id can be recreated cleanly afterwards.
        using (var store = Open(dir))
        {
            store.AppendMessage(sessionId, TextMessage("user", "recreated"));
            Assert(store.ReadRange(sessionId, 0, 1).Length == 1, "recreated session works after recovery");
        }
    }

    private static void DeletionRecoveryFileGone()
    {
        // Simulate a kill between deletion steps 2 and 3: file gone, marker left.
        var dir = TempDir("recovery-gone");
        using (var store = Open(dir))
        {
            store.AppendMessage("rec2", TextMessage("user", "content"));
            store.DeleteSession("rec2", DeletePolicy.RequireSettled); // removes marker normally
            // Then re-inject a stale marker with the file GONE.
        }
        using (var coordinator = Coordinator(dir))
        {
            Assert(!SessionDirPresent(dir, "rec2"), "unexpected leftover");
            using var command = coordinator.CreateCommand();
            command.CommandText = "INSERT INTO pending_deletions(session_id) VALUES('rec2')";
            command.ExecuteNonQuery();
        }
        using (var store = Open(dir))
        {
            // Recovery removes the marker; nothing else observable.
        }
        using (var coordinator = Coordinator(dir))
        {
            using var command = coordinator.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pending_deletions";
            Assert((long)(command.ExecuteScalar() ?? 0L) == 0, "marker-only recovery failed");
        }
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
        using (var reopened = Open(dir))
        {
            CompareContents(reopened.ReadRange("rp", 0, 2), messages, "reopened");
            var preview = reopened.OpenRecentPreview("rp", 10, 12);
            Assert(preview.Items[1].Parts[0].Preview == new string('z', 12), "reopened preview");
            Assert(reopened.FetchToolResult("rp", 1, 0).Length == 10_000, "reopened fetch");
            Assert(reopened.GetAssignment("asg-r").ResultText == "settled!", "reopened assignment");
        }
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
    }

    private static void VerifyReads(string dir, SqlitePerSessionTranscriptStore store, string session, MessageRecord expected)
    {
        VerifyReads(dir, store, session, [expected]);
    }

    private static void VerifyReads(string dir, SqlitePerSessionTranscriptStore store, string session, IReadOnlyList<MessageRecord> expected)
    {
        CompareContents(store.ReadRange(session, 0, expected.Count), expected, $"range[{session}]");
        var meta = store.ListSessionsMeta().Single(m => m.SessionId == session);
        Assert(meta.MessageCount == expected.Count, $"count {meta.MessageCount} != {expected.Count}");
    }
}