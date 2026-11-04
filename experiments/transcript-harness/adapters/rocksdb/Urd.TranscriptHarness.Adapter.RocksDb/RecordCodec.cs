using System.Text;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.Adapter.RocksDbStore;

// Explicit hand-rolled binary payload encoding, version-tagged. No reflection,
// no schema-generation pipeline: every field is 4-byte big-endian length +
// UTF-8 bytes, and tags/version are checked on decode. Keeping the encoding
// explicit also makes the bounded preview path possible without decoding full
// large contents into strings (see RecordCodec.DecodedPrefix).

internal static class RecordCodec
{
    private const byte PartTagText = 1;
    private const byte PartTagToolCall = 2;
    private const byte PartTagToolResult = 3;
    private const byte PartTagImage = 4;

    // ---- message values ----
    // [0x01][len role][role][4BE partCount] then one part:
    //   text:       [0x01][len content][content]
    //   toolCall:   [0x02][len id][id][len name][name][len args][args]
    //   toolResult: [0x03][len id][id][len content][content]
    //   image:      [0x04][len imageId][imageId][len relativePath][relativePath]

    public static byte[] EncodeMessage(MessageRecord message)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: false);
        writer.Write((byte)0x01); // version
        writer.WriteName(message.Role);
        writer.WriteInt32BigEndian(message.Parts.Count);
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case TextPart text:
                    writer.Write(PartTagText);
                    writer.WriteName(text.Content);
                    break;
                case ToolCallPart call:
                    writer.Write(PartTagToolCall);
                    writer.WriteName(call.ToolCallId);
                    writer.WriteName(call.Name);
                    writer.WriteName(call.ArgsJson);
                    break;
                case ToolResultPart result:
                    writer.Write(PartTagToolResult);
                    writer.WriteName(result.ToolCallId);
                    writer.WriteName(result.Content);
                    break;
                case ImagePart image:
                    writer.Write(PartTagImage);
                    writer.WriteName(image.ImageId);
                    writer.WriteName(image.RelativePath);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown part type {part.GetType().Name}");
            }
        }
        writer.Flush();
        return buffer.ToArray();
    }

    public static MessageRecord DecodeMessage(byte[] data) => DecodeMessage(data, fullContent: true, previewChars: 0).Full!;

    /// <summary>Preview decode: returns role and per-part (kind, charLength,
    /// toolCallId, preview) WITHOUT building full content strings — large
    /// tool-result contents are only length-counted over bytes and previewed
    /// by decoding a bounded leading prefix.</summary>
    public static MessageDecode DecodeMessage(byte[] data, bool fullContent, int previewChars)
    {
        var reader = new ByteReader(data);
        var version = reader.ReadByte();
        if (version != 0x01)
        {
            throw new InvalidOperationException($"Unknown payload version {version}");
        }
        var role = reader.ReadName();
        var partCount = reader.ReadInt32BE();
        string? fullRole = role;
        MessageRecord? full = null;
        if (fullContent)
        {
            var parts = new List<MessagePart>(partCount);
            for (var p = 0; p < partCount; p++)
            {
                parts.Add(ReadFullPart(ref reader));
            }
            full = new MessageRecord(role, parts);
            return new MessageDecode(role, null, full);
        }
        var previews = new List<PartPreview>(partCount);
        for (var p = 0; p < partCount; p++)
        {
            var tag = reader.ReadByte();
            switch (tag)
            {
                case PartTagText:
                    previews.Add(BoundedPreviewOf(reader, "text", toolCallId: null, previewChars));
                    break;
                case PartTagToolCall:
                    var callId = reader.ReadName();
                    var _ = reader.ReadName(); // name (not preview content)
                    previews.Add(BoundedPreviewOf(reader, "toolCall", toolCallId: callId, previewChars));
                    break;
                case PartTagToolResult:
                    var resultId = reader.ReadName();
                    previews.Add(BoundedPreviewOf(reader, "toolResult", toolCallId: resultId, previewChars));
                    break;
                case PartTagImage:
                    var __ = reader.ReadName(); // imageId
                    var relativePath = reader.ReadName();
                    previews.Add(new PartPreview("image", relativePath.Length, null,
                        relativePath[..Math.Min(relativePath.Length, Math.Max(previewChars, 0))]));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown part tag {tag}");
            }
        }
        return new MessageDecode(role, previews, null);
    }

    public sealed record MessageDecode(string Role, IReadOnlyList<PartPreview>? Previews, MessageRecord? Full);

    private static MessagePart ReadFullPart(ref ByteReader reader)
    {
        var tag = reader.ReadByte();
        return tag switch
        {
            PartTagText => new TextPart(reader.ReadName()),
            PartTagToolCall => new ToolCallPart(reader.ReadName(), reader.ReadName(), reader.ReadName()),
            PartTagToolResult => new ToolResultPart(reader.ReadName(), reader.ReadName()),
            PartTagImage => new ImagePart(reader.ReadName(), reader.ReadName()),
            _ => throw new InvalidOperationException($"Unknown part tag {tag}"),
        };
    }

    /// <summary>Metadata + bounded preview for one length-prefixed content field:
    /// the field's bytes are the reader's next length-prefixed item.</summary>
    private static PartPreview BoundedPreviewOf(ByteReader reader, string kind, string? toolCallId, int previewChars)
    {
        var byteLength = reader.ReadInt32BE();
        reader.AssertAvailable(byteLength);
        var contentChars = CountCodeUnits(reader.Data, reader.Offset, byteLength);
        var preview = DecodedPrefix(reader.Data, reader.Offset, byteLength, previewChars);
        reader.Skip(byteLength);
        return new PartPreview(kind, contentChars, toolCallId, preview);
    }

    /// <summary>Number of UTF-16 code units the full payload decodes to: counted
    /// over bytes without decoding — 1–3-byte UTF-8 clusters are BMP code units,
    /// 4-byte clusters are astral surrogate pairs (2 code units). Payload bytes
    /// are produced by Encoding.UTF8.GetBytes so they are well-formed.</summary>
    private static long CountCodeUnits(byte[] data, int offset, int byteLength)
    {
        long units = 0;
        var end = offset + byteLength;
        var i = offset;
        while (i < end)
        {
            var leading = data[i];
            var cluster = leading < 0x80 ? 1
                : leading < 0xC0 ? throw new InvalidOperationException("Malformed payload: continuation byte")
                : leading < 0xE0 ? 2
                : leading < 0xF0 ? 3
                : 4;
            if (i + cluster > end) throw new InvalidOperationException("Malformed payload: truncated cluster");
            i += cluster;
            units += cluster < 4 ? 1 : 2;
        }
        return units;
    }

    /// <summary>Leading prefix preview of a content payload: decodes only the
    /// byte prefix covering at most previewChars code units (whole clusters, so
    /// the decoded text is always an exact leading substring of the full decode,
    /// then truncated to exactly previewChars code units like the full string
    /// slicing would be).</summary>
    public static string DecodedPrefix(byte[] data, int offset, int byteLength, int maxChars)
    {
        if (maxChars <= 0) return string.Empty;
        var end = offset + byteLength;
        var i = offset;
        var units = 0L;
        while (i < end && units < maxChars)
        {
            var leading = data[i];
            var cluster = leading < 0x80 ? 1
                : leading < 0xC0 ? throw new InvalidOperationException("Malformed payload: continuation byte")
                : leading < 0xE0 ? 2
                : leading < 0xF0 ? 3
                : 4;
            if (i + cluster > end) throw new InvalidOperationException("Malformed payload: truncated cluster");
            i += cluster;
            units += cluster < 4 ? 1 : 2;
        }
        var text = Encoding.UTF8.GetString(data, offset, i - offset);
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    // ---- session count values: [0x01][8BE count] ----

    public static byte[] EncodeCount(long count)
    {
        var value = new byte[9];
        value[0] = 0x01;
        for (var i = 0; i < 8; i++)
        {
            value[1 + i] = (byte)((long)count >> 56 - (i * 8));
        }
        return value;
    }

    public static long DecodeCount(byte[] value)
    {
        if (value.Length != 9 || value[0] != 0x01)
        {
            throw new InvalidOperationException("Malformed session count payload");
        }
        var result = 0L;
        for (var i = 0; i < 8; i++)
        {
            result = result << 8 | value[1 + i];
        }
        return result;
    }

    // ---- assignment records----
    // [0x01][len id][id][len target][target][len title][title][status byte]
    // [resultPresent byte][optional len+resultText]

    public static byte[] EncodeAssignment(AssignmentRecord assignment)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: false);
        writer.Write((byte)0x01); // version
        writer.WriteName(assignment.AssignmentId);
        writer.WriteName(assignment.TargetSessionId);
        writer.WriteName(assignment.Title);
        writer.Write((byte)RecordCodec.StatusByteOf(assignment.Status));
        if (assignment.ResultText is { } result)
        {
            writer.Write((byte)1);
            writer.WriteName(result);
        }
        else
        {
            writer.Write((byte)0);
        }
        writer.Flush();
        return buffer.ToArray();
    }

    public static AssignmentRecord DecodeAssignment(byte[] data)
    {
        var reader = new ByteReader(data);
        var version = reader.ReadByte();
        if (version != 0x01) throw new InvalidOperationException($"Unknown payload version {version}");
        var id = reader.ReadName();
        var target = reader.ReadName();
        var title = reader.ReadName();
        var status = reader.ReadByte();
        var present = reader.ReadByte();
        return new AssignmentRecord(
            id, target, title,
            status switch
            {
                0 => AssignmentStatus.Open,
                1 => AssignmentStatus.Settled,
                2 => AssignmentStatus.Cancelled,
                _ => throw new InvalidOperationException($"Unknown assignment status byte {status}"),
            },
            present == 1 ? reader.ReadName() : null);
    }

    internal static byte StatusByteOf(AssignmentStatus status) => status switch
    {
        AssignmentStatus.Open => 0,
        AssignmentStatus.Settled => 1,
        AssignmentStatus.Cancelled => 2,
        _ => throw new InvalidOperationException($"Unknown assignment status {status}"),
    };

    /// <summary>Active-stream marker value: [0x01][len streamId][streamId][8BE lastSeq].</summary>
    public static byte[] EncodeStreamMarker(string streamId, long lastSequence)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: false);
        writer.Write((byte)0x01); // version
        writer.WriteName(streamId);
        writer.WriteInt64BigEndian(lastSequence);
        writer.Flush();
        return buffer.ToArray();
    }

    public static (string StreamId, long LastSequence) DecodeStreamMarker(byte[] data)
    {
        var reader = new ByteReader(data);
        var version = reader.ReadByte();
        if (version != 0x01) throw new InvalidOperationException($"Unknown payload version {version}");
        var streamId = reader.ReadName();
        return (streamId, reader.ReadInt64BigEndian());
    }
}

