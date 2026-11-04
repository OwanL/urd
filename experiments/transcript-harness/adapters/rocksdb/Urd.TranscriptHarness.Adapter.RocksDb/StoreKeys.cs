using System.Text;

namespace Urd.TranscriptHarness.Adapter.RocksDbStore;

// Explicit composite key encoding for the ordered keyspace. Every name field
// is encoded as a 4-byte big-endian UTF-8 byte length followed by the UTF-8
// bytes, so keys of different name lengths cannot alias; ordinals and stream
// sequence numbers are fixed-width 8-byte big-endian fields, so keys within a
// session (or within one draft stream) sort in ordinal/sequence order.
//
// Ordering consequence (adapter layout fact, not a harness contract): keys of
// one session are contiguous in keyspace order across sessions, because two
// names first compare on their length prefix and equal-length names cannot be
// strict prefixes of one another. All range operations still verify the exact
// session prefix on every key before consuming it.
internal static class StoreKeys
{
    /// <summary>Extracts the 8-byte big-endian ordinal that follows a name prefix.</summary>
    public static long ReadOrdinal(byte[] key, int prefixLength)
    {
        if (prefixLength + 8 > key.Length) throw new InvalidOperationException("Malformed key: missing ordinal");
        long value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = value << 8 | key[prefixLength + i];
        }
        return value;
    }

    /// <summary>Exclusive upper sentinel for any range ending at a session (or
    /// sub-name) prefix: 0xFF cannot be the leading byte of any encoded name
    /// length or big-endian ordinal field, so any real key sharing the prefix
    /// sorts strictly before this sentinel.</summary>
    public static readonly byte[] SentinelSuffix = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    public static byte[] NameKey(string name)
    {
        var utf8 = Encoding.UTF8.GetBytes(name);
        var key = new byte[4 + utf8.Length];
        WriteInt32BigEndian(key, 0, utf8.Length);
        utf8.CopyTo(key, 4);
        return key;
    }

    /// <summary>sessions metadata column family: one key per known session.</summary>
    public static byte[] SessionMeta(string sessionId) => NameKey(sessionId);

    /// <summary>messages column family: (session, ordinal) composite.</summary>
    public static byte[] Message(string sessionId, long ordinal) =>
        WithOrdinal(NameKey(sessionId), ordinal);

    /// <summary>assignments column family: one key per assignment id.</summary>
    public static byte[] AssignmentRecordKey(string assignmentId) => NameKey(assignmentId);

    /// <summary>assignment-index column family: (target session, assignment id)
    /// so open assignments of a session are a prefix range scan.</summary>
    public static byte[] AssignmentIndex(string targetSessionId, string assignmentId) =>
        Concat(NameKey(targetSessionId), NameKey(assignmentId));

    /// <summary>drafts column family: per-session active-stream marker -> stream id.</summary>
    public static byte[] DraftMarker(string sessionId) => NameKey(sessionId);

    /// <summary>drafts column family: stream batch record (session, streamId, sequence)
    /// -> batch text. Every StreamDraft call writes exactly one such record.</summary>
    public static byte[] DraftBatch(string sessionId, string streamId, long sequence) =>
        WithOrdinal(Concat(NameKey(sessionId), NameKey(streamId)), sequence);

    /// <summary>Prefix of all draft batch keys of one (session, stream) pair:
    /// the first byte position after this prefix is where the seq ordinal sits.</summary>
    public static byte[] DraftBatchPrefix(string sessionId, string streamId) =>
        Concat(NameKey(sessionId), NameKey(streamId));

    /// <summary>First key >= any real key whose base prefix is <paramref name="prefix"/>
    /// (used as the exclusive upper bound of ranges and DeleteRange calls).</summary>
    public static byte[] SentinelAfter(byte[] prefix) => Concat(prefix, SentinelSuffix);

    public static byte[] WithOrdinal(byte[] prefix, long ordinal)
    {
        var key = new byte[prefix.Length + 8];
        prefix.CopyTo(key, 0);
        // Signed values stored as two's complement bits; ordinals are always
        // small non-negative values, so big-endian storage sorts correctly.
        ulong bits = 0;
        if (ordinal >= 0)
        {
            bits = (ulong)ordinal;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), "Negative ordinals are outside the contract");
        }
        for (var i = 0; i < 8; i++)
        {
            key[prefix.Length + i] = (byte)(bits >> 56 - (i * 8));
        }
        return key;
    }

    public static byte[] Concat(byte[] left, byte[] right)
    {
        var result = new byte[left.Length + right.Length];
        left.CopyTo(result, 0);
        right.CopyTo(result, left.Length);
        return result;
    }

    public static bool HasPrefix(byte[] key, ArraySegment<byte> prefix)
    {
        if (key.Length < prefix.Count) return false;
        for (var i = 0; i < prefix.Count; i++)
        {
            if (key[i] != prefix.Array![prefix.Offset + i]) return false;
        }
        return true;
    }

    private static void WriteInt32BigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    /// <summary>Extracts the plain name that follows the given name prefix inside
    /// a composite key (e.g. assignment id after the target-session prefix).</summary>
    public static string ReadNameAfterPrefixedKey(byte[] key, ArraySegment<byte> prefix)
    {
        var start = prefix.Offset + prefix.Count;
        if (start + 4 > key.Length) throw new InvalidOperationException("Malformed key: missing embedded name");
        var length = (key[start] << 24) | (key[start + 1] << 16) | (key[start + 2] << 8) | key[start + 3];
        if (length < 0 || start + 4 + length > key.Length)
        {
            throw new InvalidOperationException("Malformed key: bad embedded name length");
        }
        return Encoding.UTF8.GetString(key, start + 4, length);
    }
}