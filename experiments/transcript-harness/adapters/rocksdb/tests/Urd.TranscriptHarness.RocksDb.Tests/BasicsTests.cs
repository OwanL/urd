using Xunit;
using Urd.TranscriptHarness.Adapter.RocksDbStore;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.RocksDb.Tests;

// Focused correctness tests for the family-5 RocksDB adapter, run in-process
// against a fresh store root per test (OS temp, deleted on fixture disposal).
// Coverage follows the assignment's scope: ordered composite ranges, suffix
// replacement without stale keys, empty windows, unicode content, bounded
// preview metadata, targeted tool-result fetch, assignments and deletion
// policies, streaming batch persistence, reopen persistence. Correctness
// only — no performance measurements.

public sealed class RocksDbStoreFixture : IDisposable
{
    public string TempRoot { get; } = Path.Combine(
        Path.GetTempPath(), "urd-transcript-harness", "rocksdb-tests",
        $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..8]}");

    public RocksDbStoreFixture() => Directory.CreateDirectory(TempRoot);

    /// <summary>A fresh store over a fresh store root (its own instance).</summary>
    public (RocksDbTranscriptStore Store, string StoreRoot) NewStore()
    {
        var storeRoot = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        var store = new RocksDbTranscriptStore();
        store.Open(storeRoot, Path.Combine(TempRoot, "attachments"));
        return (store, storeRoot);
    }

    /// <summary>A fresh store instance over an EXISTING store root (reopen).</summary>
    public RocksDbTranscriptStore Reopen(string storeRoot)
    {
        var store = new RocksDbTranscriptStore();
        store.Open(storeRoot, Path.Combine(TempRoot, "attachments"));
        return store;
    }

    public void Dispose()
    {
        try { Directory.Delete(TempRoot, recursive: true); }
        catch (IOException) { /* Windows file-lock races at teardown only */ }
        catch (UnauthorizedAccessException) { }
    }
}

public static class TestMessages
{
    public static MessageRecord Text(string role, string content) => new(role, [new TextPart(content)]);

    public static string ContentOf(MessageRecord message) =>
        ((TextPart)message.Parts.Single()).Content;
}

public sealed class OrderedRangeTests : IClassFixture<RocksDbStoreFixture>
{
    private readonly RocksDbStoreFixture _fixture;

    public OrderedRangeTests(RocksDbStoreFixture fixture) => _fixture = fixture;

    [Fact]
    public void ReadRange_openRecent_pageOlder_matchContractWindows()
    {
        var (store, _) = _fixture.NewStore();
        using (store)
        {
            for (var i = 0; i < 10; i++) store.AppendMessage("a", TestMessages.Text("user", $"a{i}"));
            for (var i = 0; i < 15; i++) store.AppendMessage("b", TestMessages.Text("assistant", $"b{i}"));
            for (var i = 0; i < 3; i++) store.AppendMessage("a", TestMessages.Text("assistant", $"a{i + 10}"));

            // Interleaved appends leave dense per-session ordinals; ranges are bounded.
            Assert.Equal(13, store.ReadRange("a", 0, 13).Length);
            Assert.Equal(6, store.ReadRange("a", 4, 10).Length);
            Assert.Equal("a4", TestMessages.ContentOf(store.ReadRange("a", 4, 10)[0]));

            var recent = store.OpenRecent("a", 5);
            Assert.Equal(5, recent.Length);
            Assert.Equal("a8", TestMessages.ContentOf(recent[0])); // newest window, oldest first
            Assert.Equal("a12", TestMessages.ContentOf(recent[4]));

            // count beyond the session returns the whole session.
            Assert.Equal(13, store.OpenRecent("a", 100).Length);
            Assert.Equal("a0", TestMessages.ContentOf(store.OpenRecent("a", 100)[0]));

            var older = store.PageOlder("a", 8, 5);
            Assert.Equal(5, older.Length);
            Assert.Equal("a3", TestMessages.ContentOf(older[0]));
            // before == 0 and before beyond count clamp like the reference store.
            Assert.Empty(store.PageOlder("a", 0, 5));
            Assert.Equal(13, store.PageOlder("a", 999, 20).Length);
            Assert.Equal("a0", TestMessages.ContentOf(store.PageOlder("a", 999, 20)[0]));

            // Sessions never bleed into each other's windows.
            Assert.Equal(15, store.ReadRange("b", 0, 15).Length);
        }
    }

