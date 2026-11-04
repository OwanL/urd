using Urd.TranscriptHarness.Adapter.JsonlStore;
using Urd.TranscriptHarness.Contracts;
using Urd.TranscriptHarness.Core;
using Xunit;

namespace Urd.TranscriptHarness.Jsonl.Tests;

// Focused correctness facts for the JSONL offset-index adapter, verified
// through the shared oracle (ModelOps replay + full content comparison) —
// the same evidence path the runner's validate uses. Synthetic OS-temp data
// only; no benchmark timing and no real content.

public sealed class JsonlStoreTests
{
    public static TheoryData<int> PreviewChars => [0, 1, 8, 24, 64, 65, 200];

    private static string NewDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), "urd-jsonl-tests", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static JsonlTranscriptStore Open(string dir)
    {
        var store = new JsonlTranscriptStore();
        store.Open(dir, dir + "-attachments");
        return store;
    }

    private static MessageRecord Text(string role, string content) => new(role, [new TextPart(content)]);

    // Deep message shape: records hold part-list references, so compare by content.
    private static string[] Describe(IReadOnlyList<MessageRecord> messages)
    {
        return messages.Select(m => m.Role + "|" + string.Join(",", m.Parts.Select(p => p switch
        {
            TextPart t => "text:" + t.Content,
            ToolCallPart c => "call:" + c.ToolCallId + ":" + c.Name + ":" + c.ArgsJson,
            ToolResultPart r => "result:" + r.ToolCallId + ":" + r.Content,
            ImagePart i => "image:" + i.RelativePath,
            var other => other.GetType().Name,
        }))).ToArray();
    }

    [Fact]
    public void Append_and_reopen_verify_against_the_oracle()
    {
        var dir = NewDir("roundtrip");
        var model = new OracleModel();
        using (var store = Open(dir))
        {
            for (var i = 0; i < 7; i++)
            {
                var message = i % 3 == 1
                    ? new MessageRecord("assistant",
                    [
                        new TextPart($"thinking {i}"),
                        new ToolCallPart($"call-{i}", "probe", "{\"depth\":1}"),
                        new ToolResultPart($"call-{i}", $"result-{i}-" + new string('r', 500)),
                    ])
                    : Text("user", $"message {i} with newline\ninside and unicode 😀");
                var expected = ModelOps.AppendMessage(model, i % 2 == 0 ? "a" : "b", message);
                var reported = store.AppendMessage(i % 2 == 0 ? "a" : "b", message);
                Assert.Equal(expected, reported);
            }
            Oracle.VerifyAll(store, model);
        }
        using (var reopened = Open(dir))
        {
            Oracle.VerifyAll(reopened, model); // full catalog state survives reopen
        }
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ReadRange_openRecent_pageOlder_match_oracle_windows()
    {
        var dir = NewDir("windows");
        var model = new OracleModel();
        using var store = Open(dir);
        for (var i = 0; i < 10; i++)
        {
            var message = Text(i % 2 == 0 ? "user" : "assistant", $"m{i}");
            ModelOps.AppendMessage(model, "s", message);
            store.AppendMessage("s", message);
        }
        Assert.Equal(10, store.ListSessionsMeta().Single(m => m.SessionId == "s").MessageCount);
        Assert.Equal(Describe(ModelOps.WindowOf(model, "s", 3, 7)), Describe(store.ReadRange("s", 3, 7)));
        Assert.Equal(Describe(ModelOps.WindowOf(model, "s", 8, null)), Describe(store.OpenRecent("s", 2)));
        Assert.Equal(Describe(ModelOps.WindowOf(model, "s", 4, 7)), Describe(store.PageOlder("s", beforeOrdinal: 7, count: 3)));
        // Clamp-before-subtract semantics.
        Assert.Equal(Describe(ModelOps.WindowOf(model, "s", 0, null)), Describe(store.PageOlder("s", beforeOrdinal: 99, count: 50)));
        Assert.Empty(store.PageOlder("s", beforeOrdinal: 0, count: 3));
        foreach (var (from, to) in new[] { (-1L, 3L), (3, 1L), (4, 99L) })
        {
            Assert.Throws<InvalidOperationException>(() => store.ReadRange("s", from, to));
        }
        // Absence is not an error for the windowed reads.
        Assert.Empty(store.ReadRange("absent", 0, 3));
        Assert.Empty(store.OpenRecent("absent", 5));
        Assert.Empty(store.PageOlder("absent", beforeOrdinal: 5, count: 2));
        Oracle.VerifyAll(store, model);
        store.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Stream_batches_persist_per_call_and_finalization_is_atomic_visible()
    {
        var dir = NewDir("streaming");
        var model = new OracleModel();
        var message = Text("assistant", "final body");
        using var store = Open(dir);
        store.StreamDraft("s", "stream-1", "alpha");
        // Persisted marker/batch survives reopen: the stream can continue after a process-like restart.
        store.Dispose();
        var reopened = Open(dir);
        reopened.StreamDraft("s", "stream-1", "beta");
        // One active draft per session; a different streamId while active is an error.
        Assert.Throws<InvalidOperationException>(() => reopened.StreamDraft("s", "stream-2", "other"));
        // Unfinalized drafts are never served as messages.
        Assert.Empty(reopened.OpenRecent("s", 10));
        model.ActiveDrafts.Add("stream-1");
        var ordinal = reopened.FinalizeStream("s", "stream-1", message);
        var expected = ModelOps.FinalizeStream(model, "s", "stream-1", message);
        Assert.Equal(expected, ordinal);
        var recent = reopened.OpenRecent("s", 10);
        Assert.Single(recent);
        Assert.Equal(Describe([message]), Describe(recent));
        // After finalize there is no active draft: finalizing again is an error.
        Assert.Throws<InvalidOperationException>(() => reopened.FinalizeStream("s", "stream-1", message));
        // A fresh stream is accepted after finalize.
        reopened.StreamDraft("s", "stream-2", "next");
        Oracle.VerifyAll(reopened, model);
        reopened.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ReplaceSuffix_renumbers_truncates_and_reopens_without_stale_content()
    {
        var dir = NewDir("suffix");
        var model = new OracleModel();
        using (var store = Open(dir))
        {
            for (var i = 0; i < 6; i++)
            {
                var message = Text(i % 2 == 0 ? "user" : "assistant", $"original-{i}");
                ModelOps.AppendMessage(model, "s", message);
                store.AppendMessage("s", message);
            }
            var edit = new List<MessageRecord> { Text("user", "edited") };
            Assert.Equal(
                ModelOps.ReplaceSuffix(model, "s", 2, edit),
                store.ReplaceSuffix("s", 2, edit));
            Oracle.VerifyAll(store, model);
            // A shorter replacement truncates the tail, dense renumbering follows.
            Assert.Equal(
                ModelOps.ReplaceSuffix(model, "s", 1, []),
                store.ReplaceSuffix("s", 1, []));
            Oracle.VerifyAll(store, model);
        }
        using (var reopened = Open(dir))
        {
            Oracle.VerifyAll(reopened, model); // no stale suffix content is served after reopen
        }
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Assignments_and_deletion_policies_match_the_invariants()
    {
        var dir = NewDir("assignments");
        var model = new OracleModel();
        using var store = Open(dir);
        foreach (var session in new[] { "s1", "s2" })
        {
            ModelOps.AppendMessage(model, session, Text("user", $"hello {session}"));
            store.AppendMessage(session, Text("user", $"hello {session}"));
        }
        var settled = new AssignmentRecord("asg-settled", "s1", "done already", AssignmentStatus.Settled, "ok");
        var open = new AssignmentRecord("asg-open", "s2", "work", AssignmentStatus.Open, null);
        store.CreateAssignment(settled);
        store.CreateAssignment(open);
        ModelOps.CreateAssignment(model, settled);
        ModelOps.CreateAssignment(model, open);
        Oracle.VerifyAll(store, model);

        // RequireSettled refuses; nothing changed.
        Assert.Throws<InvalidOperationException>(
            () => store.DeleteSession("s2", DeletePolicy.RequireSettled));
        Oracle.VerifyAll(store, model);
        // CancelOpen cancels open assignments in the same logical operation; records survive.
        store.DeleteSession("s2", DeletePolicy.CancelOpen);
        ModelOps.DeleteSession(model, "s2", DeletePolicy.CancelOpen);
        var cancelled = store.GetAssignment("asg-open");
        Assert.Equal(AssignmentStatus.Cancelled, cancelled.Status);
        Oracle.VerifyAll(store, model);
        // Deleting the now-deleted session again is an error.
        Assert.Throws<InvalidOperationException>(() => store.DeleteSession("s2", DeletePolicy.RequireSettled));
        // Deleting a session with no open assignments succeeds and preserves other state.
        store.DeleteSession("s1", DeletePolicy.RequireSettled);
        ModelOps.DeleteSession(model, "s1", DeletePolicy.RequireSettled);
        Oracle.VerifyAll(store, model);
        // Duplicate ids and unknown ids are errors, never silent.
        Assert.Throws<InvalidOperationException>(() => store.CreateAssignment(settled));
        Assert.Throws<InvalidOperationException>(
            () => store.SettleAssignment(new SettleAssignmentRequest("asg-unknown", "x")));
        Assert.Throws<InvalidOperationException>(() => store.GetAssignment("asg-unknown"));
        store.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Theory]
    [MemberData(nameof(PreviewChars))]
    public void Preview_is_bounded_exact_metadata_and_fetch_returns_exact_full_content(int previewChars)
    {
        var dir = NewDir($"preview-{previewChars}");
        var model = new OracleModel();
        using var store = Open(dir);
        var huge = "huge-" + new string('z', 100_000) + "-end";
        store.AppendMessage("s", Text("user", "question about something"));
        store.AppendMessage("s", new MessageRecord("assistant",
        [
            new TextPart("thinking aloud"),
            new ToolCallPart("call-9", "probe", "{\"depth\":1,\"x\":2}"),
            new ToolResultPart("call-9", huge),
        ]));
        ModelOps.AppendMessage(model, "s", Text("user", "question about something"));
        var toolMessage = ModelOps.AppendMessage(model, "s", new MessageRecord("assistant",
        [
            new TextPart("thinking aloud"),
            new ToolCallPart("call-9", "probe", "{\"depth\":1,\"x\":2}"),
            new ToolResultPart("call-9", huge),
        ]));

        var preview = store.OpenRecentPreview("s", (int)store.ListSessionsMeta().Single().MessageCount, previewChars);
        var windowSize = Math.Min(2, model.Count("s"));
        Oracle.ComparePreviewWindow(
            preview, model.Sessions["s"], windowSize - 2, previewChars, "s");

        var fetched = store.FetchToolResult("s", toolMessage, 2);
        Assert.Equal(huge, fetched); // exact full content through the targeted read
        Oracle.VerifyAll(store, model);
        store.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Unicode_and_embedded_newlines_round_trip()
    {
        var dir = NewDir("unicode");
        var model = new OracleModel();
        var contents = new[]
        {
            "emoji: \U0001F600\U0001F680 astral plane pairs",
            "每日 unicode, mixing a😀b and \u0301combining marks",
            "embedded\nnewlines\r\nand\ttabs \"quotes\" \\ backslashes",
        };
        using var store = Open(dir);
        foreach (var content in contents)
        {
            ModelOps.AppendMessage(model, "s", Text("user", content));
            store.AppendMessage("s", Text("user", content));
        }
        Oracle.VerifyAll(store, model);
        // Preview slicing equals content[..n] exactly — including cuts inside an astral pair.
        Assert.Equal(contents[0][..8], store.OpenRecentPreview("s", 3, 8).Items[0].Parts.Single().Preview);
        Assert.Equal(contents[0][..9], store.OpenRecentPreview("s", 3, 9).Items[0].Parts.Single().Preview);
        store.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Recovery_truncates_unindexed_tails_and_sweeps_orphans()
    {
        var dir = NewDir("recovery");
        var model = new OracleModel();
        string jsonlPath;
        using (var store = Open(dir))
        {
            for (var i = 0; i < 3; i++)
            {
                ModelOps.AppendMessage(model, "s", Text("user", $"acked-{i}"));
                store.AppendMessage("s", Text("user", $"acked-{i}"));
            }
            jsonlPath = Path.Combine(dir, "sessions", SessionDirs.DirName("s"), "messages-0.jsonl");
        }
        // Simulate an interrupted data stage: unindexed tail bytes plus orphans
        // an interrupted rewrite / post-delete would leave behind.
        var oldLength = new FileInfo(jsonlPath).Length;
        File.AppendAllText(jsonlPath, "{\"r\":\"user\",\"p\":[{\"k\":\"text\",\"c\":\"uncommitted\"}]}\n");
        File.WriteAllText(
            Path.Combine(dir, "sessions", SessionDirs.DirName("s"), "messages-9.jsonl"), "orphan generation");
        Directory.CreateDirectory(Path.Combine(dir, "sessions", SessionDirs.DirName("deleted")));
        File.WriteAllText(Path.Combine(dir, "sessions", SessionDirs.DirName("deleted"), "messages-0.jsonl"), "orphan");

        using (var reopened = Open(dir))
        {
            Assert.Equal(oldLength, new FileInfo(jsonlPath).Length); // unindexed tail truncated by content
            Oracle.VerifyAll(reopened, model); // before-state is exactly what remains
            Assert.True(File.Exists(jsonlPath), "referenced file survives");
        }
        Assert.False(File.Exists(Path.Combine(dir, "sessions", SessionDirs.DirName("s"), "messages-9.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(dir, "sessions", SessionDirs.DirName("deleted"))));
        // A referenced file that is missing is corruption, reported loudly (not recovery).
        File.Delete(jsonlPath);
        Assert.Throws<InvalidOperationException>(() => Open(dir).Dispose());
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Describe_declares_layout_tier_and_costs_honestly()
    {
        using var store = Open(NewDir("describe"));
        var descriptor = store.Describe();
        Assert.Equal("JsonlOffsetIndex", descriptor.StoreName);
        Assert.Equal(AckTier.OsFlush, descriptor.AckTier);
        Assert.Contains("no fsync", descriptor.SettingsNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fsync", descriptor.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SQLite", descriptor.SettingsNote);
        foreach (var declared in new[] { "generation rewrite", "no cross-file", "orphan", "RequireSettled", "CancelOpen" })
        {
            Assert.Contains(declared, descriptor.Notes, StringComparison.OrdinalIgnoreCase);
        }
        store.Dispose();
    }
}