using System.Text;
using Xunit;
using Urd.TranscriptHarness.Adapter.RocksDbStore;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.RocksDb.Tests;

// Empty-window semantics (absence is not an error) and unicode behaviour.

public sealed class EmptyWindowTests : IClassFixture<RocksDbStoreFixture>
{
    private readonly RocksDbStoreFixture _fixture;

    public EmptyWindowTests(RocksDbStoreFixture fixture) => _fixture = fixture;

    [Fact]
    public void Reads_on_unknown_and_empty_sessions_return_empty_windows()
    {
        var (store, _) = _fixture.NewStore();
        using (store)
        {
            Assert.Empty(store.OpenRecent("nope", 5));
            Assert.Empty(store.PageOlder("nope", 10, 5));
            Assert.Empty(store.ReadRange("nope", 0, 5));
            var preview = store.OpenRecentPreview("nope", 5, 10);
            Assert.Equal("nope", preview.SessionId);
            Assert.Equal(0, preview.FromOrdinal);
            Assert.Empty(preview.Items);

            // An existing-but-empty session (created by truncating itself) reads
            // empty everywhere too.
            store.AppendMessage("empty", TestMessages.Text("user", "x"));
            store.ReplaceSuffix("empty", 0, []);
            Assert.Empty(store.OpenRecent("empty", 5));
            Assert.Empty(store.ReadRange("empty", 0, 0));
            Assert.Equal(0, store.ListSessionsMeta().Single(m => m.SessionId == "empty").MessageCount);
        }
    }

    [Fact]
    public void OutOfBounds_ranges_on_known_sessions_throw_like_reference()
    {
        var (store, storeRoot) = _fixture.NewStore();
        using (store)
        {
            store.AppendMessage("s", TestMessages.Text("user", "m0"));
            Assert.Throws<InvalidOperationException>(() => store.ReadRange("s", -1, 1));
            Assert.Throws<InvalidOperationException>(() => store.ReadRange("s", 0, 2));
            Assert.Throws<InvalidOperationException>(() => store.ReadRange("s", 5, 1));
            // Deleted session is absent again afterwards.
            store.DeleteSession("s", DeletePolicy.RequireSettled);
            Assert.Empty(store.ReadRange("s", 0, 1));
        }
    }
}

public sealed class UnicodeTests : IClassFixture<RocksDbStoreFixture>
{
    private readonly RocksDbStoreFixture _fixture;

    public UnicodeTests(RocksDbStoreFixture fixture) => _fixture = fixture;

    [Fact]
    public void MultiByteUnicode_roundtrips_and_previews_exactly()
    {
        var (store, storeRoot) = _fixture.NewStore();
        var contents = new[]
        {
            "plain ascii",
            "ä ö ü — em dash\nnew line",
            "每日 世界 CJK text",
            "emoji: 😀🚀 astral plane pairs",
            "", // empty content
            "mixed 每日a😀b\u0301combining",
        };
        using (store)
        {
            foreach (var content in contents)
            {
                store.AppendMessage("u", TestMessages.Text("user", content));
            }
            store.Dispose();

            using var reopened = _fixture.Reopen(storeRoot);
            var all = reopened.ReadRange("u", 0, contents.Length);
            for (var i = 0; i < contents.Length; i++)
            {
                Assert.Equal(contents[i], TestMessages.ContentOf(all[i]));
            }

            // Preview at every char boundary: metadata length + exact prefix.
            for (var previewChars = 0; previewChars <= 12; previewChars++)
            {
                var preview = reopened.OpenRecentPreview("u", contents.Length, previewChars);
                Assert.Equal(0, preview.FromOrdinal);
                for (var i = 0; i < contents.Length; i++)
                {
                    var part = preview.Items[i].Parts.Single();
                    Assert.Equal("text", part.Kind);
                    Assert.Equal((long)contents[i].Length, part.ContentChars);
                    Assert.Null(part.ToolCallId);
                    var expected = contents[i].Length <= previewChars
                        ? contents[i]
                        : contents[i][..previewChars];
                    Assert.Equal(expected, part.Preview);
                }
            }

            // Boundary-inside-pair case: p=8 cuts the emoji pair (lone high
            // surrogate, exactly like content[..8]); p=9 ends on the pair.
            var cut8 = reopened.OpenRecentPreview("u", contents.Length, 8);
            Assert.Equal(contents[3][..8], cut8.Items[3].Parts.Single().Preview);
            var cut9 = reopened.OpenRecentPreview("u", contents.Length, 9);
            Assert.Equal(contents[3][..9], cut9.Items[3].Parts.Single().Preview);
        }
    }
}