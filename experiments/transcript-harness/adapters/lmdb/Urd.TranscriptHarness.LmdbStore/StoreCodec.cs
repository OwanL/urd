using System.Text;
using Urd.TranscriptHarness.Contracts;

namespace Urd.TranscriptHarness.LmdbStore;

// Adapter-owned binary record codec for LMDB values (versioned byte layout,
// deliberately not MessagePack: values are split so that metadata/preview
// headers stay small and decodable without touching potentially huge content
// payloads — see README). Strings in VALUES use length-prefixed UTF-8 (no
// ordering constraint); ordering-sensitive strings (keys) use KeyCodec.
//
// Record layouts (big-endian integer fields):
//   message header v1:
//     byte 1 | i32 len role UTF-8 | i32 partCount
//     per part: byte kind (0 text, 1 toolCall, 2 toolResult, 3 image)
//               | u64 contentChars (character length of that part's full
//                 content: text content / toolCall ArgsJson / toolResult
//                 content / image RelativePath)
//               | string storedPreview (leading content prefix, capped at
//                 PreviewBudgetChars characters)
//               | toolCall: string toolCallId, string name
//               | toolResult: string toolCallId
//               | image: string imageId, string relativePath
//   content record: raw UTF-8 of the content string (spilled out of the
//     header so preview reads never decode it)
//   session record: u64 dense message count
//   assignment record v1:
//     byte 1 | string targetSessionId | string title | byte status (0..2)
//     | byte hasResult | string resultText (when hasResult != 0)
//   draft-meta record: u64 next chunk index
//   draft chunk record: raw UTF-8 of the streamed batch text

internal enum PartKind : byte { Text = 0, ToolCall = 1, ToolResult = 2, Image = 3 }

internal sealed record PartMeta(
    PartKind Kind,
    long ContentChars,
    string StoredPreview,
    string? ToolCallId,
    string? Name,
    string? ImageId,
    string? RelativePath);

internal sealed record HeaderRecord(string Role, PartMeta[] Parts);

internal static class StoreCodec
{
    internal const byte Version = 1;

    // ---- primitive helpers ----

    public sealed class Writer
    {
        private byte[] _buffer = new byte[64];
        private int _length;

        public int Length => _length;

        public void WriteByte(byte value)
        {
            Reserve(1);
            _buffer[_length++] = value;
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            Reserve(bytes.Length);
            bytes.CopyTo(_buffer.AsSpan(_length));
            _length += bytes.Length;
        }

        public void WriteInt32(int value) => WriteBytes([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

        public void WriteUInt64(ulong value)
        {
            var bytes = new byte[8];
            for (var i = 7; i >= 0; i--)
            {
                bytes[i] = (byte)value;
                value >>= 8;
            }
            WriteBytes(bytes);
        }

        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(bytes.Length);
            WriteBytes(bytes);
        }

        private void Reserve(int additional)
        {
            if (_length + additional > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _buffer.Length + additional));
            }
        }

