namespace Urd.TranscriptHarness.LmdbStore;

// Order-preserving key codec for LMDB composite keys.
//
// LMDB orders keys by unsigned byte comparison. Session identifiers and other
// key components must therefore be encoded so that byte order equals the
// harness's StringComparer.Ordinal order (UTF-16 code-unit lexicographic
// order); the same encoding must also be self-delimiting so that one
// session's composite keys never overlap or interleave with another
// session's, whatever the other session's text is.
//
// Encoding ("unit-triplet"): every UTF-16 code unit is written as the three
// bytes 0x01, hi, lo, and the string is terminated by a single 0x00 byte.
//
// Order preservation proof sketch: comparison of two encodings first compares
// 0x01 markers (equal), then code units big-endian = unit numeric order, so
// divergences order exactly like StringComparer.Ordinal. If one string is a
// proper prefix of the other, its terminator 0x00 is met where the longer
// encoding has the next unit's 0x01 marker, so the shorter sorts first — as
// ordinal comparison requires. Self-delimiting/prefix-free: encodings differ
// at (or before) the first string's terminator, because in every encoding the
// terminator position is 0-based 3k, which in a longer encoding is always the
// next unit's 0x01 marker — so no session encoding is a byte-prefix of
// another, which makes per-session LMDB blocks exact and contiguous.
//
// Cost: 3 bytes per character + 1. Fine for key components of harness scale
// (session ids, assignment ids, stream ids). LMDB's default 511-byte key
// limit applies to the WHOLE composite key, so sessions with very long ids
// are out of scope here (documented in the adapter README).

internal static class KeyCodec
{
    public const byte UnitMark = 0x01;
    public const byte Terminator = 0x00;

    public static byte[] StringKey(string value)
    {
        var bytes = new byte[value.Length * 3 + 1];
        var position = 0;
        foreach (var unit in value)
        {
            bytes[position++] = UnitMark;
            bytes[position++] = (byte)((ushort)unit >> 8);
            bytes[position++] = (byte)unit;
        }
        bytes[position] = Terminator;
        return bytes;
    }

    /// <summary>Decodes one encoded string starting at offset; returns the value and bytes consumed.</summary>
    public static (string Value, int Bytes) ParseStringKey(ReadOnlySpan<byte> span, int start = 0)
    {
        var position = start;
        var units = new List<char>();
        while (true)
        {
            if (position >= span.Length)
            {
                throw new InvalidOperationException($"LmdbStore: corrupt key string encoding (no terminator at {start})");
            }
            var marker = span[position];
            if (marker == Terminator)
            {
                return (new string([.. units]), position + 1 - start);
            }
            if (marker != UnitMark || position + 2 >= span.Length)
            {
                throw new InvalidOperationException(
                    $"LmdbStore: corrupt key string encoding at byte {position} (marker 0x{marker:x2})");
            }
            units.Add((char)((span[position + 1] << 8) | span[position + 2]));
            position += 3;
        }
    }

    public static bool StartsWith(ReadOnlySpan<byte> key, ReadOnlySpan<byte> prefix) =>
        key.Length >= prefix.Length && key[..prefix.Length].SequenceEqual(prefix);

    // ---- composite keys; sessionKey comes from StringKey(sessionId) ----

    public static byte[] MessageKey(byte[] sessionKey, long ordinal) => [.. sessionKey, .. Int64Key(ordinal)];

    public static byte[] ContentKey(byte[] sessionKey, long ordinal, int partIndex) =>
        [.. sessionKey, .. Int64Key(ordinal), .. Int32Key(partIndex)];

    public static byte[] DraftMetaKey(byte[] sessionKey, string streamId) => [.. sessionKey, .. StringKey(streamId)];

    public static byte[] DraftChunkKey(byte[] sessionKey, string streamId, long chunkIndex) =>
        [.. sessionKey, .. StringKey(streamId), .. Int64Key(chunkIndex)];

    public static byte[] AssignmentIdKey(string assignmentId) => StringKey(assignmentId);

    public static byte[] AssignmentTargetKey(byte[] targetSessionKey, string assignmentId) =>
        [.. targetSessionKey, .. StringKey(assignmentId)];

    /// <summary>Decodes (ordinal, partIndex) after a session prefix in a content key.</summary>
    public static (long Ordinal, int PartIndex) ParseContentKey(ReadOnlySpan<byte> key, int sessionPrefixLength)
    {
        var rest = key[sessionPrefixLength..];
        if (rest.Length != 12)
        {
            throw new InvalidOperationException($"LmdbStore: corrupt content key (length {key.Length})");
        }
        return (ReadInt64Key(rest), ReadInt32Key(rest[8..]));
    }

    public static long ParseOrdinalKey(ReadOnlySpan<byte> key, int sessionPrefixLength)
    {
        var rest = key[sessionPrefixLength..];
        if (rest.Length != 8)
        {
            throw new InvalidOperationException($"LmdbStore: corrupt message key (length {key.Length})");
        }
        return ReadInt64Key(rest);
    }

    public static long ReadInt64Key(ReadOnlySpan<byte> span) =>
        (long)(((ulong)span[0] << 56) | ((ulong)span[1] << 48) | ((ulong)span[2] << 40) | ((ulong)span[3] << 32) |
               ((ulong)span[4] << 24) | ((ulong)span[5] << 16) | ((ulong)span[6] << 8) | span[7]);

    public static int ReadInt32Key(ReadOnlySpan<byte> span) =>
        (span[0] << 24) | (span[1] << 16) | (span[2] << 8) | span[3];

    public static byte[] Int64Key(long value)
    {
        var bytes = new byte[8];
        var u = (ulong)value;
        for (var i = 7; i >= 0; i--)
        {
            bytes[i] = (byte)u;
            u >>= 8;
        }
        return bytes;
    }

    public static byte[] Int32Key(int value)
    {
        var bytes = new byte[4];
        var u = (uint)value;
        for (var i = 3; i >= 0; i--)
        {
            bytes[i] = (byte)u;
            u >>= 8;
        }
        return bytes;
    }
}