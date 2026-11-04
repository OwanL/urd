using System.Text;
using Xunit;
using Urd.TranscriptHarness.Adapter.RocksDbStore;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.RocksDb.Tests;

// Bounded preview metadata, targeted tool-result fetch, assignments and
// deletion policies, streaming batch persistence.

public sealed class PreviewAndFetchTests : IClassFixture<RocksDbStoreFixture>
{
    private readonly RocksDbStoreFixture _fixture;

    public PreviewAndFetchTests(RocksDbStoreFixture fixture) => _fixture = fixture;

    [Fact]
    public void Preview_returns_bounded_metadata_not_full_payloads()
    {
        var (store, _) = _fixture.NewStore();
        var large = Repeat("large-block-", 512); // ~6 KiB, larger than preview
        using (store)
        {
            store.AppendMessage("s", new MessageRecord("assistant", new MessagePart[]
            {
                new TextPart("small text"),
                new ToolCallPart("call-1", "probe", "{\"depth\":1}"),
                new ToolResultPart("call-1", large),
            }));
            var preview = store.OpenRecentPreview("s", 10, 8);

            Assert.Equal(0, preview.FromOrdinal);
            var item = preview.Items.Single();
            Assert.Equal(0, item.Ordinal);
            Assert.Equal("assistant", item.Role);
            Assert.Equal(3, item.Parts.Count);

            var text = item.Parts[0];
            Assert.Equal("text", text.Kind);
            Assert.Equal(10L, text.ContentChars);
            Assert.Equal("small te", text.Preview);
            Assert.Null(text.ToolCallId);

            var call = item.Parts[1];
            Assert.Equal("toolCall", call.Kind);
            Assert.Equal(11L, call.ContentChars);
            Assert.Equal("call-1", call.ToolCallId);
            Assert.Equal("{\"depth\"", call.Preview);

            var result = item.Parts[2];
            Assert.Equal("toolResult", result.Kind);
            Assert.Equal((long)large.Length, result.ContentChars);
            Assert.Equal("call-1", result.ToolCallId);
            Assert.True(result.Preview.Length == 8, "preview must not carry full payload text");
            Assert.Equal(large[..8], result.Preview);
        }
    }

    [Fact]
    public void FetchToolResult_returns_exact_full_content_of_targeted_part()
    {
        var (store, storeRoot) = _fixture.NewStore();
        var large = Repeat("x-§-每", 100); // multi-byte content
        using (store)
        {
            store.AppendMessage("s", new MessageRecord("assistant", new MessagePart[]
            {
                new ToolCallPart("call-a", "scan", "{}"),
                new ToolResultPart("call-a", large),
                new ToolResultPart("call-a-later", "short"), // second result, different id
            }));
            store.Dispose();

            using var reopened = _fixture.Reopen(storeRoot);
            Assert.Equal(large, reopened.FetchToolResult("s", 0, 1));
            Assert.Equal("short", reopened.FetchToolResult("s", 0, 2));

            var thrown = Assert.Throws<InvalidOperationException>(() => reopened.FetchToolResult("s", 0, 0));
            Assert.Contains("not a tool result", thrown.Message);
            Assert.Throws<InvalidOperationException>(() => reopened.FetchToolResult("s", 0, 99));
            Assert.Throws<InvalidOperationException>(() => reopened.FetchToolResult("s", 5, 0));
        }
    }

    private static string Repeat(string unit, int times) => string.Concat(Enumerable.Repeat(unit, times));
}

public sealed class AssignmentTests : IClassFixture<RocksDbStoreFixture>
{
    private readonly RocksDbStoreFixture _fixture;

    public AssignmentTests(RocksDbStoreFixture fixture) => _fixture = fixture;

    [Fact]
    public void Assignment_lifecycle_and_delete_policies()
    {
        var (store, storeRoot) = _fixture.NewStore();
        using (store)
        {
            store.AppendMessage("s", TestMessages.Text("user", "m0"));
            store.CreateAssignment(new AssignmentRecord("asg-1", "s", "Inspect", AssignmentStatus.Open, null));
            Assert.Throws<InvalidOperationException>(() =>
                store.CreateAssignment(new AssignmentRecord("asg-1", "s", "again", AssignmentStatus.Open, null)));

            store.SettleAssignment(new SettleAssignmentRequest("asg-1", "done"));
            var settled = store.GetAssignment("asg-1");
            Assert.Equal(AssignmentStatus.Settled, settled.Status);
            Assert.Equal("done", settled.ResultText);

            // RequireSettled: open assignment blocks deletion, session intact.
            store.CreateAssignment(new AssignmentRecord("asg-2", "s", "open", AssignmentStatus.Open, null));
            Assert.Throws<InvalidOperationException>(() => store.DeleteSession("s", DeletePolicy.RequireSettled));
            Assert.Equal(1, store.ListSessionsMeta().Single(m => m.SessionId == "s").MessageCount);
            Assert.Equal(AssignmentStatus.Open, store.GetAssignment("asg-2").Status);

            // CancelOpen: deletion cancels outstanding assignments in the same
            // logical operation; dangling open references never remain.
            store.DeleteSession("s", DeletePolicy.CancelOpen);
            Assert.Empty(store.ListSessionsMeta());
            var cancelled = store.GetAssignment("asg-2");
            Assert.Equal(AssignmentStatus.Cancelled, cancelled.Status);
            Assert.Equal("s", cancelled.TargetSessionId);

            Assert.Throws<InvalidOperationException>(() => store.DeleteSession("s", DeletePolicy.RequireSettled));
            Assert.Throws<InvalidOperationException>(() => store.DeleteSession("missing", DeletePolicy.CancelOpen));
            Assert.Throws<InvalidOperationException>(() => store.SettleAssignment(new SettleAssignmentRequest("missing", "x")));
            Assert.Throws<InvalidOperationException>(() => store.GetAssignment("missing"));

            store.Dispose();

            // Cancelled assignments persist across reopen without dangling open state.
            using var reopened = _fixture.Reopen(storeRoot);
            Assert.Equal(AssignmentStatus.Cancelled, reopened.GetAssignment("asg-2").Status);
            Assert.Equal(AssignmentStatus.Settled, reopened.GetAssignment("asg-1").Status);
            Assert.Empty(reopened.ListSessionsMeta());
        }
    }
}

