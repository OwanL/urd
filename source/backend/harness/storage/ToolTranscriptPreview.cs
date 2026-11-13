using System.Globalization;
using System.Text;
using System.Text.Json;
using Urd.Core.Providers;

namespace Urd.Core.Storage;

// Reads the codec's text/images prefix, never its inspection-only details or an entire large JSON string.
internal sealed class ToolTranscriptPreview(int characterLimit) {
  private readonly StringBuilder text = new();
  private char firstOmitted;

  internal bool Truncated { get; private set; }

  internal string Value {
    get {
      int length = text.Length;
      if (Truncated && length > 0 && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(firstOmitted))
        length--;
      return text.ToString(0, length);
    }
  }

  internal static string ReadText(TextReader reader, int? characterLimit, out bool truncated) {
    if (characterLimit is not { } limit) {
      truncated = false;
      return reader.ReadToEnd();
    }
    var preview = new ToolTranscriptPreview(limit);
    while (!preview.Truncated && reader.Read() is >= 0 and var character)
      preview.Append((char)character);
    truncated = preview.Truncated;
    return preview.Value;
  }

  internal static string ReadResult(TextReader reader, int characterLimit, bool supportsImages,
      string prefix, out bool truncated) {
    var preview = new ToolTranscriptPreview(characterLimit);
    preview.Append(prefix);
    if (!preview.Truncated)
      preview.ReadResult(reader, supportsImages);
    truncated = preview.Truncated;
    return preview.Value;
  }

  private void Append(char character) {
    if (text.Length < characterLimit) {
      text.Append(character);
      return;
    }
    firstOmitted = character;
    Truncated = true;
  }

  private void Append(string value) {
    foreach (char character in value) {
      if (Truncated)
        break;
      Append(character);
    }
  }

  private void ReadResult(TextReader reader, bool supportsImages) {
    int resultStart = text.Length;
    Expect(reader, "{\"text\":");
    ReadString(reader);
    if (Truncated)
      return;
    Expect(reader, ",\"images\":[");
    if (Next(reader) == ']')
      return;
    if (!supportsImages) {
      Append(LlmProviderContext.AppendImageOmissionNotice(string.Empty));
      return;
    }
    while (!Truncated) {
      // ToolImageStore writes a fixed-length SHA-256 hash, then the model-facing media type.
      Expect(reader, "{\"hash\":\"");
      for (int index = 0; index < 64; index++)
        if (reader.Read() < 0)
          throw new JsonException("Stored tool image reference is incomplete.");
      Expect(reader, "\",\"mediaType\":");
      if (text.Length > resultStart)
        Append('\n');
      Append("[image: ");
      if (Truncated)
        return;
      ReadString(reader);
      Append("]");
      if (Truncated)
        return;
      Expect(reader, ",\"byteLength\":");
      while (Next(reader) is >= '0' and <= '9')
        reader.Read();
      Expect(reader, "}");
      if (Next(reader) == ']')
        return;
      Expect(reader, ",");
    }
  }

  private void ReadString(TextReader reader) {
    Expect(reader, "\"");
    while (!Truncated) {
      int character = reader.Read();
      if (character == '"')
        return;
      if (character < ' ')
        throw new JsonException("Stored tool output has an invalid JSON string.");
      if (character == '\\') {
        character = reader.Read() switch {
          '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f',
          'n' => '\n', 'r' => '\r', 't' => '\t', 'u' => ReadUnicodeEscape(reader),
          _ => throw new JsonException("Stored tool output has an invalid JSON escape.")
        };
      }
      Append((char)character);
    }
  }

  private static char ReadUnicodeEscape(TextReader reader) {
    Span<char> digits = stackalloc char[4];
    for (int index = 0; index < digits.Length; index++) {
      int character = reader.Read();
      if (character < 0)
        throw new JsonException("Stored tool output has an incomplete Unicode escape.");
      digits[index] = (char)character;
    }
    return ushort.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort value)
        ? (char)value : throw new JsonException("Stored tool output has an invalid Unicode escape.");
  }

  private static void Expect(TextReader reader, ReadOnlySpan<char> token) {
    foreach (char character in token)
      if (Next(reader) != character || reader.Read() < 0)
        throw new JsonException("Stored tool output has an invalid text/images prefix.");
  }

  private static int Next(TextReader reader) {
    while (reader.Peek() is ' ' or '\t' or '\r' or '\n')
      reader.Read();
    return reader.Peek();
  }
}
