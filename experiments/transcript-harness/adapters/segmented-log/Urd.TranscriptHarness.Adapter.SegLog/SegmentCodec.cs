using System.Buffers;
using System.Text.Json;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.SegLog;

// Message record encoding for the segmented binary log: the same JSON-ish
// body the JSONL family uses (every string value escaped newline-free), but
// framed as a LENGTH-PREFIXED BINARY record instead of a line:
//
//   [int32 little-endian payload length][payload bytes]
//
// The length prefix is the only framing metadata; no CRC, no header record,
// no tail sentinel — the SQLite index (segment id, offset, length) is the
// authority for where a record lives, and recovery never needs to parse
// records. Same write-time preview metadata rules as the JSONL family:
// full content character length (UTF-16 code units — the unit the oracle
// compares with string.Length and string[..n] slicing) plus a bounded
// preview string, so preview reads decode only short persisted strings.

/// <summary>Framing constants for the segmented binary log record codec.</summary>
public static class SegmentFraming
{
    /// <summary>Size of the length prefix in bytes (int32 little-endian).</summary>
    public const int LengthPrefixBytes = 4;

    public static void WritePrefix(Span<byte> destination, int payloadLength)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination, payloadLength);
    }

    public static byte[] EncodeRecord(ReadOnlySpan<byte> payload)
    {
        var record = new byte[LengthPrefixBytes + payload.Length];
        WritePrefix(record, payload.Length);
        payload.CopyTo(record.AsSpan(LengthPrefixBytes));
        return record;
    }
}

public static class SegmentJson
{
    /// <summary>Maximum preview text persisted in the index; larger requests fall back to bounded row reads.</summary>
    public const int PreviewCap = 64;

    public static byte[] EncodeMessage(MessageRecord message)
    {
        var line = new ArrayBufferWriter<byte>(256);
        using var json = new Utf8JsonWriter(line);
        json.WriteStartObject();
        json.WriteString("r", message.Role);
        json.WriteStartArray("p");
        foreach (var part in message.Parts)
        {
            json.WriteStartObject();
            switch (part)
            {
                case TextPart text:
                    json.WriteString("k", "text");
                    json.WriteString("c", text.Content);
                    break;
                case ToolCallPart call:
                    json.WriteString("k", "toolCall");
                    json.WriteString("id", call.ToolCallId);
                    json.WriteString("n", call.Name);
                    json.WriteString("a", call.ArgsJson);
                    break;
                case ToolResultPart result:
                    json.WriteString("k", "toolResult");
                    json.WriteString("id", result.ToolCallId);
                    json.WriteString("c", result.Content);
                    break;
                case ImagePart image:
                    json.WriteString("k", "image");
                    json.WriteString("id", image.ImageId);
                    json.WriteString("rp", image.RelativePath);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown part type {part.GetType().Name}");
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
        return line.WrittenSpan.ToArray(); // payload only; SegmentFraming adds the length prefix
    }

    public static MessageRecord DecodeMessage(ReadOnlySpan<byte> line) =>
        ReadMessage(line, out _);

    private static MessageRecord ReadMessage(ReadOnlySpan<byte> line, out string role)
    {
        role = string.Empty;
        var reader = new Utf8JsonReader(line);
        if (!reader.Read()) throw Corrupt(reader);
        Expect(reader, JsonTokenType.StartObject);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw Corrupt(reader);
            }
            if (reader.ValueTextEquals("r"))
            {
                reader.Read();
                role = ExpectString(reader);
            }
            else if (reader.ValueTextEquals("p"))
            {
                if (role.Length == 0) throw Corrupt(reader);
                reader.Read();
                return new MessageRecord(role, ReadParts(ref reader));
            }
            else
            {
                throw Corrupt(reader);
            }
        }
        throw Corrupt(reader);
    }