    [Fact]
    public void Sessions_index_lists_counts_and_orders()
    {
        var (store, _) = _fixture.NewStore();
        using (store)
        {
            store.AppendMessage("s-2", TestMessages.Text("user", "one"));
            store.AppendMessage("s-1", TestMessages.Text("user", "x"));
            store.AppendMessage("s-1", TestMessages.Text("user", "y"));
            var metas = store.ListSessionsMeta();
            Assert.Equal([new SessionMeta("s-1", 2), new SessionMeta("s-2", 1)], metas);
        }
    }
}

public sealed class SuffixReplacementTests : IClassFixture<RocksDbStoreFixture>
{
    private readonly RocksDbStoreFixture _fixture;

    public SuffixReplacementTests(RocksDbStoreFixture fixture) => _fixture = fixture;

    [Fact]
    public void ReplaceSuffix_deletes_old_suffix_noStaleKeys_afterReopen()
    {
        var (store, storeRoot) = _fixture.NewStore();
        for (var i = 0; i < 10; i++) store.AppendMessage("s", TestMessages.Text("user", $"old{i}"));

        var replacement = new[]
        {
            TestMessages.Text("user", "new0"),
            TestMessages.Text("assistant", "new1"),
            TestMessages.Text("user", "new2"),
        };
        Assert.Equal(7, store.ReplaceSuffix("s", 4, replacement));
        Assert.Equal(7, store.ListSessionsMeta().Single(m => m.SessionId == "s").MessageCount);
        Assert.Equal("new2", TestMessages.ContentOf(store.OpenRecent("s", 1)[0])); // dense renumbering
        store.Dispose();

        using var reopened = _fixture.Reopen(storeRoot);
        var all = reopened.ReadRange("s", 0, 7);
        Assert.Equal(7, all.Length);
        Assert.Equal("old0old1old2old3new0new1new2",
            string.Join(string.Empty, all.Select(TestMessages.ContentOf)));
        // No stale key resurrection: the whole tail beyond the new count is empty.
        Assert.Equal(7, reopened.OpenRecent("s", 50).Length);
        Assert.Equal(7, reopened.ListSessionsMeta().Single(m => m.SessionId == "s").MessageCount);
    }

    [Fact]
    public void ReplaceSuffix_shorter_replacement_truncates()
    {
        var (store, _) = _fixture.NewStore();
        using (store)
        {
            for (var i = 0; i < 6; i++) store.AppendMessage("s", TestMessages.Text("user", $"m{i}"));
            Assert.Equal(2, store.ReplaceSuffix("s", 2, []));
            Assert.Empty(store.ReadRange("s", 2, 2));
            Assert.Equal(2, store.ListSessionsMeta().Single(m => m.SessionId == "s").MessageCount);

            // Append after truncation lands on the compacted tail.
            var ordinal = store.AppendMessage("s", TestMessages.Text("assistant", "tail"));
            Assert.Equal(2, ordinal);
            Assert.Equal("tail", TestMessages.ContentOf(store.OpenRecent("s", 1)[0]));
        }
    }

    [Fact]
    public void ReplaceSuffix_bounds_and_unknown_session()
    {
        var (store, _) = _fixture.NewStore();
        using (store)
        {
            store.AppendMessage("s", TestMessages.Text("user", "m0"));
            Assert.Throws<InvalidOperationException>(() => store.ReplaceSuffix("s", -1, []));
            Assert.Throws<InvalidOperationException>(() => store.ReplaceSuffix("s", 2, []));
            Assert.Throws<InvalidOperationException>(() => store.ReplaceSuffix("unknown", 0, []));
            // pure-append replacement from the tail: count goes 1 -> 2
            Assert.Equal(2, store.ReplaceSuffix("s", 1, [TestMessages.Text("user", "m1")]));
        }
    }
}