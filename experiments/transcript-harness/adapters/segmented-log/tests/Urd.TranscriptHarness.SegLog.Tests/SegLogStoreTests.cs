using Urd.TranscriptHarness.Adapter.SegLog;
using Urd.TranscriptHarness.Contracts;
using Urd.TranscriptHarness.Core;
using Xunit;

namespace Urd.TranscriptHarness.SegLog.Tests;

// Focused correctness facts for the segmented binary log adapter, verified
// through the shared oracle (ModelOps replay + full content comparison) — the
// same evidence path the runner's validate uses. Synthetic OS-temp data only;
// no benchmark timing and no large payloads (a test-only small segment target
// exercises rolling and the oversized exception; the parameterless constructor
// keeps the 8 MiB default for the plugin loader).

public sealed class SegLogStoreTests
{
    public static TheoryData<int> PreviewChars => [0, 1, 8, 24, 64, 65, 200];

    private static string NewDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), "urd-seglog-tests", $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SegLogTranscriptStore Open(string dir, long segmentTarget = SegLogTranscriptStore.DefaultSegmentTarget)
    {
        var store = new SegLogTranscriptStore(segmentTarget);
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

    private static byte[] FileBytes(string path) => File.ReadAllBytes(path);

    private static string[] SegmentFiles(string dir)
    {
        var sessionsRoot = Path.Combine(dir, "sessions");
        return Directory.EnumerateDirectories(sessionsRoot)
            .SelectMany(d => Directory.EnumerateFiles(d, "seg-*.log"))
            .ToArray();
    }

    private static string SegmentPath(string dir, string sessionId, long segmentId) => Path.Combine(
        dir, "sessions", SegmentDirs.DirName(sessionId), $"seg-{segmentId:D6}.log");

    [Fact]
    public void Append_windows_and_reopen_verify_against_the_oracle()
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
                        new ToolResultPart($"call-{i}", $"result-{i}-" + new string('r', 400)),
                    ])
                    : Text("user", $"message {i} newline\ninside 😀");
                var expected = ModelOps.AppendMessage(model, i % 2 == 0 ? "a" : "b", message);
                Assert.Equal(expected, store.AppendMessage(i % 2 == 0 ? "a" : "b", message));
            }
            Oracle.VerifyAll(store, model);
        }
        using (var reopened = Open(dir))
        {
            Oracle.VerifyAll(reopened, model);
        }
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ReadRange_openRecent_pageOlder_match_oracle_windows()
    {
        var dir = NewDir("windows");
        var model = new OracleModel();
        using var store = Open(dir, segmentTarget: 300); // small target so multiple segments participate
        for (var i = 0; i < 10; i++)
        {
            ModelOps.AppendMessage(model, "s", Text(i % 2 == 0 ? "user" : "assistant", $"m{i}"));
            store.AppendMessage("s", Text(i % 2 == 0 ? "user" : "assistant", $"m{i}"));
        }
        Assert.True(SegmentFiles(dir).Length > 1,
            "rolling should have produced multiple segments"); // 49-byte frames, 300-byte target, 10 messages
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
    public void Records_roll_or_get_dedicated_segments_per_the_budget()
    {
        var dir = NewDir("rolling");
        var model = new OracleModel();
        using var store = Open(dir, segmentTarget: 400);
        for (var i = 0; i < 6; i++)
        {
            var message = Text("user", $"m{i}-" + new string('x', 90));
            ModelOps.AppendMessage(model, "s", message);
            store.AppendMessage("s", message);
        }
        // Several budgeted segments hold the whole session; each segment is
        // whole-frame addressed by the index, and content verifies.
        var segmentsBeforeOversize = SegmentFiles(dir).Length;
        Assert.True(segmentsBeforeOversize >= 2, "rolling should produce multiple segments");
        Oracle.VerifyAll(store, model);

        // Oversized exception: one frame beyond the target → dedicated segment;
        // the next append starts yet another segment.
        var huge = Text("user", "big-" + new string('y', 500));
        store.AppendMessage("s", huge);
        ModelOps.AppendMessage(model, "s", huge);
        var files = SegmentFiles(dir);
        Assert.Equal(segmentsBeforeOversize + 1, files.Length);
        Oracle.VerifyAll(store, model);
        // Next append rolls further; everything still verifies.
        store.AppendMessage("s", Text("user", "tail"));
        ModelOps.AppendMessage(model, "s", Text("user", "tail"));
        Assert.Equal(segmentsBeforeOversize + 2, SegmentFiles(dir).Length);
        Oracle.VerifyAll(store, model);
        store.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Stream_batches_persist_per_call_and_finalization_is_atomically_visible()
    {
        var dir = NewDir("streaming");
        var model = new OracleModel();
        var message = Text("assistant", "final body");
        using var store = Open(dir);
        store.StreamDraft("s", "stream-1", "alpha");
        // Persisted marker/batch survives reopen: the stream continues after a process-like restart.
        store.Dispose();
        var reopened = Open(dir);
        reopened.StreamDraft("s", "stream-1", "beta");
        Assert.Throws<InvalidOperationException>(() => reopened.StreamDraft("s", "stream-2", "other"));
        // Unfinalized drafts are never served as messages.
        Assert.Empty(reopened.OpenRecent("s", 10));
        model.ActiveDrafts.Add("stream-1");
        var expected = ModelOps.FinalizeStream(model, "s", "stream-1", message);
        var ordinal = reopened.FinalizeStream("s", "stream-1", message);
        Assert.Equal(expected, ordinal);
        var recent = reopened.OpenRecent("s", 10);
        Assert.Single(recent);
        Assert.Equal(Describe([message]), Describe(recent));
        // After finalize there is no active draft: finalizing again is an error.
        Assert.Throws<InvalidOperationException>(() => reopened.FinalizeStream("s", "stream-1", message));
        // A fresh stream is accepted after finalize; a finalize can also be a
        // brand-new session's first message.
        reopened.StreamDraft("t", "stream-t", "start");
        model.ActiveDrafts.Add("stream-t");
        Assert.Equal(
            ModelOps.AppendMessage(model, "t", Text("user", "warmup")),
            reopened.AppendMessage("t", Text("user", "warmup")));
        var done = ModelOps.FinalizeStream(model, "t", "stream-t", Text("user", "done t"));
        Assert.Equal(done, reopened.FinalizeStream("t", "stream-t", Text("user", "done t")));
        reopened.StreamDraft("u", "stream-u", "solo");
        model.ActiveDrafts.Add("stream-u");
        expected = ModelOps.FinalizeStream(model, "u", "stream-u", message);
        Assert.Equal(expected, reopened.FinalizeStream("u", "stream-u", message));
        Oracle.VerifyAll(reopened, model);
        reopened.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ReplaceSuffix_down_to_zero_then_append_or_finalize_works_in_place_and_after_reopen()
    {
        // Reviewer probe: Publish used head.Count==0 to choose INSERT, so an
        // EXISTING pointer row at count 0 (ReplaceSuffix(…, 0, …)) made the
        // next append/finalize fail UNIQUE session_ptr.name. Deciding on row
        // existence (head null) fixes both paths, same instance and reopened.
        var model = new OracleModel();
        var dir = NewDir("zero");
        using (var store = Open(dir, segmentTarget: 400))
        {
            for (var i = 0; i < 3; i++)
            {
                ModelOps.AppendMessage(model, "s", Text("user", $"m{i}-" + new string('x', 80)));
                store.AppendMessage("s", Text("user", $"m{i}-" + new string('x', 80)));
            }
            Assert.Equal(0, store.ReplaceSuffix("s", 0, []));
            ModelOps.ReplaceSuffix(model, "s", 0, []);
            Assert.Equal(0, store.ListSessionsMeta().Single(m => m.SessionId == "s").MessageCount);
            Oracle.VerifyAll(store, model); // empty session, pointer row still exists
            // Same-instance: UPDATE branch (not INSERT) on the count-0 row.
            Assert.Equal(
                ModelOps.AppendMessage(model, "s", Text("user", "after-empty")),
                store.AppendMessage("s", Text("user", "after-empty")));
            Oracle.VerifyAll(store, model);
            // Same-instance finalize on the count-0 row (stream never existed).
            store.StreamDraft("s", "stream-z", "batch");
            model.ActiveDrafts.Add("stream-z");
            var zmsg = Text("user", "finalize-on-zero");
            Assert.Equal(
                ModelOps.FinalizeStream(model, "s", "stream-z", zmsg),
                store.FinalizeStream("s", "stream-z", zmsg));
            Oracle.VerifyAll(store, model);
            // Back to count 0, then reopen and drive both operations again.
            Assert.Equal(0, store.ReplaceSuffix("s", 0, []));
            ModelOps.ReplaceSuffix(model, "s", 0, []);
            Oracle.VerifyAll(store, model);
        }
        using (var reopened = Open(dir, 400))
        {
            Assert.Equal(
                ModelOps.AppendMessage(model, "s", Text("user", "reopened-after-empty")),
                reopened.AppendMessage("s", Text("user", "reopened-after-empty")));
            reopened.StreamDraft("s", "stream-z2", "batch");
            model.ActiveDrafts.Add("stream-z2");
            var z2 = Text("user", "reopened-finalize");
            Assert.Equal(
                ModelOps.FinalizeStream(model, "s", "stream-z2", z2),
                reopened.FinalizeStream("s", "stream-z2", z2));
            Oracle.VerifyAll(reopened, model);
        }
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Open_truncates_the_active_slot_tail_even_at_zero_count()
    {
        // Reviewer probe: with a count-0 pointer row (or a fresh oversize slot)
        // there are no referenced rows, so the per-segment recovery loop did
        // not touch the active file and an unindexed tail survived Open.
        // NOTE: ReplaceSuffix(…, 0, …) keeps cur_end at the old indexed end
        // (the deleted message's payload bytes remain as declared garbage);
        var dir = NewDir("zero-tail");
        var model = new OracleModel();
        string activePath;
        long cleanEnd;
        using (var store = Open(dir, segmentTarget: 300))
        {
            ModelOps.AppendMessage(model, "s", Text("user", "one-" + new string('a', 80)));
            store.AppendMessage("s", Text("user", "one-" + new string('a', 80)));
            Assert.Equal(0, store.ReplaceSuffix("s", 0, [])); // pointer row stays, count 0, rows gone
            ModelOps.ReplaceSuffix(model, "s", 0, []);
            activePath = SegmentPath(dir, "s", 0);
            cleanEnd = new FileInfo(activePath).Length; // indexed end (cur_end) incl. retained garbage below it
        }
        // Simulate an interrupted data stage: unindexed bytes beyond cur_end.
        File.AppendAllText(activePath, new string('J', 77));
        Assert.Equal(cleanEnd + 77, new FileInfo(activePath).Length);
        using (var reopened = Open(dir, segmentTarget: 300))
        {
            Assert.Equal(cleanEnd, new FileInfo(activePath).Length); // truncated to the authoritative cur_end
            Oracle.VerifyAll(reopened, model);
            // The session still appends normally through the same pointer row.
            Assert.Equal(
                ModelOps.AppendMessage(model, "s", Text("user", "after-zero-tail")),
                reopened.AppendMessage("s", Text("user", "after-zero-tail")));
            Oracle.VerifyAll(reopened, model);
        }
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ReplaceSuffix_appends_without_rewriting_and_reassigns_dense_ordinals()
    {
        var dir = NewDir("suffix");
        var model = new OracleModel();
        using (var store = Open(dir, segmentTarget: 400))
        {
            for (var i = 0; i < 6; i++)
            {
                var message = Text(i % 2 == 0 ? "user" : "assistant", $"original-{i}-" + new string('p', 40));
                ModelOps.AppendMessage(model, "s", message);
                store.AppendMessage("s", message);
            }
            var published = FileBytes(SegmentPath(dir, "s", 0));
            var garbageBefore = store.RetainedGarbageBytes;
            var edit = new List<MessageRecord> { Text("user", "edited") };
            Assert.Equal(
                ModelOps.ReplaceSuffix(model, "s", 2, edit),
                store.ReplaceSuffix("s", 2, edit));
            // Segment immutability: the published file's bytes are untouched by edits;
            // replacement records were appended, and the superseded bytes are reported.
            Assert.Equal(published, FileBytes(SegmentPath(dir, "s", 0)));
            Assert.True(store.RetainedGarbageBytes > garbageBefore, "retained garbage must be reported");
            Oracle.VerifyAll(store, model);
            // A shorter replacement truncates the logical tail, dense renumbering follows.
            Assert.Equal(
                ModelOps.ReplaceSuffix(model, "s", 1, []),
                store.ReplaceSuffix("s", 1, []));
            Oracle.VerifyAll(store, model);
        }
        using (var reopened = Open(dir, segmentTarget: 400))
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
        var openAsg = new AssignmentRecord("asg-open", "s2", "work", AssignmentStatus.Open, null);
        store.CreateAssignment(settled);
        store.CreateAssignment(openAsg);
        ModelOps.CreateAssignment(model, settled);
        ModelOps.CreateAssignment(model, openAsg);
        Oracle.VerifyAll(store, model);

        // RequireSettled refuses; nothing changed.
        Assert.Throws<InvalidOperationException>(() => store.DeleteSession("s2", DeletePolicy.RequireSettled));
        Oracle.VerifyAll(store, model);
        // CancelOpen cancels open assignments in the same logical operation; records survive.
        store.DeleteSession("s2", DeletePolicy.CancelOpen);
        ModelOps.DeleteSession(model, "s2", DeletePolicy.CancelOpen);
        Assert.Equal(AssignmentStatus.Cancelled, store.GetAssignment("asg-open").Status);
        Oracle.VerifyAll(store, model);
        Assert.Throws<InvalidOperationException>(() => store.DeleteSession("s2", DeletePolicy.RequireSettled));
        // Deleting a session with no open assignments succeeds and preserves other state.
        store.DeleteSession("s1", DeletePolicy.RequireSettled);
        ModelOps.DeleteSession(model, "s1", DeletePolicy.RequireSettled);
        Oracle.VerifyAll(store, model);
        // Duplicate ids and unknown ids are errors, never silent.
        Assert.Throws<InvalidOperationException>(() => store.CreateAssignment(settled));
        Assert.Throws<InvalidOperationException>(() => store.SettleAssignment(new SettleAssignmentRequest("asg-unknown", "x")));
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
        var huge = "huge-" + new string('z', 100_000) + "-end";
        using var store = Open(dir);
        ModelOps.AppendMessage(model, "s", Text("user", "question about something"));
        store.AppendMessage("s", Text("user", "question about something"));
        var toolMessage = ModelOps.AppendMessage(model, "s", new MessageRecord("assistant",
        [
            new TextPart("thinking aloud"),
            new ToolCallPart("call-9", "probe", "{\"depth\":1,\"x\":2}"),
            new ToolResultPart("call-9", huge),
        ]));
        store.AppendMessage("s", new MessageRecord("assistant",
        [
            new TextPart("thinking aloud"),
            new ToolCallPart("call-9", "probe", "{\"depth\":1,\"x\":2}"),
            new ToolResultPart("call-9", huge),
        ]));

        var count = store.ListSessionsMeta().Single().MessageCount;
        var preview = store.OpenRecentPreview("s", (int)count, previewChars);
        Oracle.ComparePreviewWindow(preview, model.Sessions["s"], 0, previewChars, "s");

        var fetched = store.FetchToolResult("s", toolMessage, 2);
        Assert.Equal(huge, fetched); // exact full content through the targeted single-frame read
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
            "\u6BCF\u65E5 unicode, mixing a\U0001F600b and \u0301combining marks",
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
    public void Recovery_truncates_the_active_tail_sweeps_unreferenced_and_keeps_referenced_segments()
    {
        var dir = NewDir("recovery");
        var model = new OracleModel();
        string activePath;
        using (var store = Open(dir, segmentTarget: 300))
        {
            for (var i = 0; i < 8; i++)
            {
                ModelOps.AppendMessage(model, "s", Text("user", $"acked-{i}-" + new string('a', 60)));
                store.AppendMessage("s", Text("user", $"acked-{i}-" + new string('a', 60)));
            }
            activePath = SegmentPath(dir, "s", 1); // two frames fit in seg 0; the rest roll
        }
        var oldLength = new FileInfo(activePath).Length;
        // Simulate an interrupted data stage: unindexed tail bytes in the ACTIVE segment,
        // plus a wholly unreferenced segment beyond the active pointer (interrupted roll),
        // plus an unreferenced session directory (deleted session).
        File.AppendAllText(activePath, new string('J', 64));
        File.WriteAllText(
            Path.Combine(dir, "sessions", SegmentDirs.DirName("s"), "seg-000009.log"), "orphan segment");
        Directory.CreateDirectory(Path.Combine(dir, "sessions", SegmentDirs.DirName("deleted")));
        File.WriteAllText(Path.Combine(dir, "sessions", SegmentDirs.DirName("deleted"), "seg-000000.log"), "orphan dir");

        using (var reopened = Open(dir, segmentTarget: 300))
        {
            Assert.Equal(oldLength, new FileInfo(activePath).Length); // unindexed tail truncated by content
            Oracle.VerifyAll(reopened, model); // before-state is exactly what remains
            Assert.True(File.Exists(activePath), "referenced segment survives");
            // A referenced OLDER segment is never swept even when an edit left it
            // (logically) superseded — referenced bytes are never deleted.
        }
        Assert.False(File.Exists(
            Path.Combine(dir, "sessions", SegmentDirs.DirName("s"), "seg-000009.log")));
        Assert.False(Directory.Exists(Path.Combine(dir, "sessions", SegmentDirs.DirName("deleted"))));
        // A referenced segment missing or short is corruption, reported loudly (never "repaired").
        File.Delete(activePath);
        Assert.Throws<InvalidOperationException>(() => Open(dir, segmentTarget: 300).Dispose());
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Retained_garbage_is_counted_and_old_bytes_stay_verbatim()
    {
        var dir = NewDir("retention");
        var model = new OracleModel();
        using var store = Open(dir);
        for (var i = 0; i < 3; i++)
        {
            ModelOps.AppendMessage(model, "s", Text("user", $"m{i}-" + new string('r', 100)));
            store.AppendMessage("s", Text("user", $"m{i}-" + new string('r', 100)));
        }
        var segmentFile = SegmentPath(dir, "s", 0);
        var before = FileBytes(segmentFile);
        var retainedBefore = store.RetainedGarbageBytes;
        store.ReplaceSuffix("s", 1, [Text("user", "replacement")]);
        ModelOps.ReplaceSuffix(model, "s", 1, [Text("user", "replacement")]);
        // Append-only: the old bytes are preserved verbatim; the replacement frame grew after them.
        var after = FileBytes(segmentFile);
        Assert.True(after.Length > before.Length);
        Assert.Equal(before, after[..before.Length]);
        // Superseded payload bytes stay resident (no compaction claimed) and are reported:
        // two superseded payloads of (38 JSON overhead + 103 content chars) = 282 bytes.
        Assert.Equal(2 * (38 + 103), store.RetainedGarbageBytes - retainedBefore);
        Oracle.VerifyAll(store, model);
        store.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Describe_declares_layout_tier_and_costs_honestly()
    {
        using var store = Open(NewDir("describe"));
        var descriptor = store.Describe();
        Assert.Equal("SegLogIndex", descriptor.StoreName);
        Assert.Equal(AckTier.OsFlush, descriptor.AckTier);
        Assert.Contains("no fsync", descriptor.SettingsNote, StringComparison.OrdinalIgnoreCase);
        // Honesty guards: flush-to-disk exists in .NET; OsFlush is chosen, not forced.
        Assert.DoesNotContain("no random-access", descriptor.SettingsNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("flushToDisk", descriptor.SettingsNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fsync", descriptor.Notes, StringComparison.OrdinalIgnoreCase);
        foreach (var declared in new[] { "authority", "RetainedGarbageBytes", "no compaction", "truncates",
                     "RequireSettled", "CancelOpen", "8 MiB" })
        {
            Assert.Contains(declared, descriptor.Notes, StringComparison.OrdinalIgnoreCase);
        }
        store.Dispose();
    }
}