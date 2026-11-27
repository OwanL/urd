using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Urd.Core.Execution;

// Keep a fixed prefix and a ring of the most recent bytes; concatenate only for a snapshot.
internal sealed class ToolDraftPreview {
  private const int HeadLimit = 1024;
  private const int TailLimit = 3 * 1024;
  private const int PreviewLimit = HeadLimit + TailLimit;
  private byte[]? buffer;
  private readonly byte[] token = new byte[66];
  private int tokenLength;
  private bool inString;
  private bool escaped;
  private bool pendingToken;

  internal long Count { get; private set; }
  internal string? Field { get; private set; }

  internal void Reset() {
    Count = 0;
    Field = null;
    tokenLength = 0;
    inString = escaped = pendingToken = false;
  }

  internal void Append(ReadOnlySpan<byte> bytes) {
    Scan(bytes);
    buffer ??= new byte[PreviewLimit];
    long offset = Count;
    Count += bytes.Length;
    if (offset < HeadLimit) {
      int head = Math.Min(bytes.Length, HeadLimit - (int)offset);
      bytes[..head].CopyTo(buffer.AsSpan((int)offset));
      bytes = bytes[head..];
    }
    if (bytes.IsEmpty)
      return;

    int tailBytes = Math.Min(TailLimit, bytes.Length);
    int start = TailOffset(Count - tailBytes);
    ReadOnlySpan<byte> tail = bytes[^tailBytes..];
    int first = Math.Min(TailLimit - start, tailBytes);
    tail[..first].CopyTo(buffer.AsSpan(HeadLimit + start));
    tail[first..].CopyTo(buffer.AsSpan(HeadLimit));
  }

  internal (string Head, string Tail) Read() {
    if (Count <= PreviewLimit)
      return (Decode(buffer.AsSpan(0, (int)Count)), "");

    int start = TailOffset(Count);
    ReadOnlySpan<byte> ring = buffer.AsSpan(HeadLimit, TailLimit);
    Span<byte> tail = stackalloc byte[TailLimit];
    ring[start..].CopyTo(tail);
    ring[..start].CopyTo(tail[(TailLimit - start)..]);
    return (Decode(buffer.AsSpan(0, HeadLimit)), Decode(tail));
  }

  private void Scan(ReadOnlySpan<byte> bytes) {
    foreach (byte value in bytes) {
      if (inString) {
        if (!escaped && value == '"') {
          inString = false;
          pendingToken = true;
          continue;
        }
        if (tokenLength < 65)
          token[tokenLength++] = value;
        else
          tokenLength = 66;
        if (escaped)
          escaped = false;
        else if (value == '\\')
          escaped = true;
      } else if (value == '"') {
        inString = true;
        pendingToken = false;
        token[0] = (byte)'"';
        tokenLength = 1;
      } else if (pendingToken && value == ':') {
        if (tokenLength <= 65) {
          token[tokenLength] = (byte)'"';
          try {
            Field = JsonSerializer.Deserialize<string>(token.AsSpan(0, tokenLength + 1));
          } catch (JsonException) {
            // Incomplete or invalid provider output is display-only.
          }
        }
        pendingToken = false;
      } else if (value is not (byte)' ' and not (byte)'\t' and not (byte)'\r' and not (byte)'\n') {
        pendingToken = false;
      }
    }
  }

  private static int TailOffset(long count) => (int)((count - HeadLimit) % TailLimit);

  private static string Decode(ReadOnlySpan<byte> bytes) {
    int start = 0;
    while (start < bytes.Length && IsContinuation(bytes[start]))
      start++;
    if (start == bytes.Length)
      return "";
    int lastScalar = bytes.Length - 1;
    while (lastScalar > start && IsContinuation(bytes[lastScalar]))
      lastScalar--;
    int end = Rune.DecodeFromUtf8(bytes[lastScalar..], out _, out _) == OperationStatus.NeedMoreData
        ? lastScalar : bytes.Length;
    return Encoding.UTF8.GetString(bytes[start..end]);
  }

  private static bool IsContinuation(byte value) => (value & 0xC0) == 0x80;
}
