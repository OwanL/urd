using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Urd.TranscriptHarness.Adapter.SqliteRows;
using Urd.TranscriptHarness.Contracts;
using Urd.TranscriptHarness.Core;
using Urd.TranscriptHarness.ReferenceStore;

namespace Urd.TranscriptHarness.SqliteRows.Tests;

public static partial class TestRunner
{
    private static void PreviewWindowSnapshot()
    {
        foreach (var append in new[] { true, false })
        {
            var dir = TempDir($"preview-snapshot-{append}");
            using var reader = Open(dir);
            using var writer = Open(dir);
            writer.AppendMessage("s", MessageRecord.Single("user", new TextPart("first")));
            writer.AppendMessage("s", MessageRecord.Single("assistant", new TextPart("last")));
            var committed = false;
            OnRead(reader, "messages", () =>
            {
                if (append) writer.AppendMessage("s", MessageRecord.Single("user", new TextPart("new")));
                else writer.ReplaceSuffix("s", 1, []);
                committed = true;
            });
            var preview = reader.OpenRecentPreview("s", 1, 24);
            Assert(committed, "writer must commit during the read, not before/after it");
            Assert(preview.FromOrdinal == 1 && preview.Items.Count == 1 && preview.Items[0].Ordinal == 1,
                "cursor and latest window must share the pre-commit snapshot");
            Assert(preview.Items[0].Parts[0].Preview == "last", "snapshot content");
        }
    }

    private static void PreviewFallbackSnapshot()
    {
        foreach (var delete in new[] { false, true })
        {
            var dir = TempDir($"preview-fallback-snapshot-{delete}");
            using var reader = Open(dir);
            using var writer = Open(dir);
            var old = new string('A', 512);
            writer.AppendMessage("s", new MessageRecord("assistant", [new TextPart(old), new ToolResultPart("c", old)]));
            var committed = false;
            OnRead(reader, "parts_meta", () =>
            {
                if (delete) writer.DeleteSession("s", DeletePolicy.RequireSettled);
                else writer.ReplaceSuffix("s", 0, [MessageRecord.Single("user", new TextPart(new string('B', 600)))]);
                committed = true;
            });
            // One message: fallback happens after the metadata reader reaches DONE,
            // when an implicit statement snapshot alone no longer protects it.
            var preview = reader.OpenRecentPreview("s", 1, 300);
            Assert(committed, "writer must commit while metadata is being read");
            Assert(preview.Items.Count == 1 && preview.Items[0].Role == "assistant", "original header");
            Assert(preview.Items[0].Parts.Count == 2 && preview.Items[0].Parts.All(p =>
                p.ContentChars == 512 && p.Preview == new string('A', 300)), "fallback must use metadata snapshot");
        }
    }

    private static void PreviewZeroParts()
    {
        var dir = TempDir("preview-zero-parts");
        using var store = Open(dir);
        MessageRecord[] messages = [new("user", []), MessageRecord.Single("assistant", new TextPart("middle")), new("assistant", [])];
        foreach (var message in messages) store.AppendMessage("s", message);
        foreach (var count in new[] { 1, 2, 3, 10 })
        {
            var preview = store.OpenRecentPreview("s", count, 24);
            var from = Math.Max(0, messages.Length - count);
            Assert(preview.FromOrdinal == from && preview.Items.Count == messages.Length - from, "zero-part window shape");
            for (var i = 0; i < preview.Items.Count; i++)
            {
                var item = preview.Items[i];
                Assert(item.Ordinal == from + i && item.Role == messages[from + i].Role &&
                    item.Parts.Count == messages[from + i].Parts.Count, "zero-part header and ordering");
            }
        }
        Assert(store.OpenRecentPreview("s", 0, 24).Items.Count == 0, "zero requested messages");
        store.ReplaceSuffix("s", 0, []);
        var empty = store.OpenRecentPreview("s", 10, 24);
        Assert(empty.Items.Count == 0 && empty.FromOrdinal == 0, "empty session preview");
    }