public sealed class StreamingTests : IClassFixture<RocksDbStoreFixture>
{
    private readonly RocksDbStoreFixture _fixture;

    public StreamingTests(RocksDbStoreFixture fixture) => _fixture = fixture;

    [Fact]
    public void Every_stream_draft_batch_is_persisted_stream_restarts()
    {
        var (store, storeRoot) = _fixture.NewStore();
        using (store)
        {
            Assert.Equal(0, store.AppendMessage("s", TestMessages.Text("user", "prompt")));
            store.StreamDraft("s", "stream-1", "first batch ");
            store.StreamDraft("s", "stream-1", "second batch ");
            store.Dispose();

            // Reopen without finalize: the draft must still exist (batches were
            // written per call, not buffered in host memory until finalize).
            using var reopened = _fixture.Reopen(storeRoot);
            var ordinal = reopened.FinalizeStream("s", "stream-1", TestMessages.Text("assistant", "final"));
            Assert.Equal(1, ordinal); // tail position after the prompt
            Assert.Equal(2, reopened.ListSessionsMeta().Single(m => m.SessionId == "s").MessageCount);
        }
    }

    [Fact]
    public void StreamIdentity_rules_match_reference()
    {
        var (store, _) = _fixture.NewStore();
        using (store)
        {
            store.StreamDraft("s", "stream-1", "a");
            Assert.Throws<InvalidOperationException>(() => store.StreamDraft("s", "stream-2", "b"));
            Assert.Throws<InvalidOperationException>(() => store.FinalizeStream("s", "stream-2", TestMessages.Text("assistant", "x")));
            Assert.Throws<InvalidOperationException>(() => store.FinalizeStream("s", "never-opened", TestMessages.Text("assistant", "x")));

            // After finalize, a new stream is accepted.
            store.FinalizeStream("s", "stream-1", TestMessages.Text("assistant", "final"));
            store.StreamDraft("s", "stream-2", "fresh stream");
            store.FinalizeStream("s", "stream-2", TestMessages.Text("assistant", "final2"));
            // Streams on a different session are independent (drafts do not
            // create session metadata: only finalized messages do).
            store.StreamDraft("other", "stream-1", "independent session");
            Assert.Equal(2, store.ListSessionsMeta().Single(m => m.SessionId == "s").MessageCount);
        }
    }
}

public sealed class ReopenPersistenceTests : IClassFixture<RocksDbStoreFixture>
{
    private readonly RocksDbStoreFixture _fixture;

    public ReopenPersistenceTests(RocksDbStoreFixture fixture) => _fixture = fixture;

    [Fact]
    public void Full_catalog_state_survives_reopen()
    {
        var (store, storeRoot) = _fixture.NewStore();
        using (store)
        {
            store.AppendMessage("keep", new MessageRecord("assistant", new MessagePart[]
            {
                new TextPart("answer"),
                new ToolResultPart("call-k", "result content"),
            }));
            store.AppendMessage("keep", TestMessages.Text("user", "follow-up"));
            store.CreateAssignment(new AssignmentRecord("asg-a", "keep", "title", AssignmentStatus.Open, null));
            store.CreateAssignment(new AssignmentRecord("asg-b", "gone", "title", AssignmentStatus.Open, null));
            store.SettleAssignment(new SettleAssignmentRequest("asg-b", "settled"));
            store.StreamDraft("gone", "stream-g", "partial");
            store.FinalizeStream("gone", "stream-g", TestMessages.Text("assistant", "done"));
            store.AppendMessage("gone", TestMessages.Text("user", "also"));
            store.AppendMessage("drop", TestMessages.Text("user", "bye"));
            store.DeleteSession("drop", DeletePolicy.RequireSettled);
            store.Dispose();

            using var reopened = _fixture.Reopen(storeRoot);
            var metas = reopened.ListSessionsMeta();
            Assert.Equal(
                [new SessionMeta("gone", 2), new SessionMeta("keep", 2)],
                metas.OrderBy(m => m.SessionId, StringComparer.Ordinal).ToArray());
            Assert.Equal("follow-up", TestMessages.ContentOf(reopened.OpenRecent("keep", 1).Single()));
            Assert.Equal("result content", reopened.FetchToolResult("keep", 0, 1));
            Assert.Equal(AssignmentStatus.Open, reopened.GetAssignment("asg-a").Status);
            Assert.Equal(AssignmentStatus.Settled, reopened.GetAssignment("asg-b").Status);
        }
    }

    [Fact]
    public void Describe_and_statistics_are_wired()
    {
        var (store, _) = _fixture.NewStore();
        using (store)
        {
            var descriptor = store.Describe();
            Assert.Equal("RocksDbIndexedRecords", descriptor.StoreName);
            Assert.Equal(AckTier.OsFlush, descriptor.AckTier);
            Assert.Contains("WAL", descriptor.SettingsNote);

            var before = store.Sample().ManagedAllocatedBytes;
            store.AppendMessage("s", TestMessages.Text("user", "m"));
            var after = store.Sample().ManagedAllocatedBytes;
            Assert.True(after >= before);
        }
    }
}