using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Urd.Core.Storage;

// Shares strict encoding, text bounds and tool JSON validation across storage operations.
internal static class StorageText {
  internal const int MaximumBytes = 16 * 1024;
  internal const int MaximumSegments = 16;
  internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

  internal static string UtcTimestamp() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

  // Capture valid JSON text now rather than retaining a borrowed JsonDocument or serializing exceptions.
  internal static string JsonText(JsonElement value, bool requireObject = false) {
    if (value.ValueKind == JsonValueKind.Undefined || (requireObject && value.ValueKind != JsonValueKind.Object))
      throw new ArgumentException("Tool payloads require valid JSON; arguments must be an object.", nameof(value));

    return value.GetRawText();
  }
}