    private static void PreviewUnicode()
    {
        var a254 = new string('a', 254);
        var a255 = new string('a', 255);
        var a299 = new string('a', 299);
        // Expected strings are explicit, independent of the production prefix helper.
        (string Content, int Cap, string Expected)[] cases =
        [
            (a255 + "😀z", 256, a255),
            ("", 24, ""), ("😀z", -1, ""), ("😀z", 0, ""), ("😀z", 1, ""),
            ("😀z", 2, "😀"), ("😀z", 3, "😀z"), ("e\u0301z", 1, "e"),
            (a254 + "😀z", 255, a254), (a254 + "😀z", 256, a254 + "😀"),
            (a255 + "😀z", 257, a255 + "😀"),
            (a255 + "😀z", 1000, a255 + "😀z"),
            (a299 + "😀z", 300, a299), (a299 + "😀z", 301, a299 + "😀"),
        ];
        var dir = TempDir("preview-unicode");
        using (var store = Open(dir)) Check(store, append: true);
        using (var reopened = Open(dir)) Check(reopened, append: false);
        using var reference = new ReferenceTranscriptStore();
        Check(reference, append: true);

        void Check(ITranscriptStore store, bool append)
        {
            for (var i = 0; i < cases.Length; i++)
            {
                var (content, cap, expected) = cases[i];
                var session = $"s-{i}";
                var message = new MessageRecord("assistant", [new TextPart(content), new ToolCallPart("c", "tool", content),
                    new ToolResultPart("c", content), new ImagePart("image", content)]);
                if (append) store.AppendMessage(session, message);
                var preview = store.OpenRecentPreview(session, 1, cap);
                Assert(preview.Items[0].Parts.All(p => p.Preview == expected && p.ContentChars == content.Length),
                    $"{store.Describe().StoreName} Unicode case {i}: exact maximal prefix and UTF-16 length");
                CompareContents(store.OpenRecent(session, 1), [message], "Unicode full content unchanged");
                // Exercise the actual shared transport codec and oracle, not a test-only serializer.
                using var buffer = new MemoryStream();
                using (var json = new Utf8JsonWriter(buffer)) HarnessJson.WriteWindowPreview(json, preview);
                using var document = JsonDocument.Parse(buffer.ToArray());
                var decoded = HarnessJson.ReadWindowPreview(document.RootElement);
                Assert(decoded.Items[0].Parts.All(p => p.Preview == expected), "wire must preserve the exact preview");
                Oracle.ComparePreviewWindow(decoded, [new(message.Role, message.Parts)], 0, cap, session);
            }
        }
    }

    private static void PreviewMetadataOnly()
    {
        var dir = TempDir("preview-metadata-only");
        using var store = Open(dir);
        var prefix = new string('a', 255);
        store.AppendMessage("s", MessageRecord.Single("assistant", new ToolResultPart("c", prefix + "😀" + new string('z', 64 * 1024))));
        var connection = ConnectionOf(store);
        var payloadReads = 0;
        connection.CreateFunction<string, string>("observe_payload", json => { payloadReads++; return json; });
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TEMP VIEW messages AS SELECT session_id, ordinal, role, observe_payload(parts_json) AS parts_json FROM main.messages";
        command.ExecuteNonQuery();
        Assert(store.OpenRecentPreview("s", 1, 256).Items[0].Parts[0].Preview == prefix, "stored cap backs off a split pair");
        Assert(payloadReads == 0, "cap <=256 must not fetch the payload even when stored prefix is 255");
        Assert(store.OpenRecentPreview("s", 1, 257).Items[0].Parts[0].Preview == prefix + "😀", "fallback maximal prefix");
        Assert(payloadReads == 1, "fallback decodes just its one message envelope");
    }

    private static SqliteConnection ConnectionOf(SqliteRowsTranscriptStore store) =>
        (SqliteConnection)typeof(SqliteRowsTranscriptStore).GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;

    private static void OnRead(SqliteRowsTranscriptStore store, string table, Action mutation)
    {
        // Reader-local TEMP view: inject a real commit on an independent WAL
        // connection once the statement has begun reading. No sleeps, threads,
        // schema changes in the persisted DB, or production interception hooks.
        var connection = ConnectionOf(store);
        var fired = false;
        connection.CreateFunction("mutate_once", () =>
        {
            if (!fired) { fired = true; mutation(); }
            return 1;
        });
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TEMP VIEW {table} AS SELECT * FROM main.{table} WHERE mutate_once()";
        command.ExecuteNonQuery();
    }
}