        public byte[] ToArray() => _buffer[.._length];
    }

    public ref struct Reader(ReadOnlySpan<byte> span)
    {
        private ReadOnlySpan<byte> _span = span;
        private int _position;

        public readonly int Remaining => _span.Length - _position;

        public byte ReadByte()
        {
            if (Remaining < 1) throw Corrupt();
            return _span[_position++];
        }

        public int ReadInt32()
        {
            if (Remaining < 4) throw Corrupt();
            var result = (_span[_position] << 24) | (_span[_position + 1] << 16) | (_span[_position + 2] << 8) | _span[_position + 3];
            _position += 4;
            return result;
        }

        public ulong ReadUInt64()
        {
            if (Remaining < 8) throw Corrupt();
            ulong result = 0;
            for (var i = 0; i < 8; i++)
            {
                result = (result << 8) | _span[_position + i];
            }
            _position += 8;
            return result;
        }

        public string ReadString()
        {
            var length = ReadInt32();
            if (length < 0 || length > Remaining)
            {
                throw Corrupt();
            }
            var text = Encoding.UTF8.GetString(_span.Slice(_position, length));
            _position += length;
            return text;
        }

        private static InvalidOperationException Corrupt() => new("LmdbStore: corrupt stored record");
    }

    // ---- message header ----

    public static byte[] EncodeMessageHeader(string role, IReadOnlyList<MessagePartEncoding> parts)
    {
        var writer = new Writer();
        writer.WriteByte(Version);
        writer.WriteString(role);
        writer.WriteInt32(parts.Count);
        foreach (var part in parts)
        {
            writer.WriteByte((byte)part.Kind);
            writer.WriteUInt64((ulong)part.ContentChars);
            writer.WriteString(part.StoredPreview);
            switch (part.Kind)
            {
                case PartKind.ToolCall:
                    writer.WriteString(part.ToolCallId!);
                    writer.WriteString(part.Name ?? "");
                    break;
                case PartKind.ToolResult:
                    writer.WriteString(part.ToolCallId!);
                    break;
                case PartKind.Image:
                    writer.WriteString(part.ImageId!);
                    writer.WriteString(part.RelativePath!);
                    break;
                case PartKind.Text:
                    break;
                default:
                    throw new InvalidOperationException($"LmdbStore: unknown part kind {part.Kind}");
            }
        }
        return writer.ToArray();
    }

    public static HeaderRecord DecodeMessageHeader(ReadOnlySpan<byte> record)
    {
        var reader = new Reader(record);
        if (reader.ReadByte() != Version) throw CorruptHeader("version");
        var role = reader.ReadString();
        var partCount = reader.ReadInt32();
        if (partCount < 0) throw CorruptHeader("part count");
        var parts = new PartMeta[partCount];
        for (var i = 0; i < partCount; i++)
        {
            var kind = (PartKind)reader.ReadByte();
            var contentChars = (long)reader.ReadUInt64();
            var storedPreview = reader.ReadString();
            string? toolCallId = null, name = null, imageId = null, relativePath = null;
            switch (kind)
            {
                case PartKind.ToolCall:
                    toolCallId = reader.ReadString();
                    name = reader.ReadString();
                    break;
                case PartKind.ToolResult:
                    toolCallId = reader.ReadString();
                    break;
                case PartKind.Image:
                    imageId = reader.ReadString();
                    relativePath = reader.ReadString();
                    break;
                case PartKind.Text:
                    break;
                default:
                    throw CorruptHeader($"part kind {kind}");
            }
            parts[i] = new PartMeta(kind, contentChars, storedPreview, toolCallId, name, imageId, relativePath);
        }
        if (reader.Remaining != 0) throw CorruptHeader("trailing bytes");
        return new HeaderRecord(role, parts);
    }

    private static InvalidOperationException CorruptHeader(string what) =>
        new($"LmdbStore: corrupt message header ({what})");

    // ---- session count ----

    public static byte[] EncodeSessionCount(long count)
    {
        var writer = new Writer();
        writer.WriteUInt64((ulong)count);
        return writer.ToArray();
    }

    public static long DecodeSessionCount(ReadOnlySpan<byte> record)
    {
        var reader = new Reader(record);
        return (long)reader.ReadUInt64();
    }

    // ---- assignment ----

    public static byte[] EncodeAssignment(AssignmentRecord assignment)
    {
        var writer = new Writer();
        writer.WriteByte(Version);
        writer.WriteString(assignment.TargetSessionId);
        writer.WriteString(assignment.Title);
        writer.WriteByte((byte)assignment.Status);
        writer.WriteByte(assignment.ResultText is null ? (byte)0 : (byte)1);
        if (assignment.ResultText is { } result) writer.WriteString(result);
        return writer.ToArray();
    }

    public static AssignmentRecord DecodeAssignment(string assignmentId, ReadOnlySpan<byte> record)
    {
        var reader = new Reader(record);
        if (reader.ReadByte() != Version) throw new InvalidOperationException("LmdbStore: corrupt assignment record (version)");
        var target = reader.ReadString();
        var title = reader.ReadString();
        var status = (AssignmentStatus)reader.ReadByte();
        var hasResult = reader.ReadByte();
        var result = hasResult != 0 ? reader.ReadString() : null;
        if (reader.Remaining != 0) throw new InvalidOperationException("LmdbStore: corrupt assignment record (trailing bytes)");
        return new AssignmentRecord(assignmentId, target, title, status, result);
    }

    // ---- draft meta ----

    public static byte[] EncodeDraftNextIndex(long nextIndex)
    {
        var writer = new Writer();
        writer.WriteUInt64((ulong)nextIndex);
        return writer.ToArray();
    }

    public static long DecodeDraftNextIndex(ReadOnlySpan<byte> record)
    {
        var reader = new Reader(record);
        return (long)reader.ReadUInt64();
    }
}

/// <summary>Per-part encoding input built when a message record is stored.</summary>
internal sealed record MessagePartEncoding(
    PartKind Kind,
    long ContentChars,
    string StoredPreview,
    string? ToolCallId,
    string? Name,
    string? ImageId,
    string? RelativePath,
    /// <summary>Full content to spill to the content DB; null for image parts (inline in header).</summary>
    string? Content);