    private static IReadOnlyList<MessagePart> ReadParts(ref Utf8JsonReader reader)
    {
        Expect(reader, JsonTokenType.StartArray);
        var parts = new List<MessagePart>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return parts;
            Expect(reader, JsonTokenType.StartObject);
            string? kind = null, id = null, name = null, content = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                Expect(reader, JsonTokenType.PropertyName);
                if (reader.ValueTextEquals("k")) kind = ReadNextString(ref reader);
                else if (reader.ValueTextEquals("c") || reader.ValueTextEquals("a") || reader.ValueTextEquals("rp"))
                    content = ReadNextString(ref reader);
                else if (reader.ValueTextEquals("id")) id = ReadNextString(ref reader);
                else if (reader.ValueTextEquals("n")) name = ReadNextString(ref reader);
                else throw Corrupt(reader);
            }
            parts.Add(kind switch
            {
                "text" when content is not null => new TextPart(content),
                "toolCall" when content is not null && name is not null => new ToolCallPart(MissingId(id), name, content),
                "toolResult" when content is not null => new ToolResultPart(MissingId(id), content),
                "image" when content is not null => new ImagePart(MissingId(id), content),
                _ => throw Malformed($"kind '{kind ?? "-"}' or its fields"),
            });
        }
        throw new InvalidOperationException("Malformed segment-JSON parts array");
    }

    private static void Expect(in Utf8JsonReader reader, JsonTokenType expected)
    {
        if (reader.TokenType != expected)
        {
            throw new InvalidOperationException($"Malformed segment-JSON record (expected {expected}, got {reader.TokenType})");
        }
    }

    private static string ExpectString(in Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new InvalidOperationException("Malformed segment-JSON record (expected a string)");
        }
        return reader.GetString() ?? throw new InvalidOperationException("Malformed segment-JSON record (null string)");
    }

    private static string ReadNextString(ref Utf8JsonReader reader)
    {
        reader.Read();
        return ExpectString(reader);
    }

    private static InvalidOperationException Corrupt(in Utf8JsonReader reader) =>
        new($"Malformed JSON record (unexpected token {reader.TokenType})");

    private static string MissingId(string? id) =>
        id ?? throw Malformed("tool-call/image id is missing");

    private static InvalidOperationException Malformed(string why) =>
        new($"Malformed segment-JSON part: {why}");

    /// <summary>
    /// Preview-index metadata for ONE message's parts, persisted as a JSON row:
    /// kind, toolCallId (paired kinds), full content character length, and the
    /// bounded preview string (up to <see cref="PreviewCap"/> code units).
    /// </summary>
    public static string EncodePartsMeta(IReadOnlyList<MessagePart> parts)
    {
        var row = new ArrayBufferWriter<byte>(128);
        using var json = new Utf8JsonWriter(row);
        json.WriteStartArray();
        foreach (var part in parts)
        {
            json.WriteStartObject();
            switch (part)
            {
                case TextPart text:
                    json.WriteString("k", "text");
                    json.WriteNumber("n", text.Content.Length);
                    json.WriteString("pv", Cap(text.Content));
                    break;
                case ToolCallPart call:
                    json.WriteString("k", "toolCall");
                    json.WriteString("id", call.ToolCallId);
                    json.WriteNumber("n", call.ArgsJson.Length);
                    json.WriteString("pv", Cap(call.ArgsJson));
                    break;
                case ToolResultPart result:
                    json.WriteString("k", "toolResult");
                    json.WriteString("id", result.ToolCallId);
                    json.WriteNumber("n", result.Content.Length);
                    json.WriteString("pv", Cap(result.Content));
                    break;
                case ImagePart image:
                    json.WriteString("k", "image");
                    json.WriteString("rp", image.RelativePath);
                    json.WriteNumber("n", image.RelativePath.Length);
                    json.WriteString("pv", Cap(image.RelativePath));
                    break;
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.Flush();
        return System.Text.Encoding.UTF8.GetString(row.WrittenSpan);
    }

    /// <summary>
    /// Materializes PartPreview rows for one message from its persisted
    /// metadata. With <paramref name="previewChars"/> at or under the persisted
    /// cap no large content is decoded; beyond the cap the bounded-row fallback
    /// slices the decoded content (still at most <paramref name="previewChars"/>).
    /// </summary>
    public static IReadOnlyList<PartPreview> DecodePartsMeta(
        string meta, int previewChars, IReadOnlyList<MessagePart>? partsForFallback = null)
    {
        var document = JsonDocument.Parse(meta);
        var result = new List<PartPreview>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var kind = element.GetProperty("k").GetString()!;
            string? toolCallId = kind is "toolCall" or "toolResult" ? element.GetProperty("id").GetString() : null;
            var contentChars = element.GetProperty("n").GetInt64();
            var stored = element.GetProperty("pv").GetString() ?? string.Empty;
            string preview;
            if (previewChars <= PreviewCap || partsForFallback is null)
            {
                preview = stored[..Math.Min(previewChars, stored.Length)];
            }
            else
            {
                var content = ContentOf(partsForFallback[result.Count]);
                preview = content[..Math.Min(previewChars, content.Length)];
            }
            result.Add(new PartPreview(kind, contentChars, toolCallId, preview));
        }
        return result;
    }

    public static string ContentOf(MessagePart part) => part switch
    {
        TextPart text => text.Content,
        ToolCallPart call => call.ArgsJson,
        ToolResultPart result => result.Content,
        ImagePart image => image.RelativePath,
        _ => throw new InvalidOperationException($"Unknown part type {part.GetType().Name}"),
    };

    private static string Cap(string content) =>
        content.Length <= PreviewCap ? content : content[..PreviewCap];
}

public static class SegmentDirs
{
    /// <summary>
    /// Filesystem-safe session directory name: hex-encoded UTF-8 of the session
    /// id (no collisions, no platform-hostile characters). A public prefix keeps
    /// recovery's orphan detection self-describing.
    /// </summary>
    public static string DirName(string sessionId)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(sessionId);
        return "s-" + Convert.ToHexString(bytes);
    }
}