// Explicit little reader over one byte buffer: lengths are 4-byte big-endian;
// strings are UTF-8. A class (not a struct): read methods mutate the shared
// instance, so a single reader walks one payload across calls.
internal sealed class ByteReader
{
    public byte[] Data;
    public int Offset;

    public ByteReader(byte[] data)
    {
        Data = data;
        Offset = 0;
    }

    public int Remaining => Data.Length - Offset;

    public void AssertAvailable(int bytes)
    {
        if (Offset + bytes > Data.Length)
        {
            throw new InvalidOperationException("Malformed payload: unexpected end of data");
        }
    }

    public void Skip(int bytes)
    {
        AssertAvailable(bytes);
        Offset += bytes;
    }

    public byte ReadByte()
    {
        AssertAvailable(1);
        return Data[Offset++];
    }

    public int ReadInt32BE()
    {
        AssertAvailable(4);
        var value = (Data[Offset] << 24) | (Data[Offset + 1] << 16) | (Data[Offset + 2] << 8) | Data[Offset + 3];
        Offset += 4;
        return value;
    }

    public long ReadInt64BigEndian()
    {
        AssertAvailable(8);
        long value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = value << 8 | Data[Offset + i];
        }
        Offset += 8;
        return value;
    }

    public string ReadName()
    {
        var length = ReadInt32BE();
        if (length < 0 || Offset + length > Data.Length)
        {
            throw new InvalidOperationException("Malformed payload: bad string length");
        }
        var text = Encoding.UTF8.GetString(Data, Offset, length);
        Offset += length;
        return text;
    }
}

internal static class BinaryWriterExtensions
{
    // BinaryWriter.Write(int/long) emits little-endian; the decoder reads
    // big-endian, so int fields are written here explicitly in big-endian form.
    public static void WriteInt32BigEndian(this BinaryWriter writer, int length)
    {
        writer.Write((byte)(length >> 24));
        writer.Write((byte)(length >> 16));
        writer.Write((byte)(length >> 8));
        writer.Write((byte)length);
    }

    public static void WriteInt64BigEndian(this BinaryWriter writer, long value)
    {
        for (var i = 7; i >= 0; i--)
        {
            writer.Write((byte)(value >> (i * 8)));
        }
    }

    public static void WriteName(this BinaryWriter writer, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        writer.WriteInt32BigEndian(utf8.Length);
        writer.Write(utf8);
    }
}