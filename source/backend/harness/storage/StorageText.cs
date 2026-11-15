using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Urd.Core.Storage;

// Shares strict encoding, text bounds and tool JSON validation across storage operations.
internal static class StorageText {
  internal const int MaximumBytes = 16 * 1024;
  internal const int MaximumSegments = 16;
  internal const int PreviewUtf16Limit = 256;
  internal const int PreviewByteLimit = PreviewUtf16Limit * 3 + 3;
  internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

  internal static string UtcTimestamp() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

  internal static string ReadPreview(byte[] utf8) {
    var text = new StringBuilder(Math.Min(PreviewUtf16Limit, utf8.Length));
    AppendPrefix(utf8, text, int.MaxValue, PreviewUtf16Limit, allowIncompleteTail: true,
        invalidUtf8Message: "A pending input contains invalid UTF-8.");
    return text.ToString();
  }

  internal static int AppendPrefix(ReadOnlySpan<byte> prefix, StringBuilder text, int maximumUtf8Bytes,
      int maximumUtf16Units, bool allowIncompleteTail = false,
      string invalidUtf8Message = "A message segment contains invalid UTF-8.") {
    int consumed = 0;
    Span<char> scalar = stackalloc char[2];
    while (consumed < prefix.Length && text.Length < maximumUtf16Units && consumed < maximumUtf8Bytes) {
      OperationStatus status = Rune.DecodeFromUtf8(prefix[consumed..], out Rune rune, out int scalarBytes);
      if (allowIncompleteTail && status == OperationStatus.NeedMoreData)
        break;
      if (status != OperationStatus.Done)
        throw new InvalidDataException(invalidUtf8Message);
      if (scalarBytes > maximumUtf8Bytes - consumed || rune.Utf16SequenceLength > maximumUtf16Units - text.Length)
        break;

      int units = rune.EncodeToUtf16(scalar);
      text.Append(scalar[..units]);
      consumed += scalarBytes;
    }
    return consumed;
  }

  // Capture valid JSON text now rather than retaining a borrowed JsonDocument or serializing exceptions.
  internal static string JsonText(JsonElement value, bool requireObject = false) {
    if (value.ValueKind == JsonValueKind.Undefined || (requireObject && value.ValueKind != JsonValueKind.Object))
      throw new ArgumentException("Tool payloads require valid JSON; arguments must be an object.", nameof(value));

    return value.GetRawText();
  }
}
