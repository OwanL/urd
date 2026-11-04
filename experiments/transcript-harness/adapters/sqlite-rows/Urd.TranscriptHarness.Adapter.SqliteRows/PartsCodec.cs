using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.SqliteRows;

// Message-content codec for the SqliteRows (family 1) baseline: each message's
// parts are serialized as a compact JSON array stored in the message row's
// parts_json column. JSON text is the ONLY variable-content representation in
// this family — raw text + JSON, no binary encoding, no separate raw-content
// columns. The full part content (including large tool-result text) lives
// inside this row envelope; kind discriminators keep the envelope self-describing:
//   {"k":"text","c":...}
//   {"k":"call","id":...,"n":...,"args":...}
//   {"k":"result","id":...,"c":...}
//   {"k":"image","id":...,"path":...}
// Queryable preview metadata is DERIVED from this envelope at write time into
// parts_meta (see the store); the envelope itself is the authoritative content.

public static class PartsCodec
{
    private const string KindText = "text";
    private const string KindCall = "toolCall";
    private const string KindResult = "toolResult";
    private const string KindImage = "image";

    // Own storage format (never re-parsed across trust boundaries), so an
    // unescaped/relaxed encoder keeps CJK text readable and compact.
    private static readonly JsonSerializerOptions _relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(IReadOnlyList<MessagePart> parts)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = _relaxed.Encoder }))
        {
            WriteTo(writer, parts);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static void WriteTo(Utf8JsonWriter w, IReadOnlyList<MessagePart> parts)
    {
        w.WriteStartArray();
        foreach (var part in parts)
        {
            w.WriteStartObject();
            switch (part)
            {
                case TextPart text:
                    w.WriteString("k", KindText);
                    w.WriteString("c", text.Content);
                    break;
                case ToolCallPart call:
                    w.WriteString("k", KindCall);
                    w.WriteString("id", call.ToolCallId);
                    w.WriteString("n", call.Name);
                    w.WriteString("args", call.ArgsJson);
                    break;
                case ToolResultPart result:
                    w.WriteString("k", KindResult);
                    w.WriteString("id", result.ToolCallId);
                    w.WriteString("c", result.Content);
                    break;
                case ImagePart image:
                    w.WriteString("k", KindImage);
                    w.WriteString("id", image.ImageId);
                    w.WriteString("path", image.RelativePath);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported part type {part.GetType().Name}");
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    public static List<MessagePart> FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return FromJson(document.RootElement);
    }

    private static List<MessagePart> FromJson(JsonElement array)
    {
        var parts = new List<MessagePart>();
        foreach (var entry in array.EnumerateArray())
        {
            var kind = entry.GetProperty("k").GetString()
                ?? throw new InvalidOperationException("Corrupt parts envelope: missing kind");
            parts.Add(kind switch
            {
                KindText => new TextPart(entry.GetProperty("c").GetString() ?? string.Empty),
                KindCall => new ToolCallPart(
                    entry.GetProperty("id").GetString() ?? string.Empty,
                    entry.GetProperty("n").GetString() ?? string.Empty,
                    entry.GetProperty("args").GetString() ?? string.Empty),
                KindResult => new ToolResultPart(
                    entry.GetProperty("id").GetString() ?? string.Empty,
                    entry.GetProperty("c").GetString() ?? string.Empty),
                KindImage => new ImagePart(
                    entry.GetProperty("id").GetString() ?? string.Empty,
                    entry.GetProperty("path").GetString() ?? string.Empty),
                _ => throw new InvalidOperationException($"Corrupt parts envelope: unknown kind '{kind}'"),
            });
        }
        return parts;
    }

    /// <summary>The content string a part's preview metadata is derived from.</summary>
    public static string ContentOf(MessagePart part) => part switch
    {
        TextPart t => t.Content,
        ToolCallPart c => c.ArgsJson,
        ToolResultPart r => r.Content,
        // Images: content here means the stable relative path (oracle-conform preview mapping).
        ImagePart i => i.RelativePath,
        _ => string.Empty,
    };

    public static (string Kind, string? ToolCallId) MetaOf(MessagePart part) => part switch
    {
        TextPart => (KindText, null),
        ToolCallPart c => (KindCall, c.ToolCallId),
        ToolResultPart r => (KindResult, r.ToolCallId),
        ImagePart => (KindImage, null),
        _ => ("unknown", null),
    };

    /// <summary>Kind string stored in parts_meta (text/call/result/image).</summary>
    public static string KindOf(string kind) => kind;
}