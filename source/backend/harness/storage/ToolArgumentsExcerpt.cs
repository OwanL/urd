using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Urd.Core.Storage;

// Builds a deterministic JSON preview of tool arguments within a fixed UTF-8 byte budget.
internal static class ToolArgumentsExcerpt {
  private const int MaximumBytes = 4096;
  private const int MaximumCandidateBufferBytes = 16 * 1024;
  private const int MaximumScalarStringBytes = 2048;
  private const int MaximumNestedStringChars = 256;
  private const int MaximumPrefixChars = 256;
  private const int MaximumKeyBytes = 128;
  private const int MaximumArrayItems = 16;

  internal static string Create(string argumentsJson) {
    ArgumentNullException.ThrowIfNull(argumentsJson);
    using JsonDocument document = JsonDocument.Parse(argumentsJson);
    if (document.RootElement.ValueKind != JsonValueKind.Object)
      throw new ArgumentException("Tool arguments must be a JSON object.", nameof(argumentsJson));

    int memberCount = document.RootElement.EnumerateObject().Count();
    byte[] candidateBytes = ArrayPool<byte>.Shared.Rent(MaximumCandidateBufferBytes);
    byte[] outputBytes = ArrayPool<byte>.Shared.Rent(MaximumBytes);
    try {
      var candidate = new LimitedBufferWriter(candidateBytes, MaximumBytes);
      var output = new LimitedBufferWriter(outputBytes, MaximumBytes);
      int markerBytes = memberCount == 0 ? 0 : MeasureOmittedMarker(candidate, memberCount);
      int memberBudget = MaximumBytes - 2 - markerBytes - (memberCount == 0 ? 0 : 1);
      int admittedCount = 0;
      int admittedBytes = 0;
      bool stopped = false;
      output.WriteByte((byte)'{');

      foreach (JsonProperty property in document.RootElement.EnumerateObject()) {
        if (!IsScalar(property.Value.ValueKind))
          continue;

        if (!TryAdmit(property, true, candidate, output, memberBudget, ref admittedCount, ref admittedBytes)) {
          stopped = true;
          break;
        }
      }

      if (!stopped) {
        foreach (JsonProperty property in document.RootElement.EnumerateObject()) {
          if (IsScalar(property.Value.ValueKind))
            continue;

          if (!TryAdmit(property, false, candidate, output, memberBudget, ref admittedCount, ref admittedBytes))
            break;
        }
      }

      int omittedCount = memberCount - admittedCount;
      if (omittedCount > 0) {
        if (admittedCount > 0)
          output.WriteByte((byte)',');
        WriteOmittedMarker(candidate, omittedCount);
        output.Write(candidate.WrittenSpan[1..^1]);
      }
      output.WriteByte((byte)'}');
      return Encoding.UTF8.GetString(output.WrittenSpan);
    } finally {
      ArrayPool<byte>.Shared.Return(candidateBytes, clearArray: true);
      ArrayPool<byte>.Shared.Return(outputBytes, clearArray: true);
    }
  }

  private static bool TryAdmit(JsonProperty property, bool scalar, LimitedBufferWriter candidate,
      LimitedBufferWriter output, int memberBudget, ref int admittedCount, ref int admittedBytes) {
    string name = property.Name;
    if (Encoding.UTF8.GetByteCount(name) > MaximumKeyBytes)
      return true;

    int commaBytes = admittedCount == 0 ? 0 : 1;
    int remaining = memberBudget - admittedBytes - commaBytes;
    if (remaining < 0 || !TryWriteProperty(candidate, name, property.Value, scalar, remaining + 2))
      return false;

    int memberBytes = candidate.WrittenCount - 2;
    if (admittedCount > 0)
      output.WriteByte((byte)',');
    output.Write(candidate.WrittenSpan[1..^1]);
    admittedCount++;
    admittedBytes += commaBytes + memberBytes;
    return true;
  }

  private static int MeasureOmittedMarker(LimitedBufferWriter buffer, int maximumCount) {
    WriteOmittedMarker(buffer, maximumCount);
    return buffer.WrittenCount - 2;
  }

  private static void WriteOmittedMarker(LimitedBufferWriter buffer, int count) {
    buffer.Reset(MaximumBytes);
    using var writer = new Utf8JsonWriter(buffer);
    writer.WriteStartObject();
    writer.WriteNumber("$omitted", count);
    writer.WriteEndObject();
    writer.Flush();
  }

  private static bool TryWriteProperty(LimitedBufferWriter buffer, string name, JsonElement value,
      bool scalar, int capacity) {
    buffer.Reset(capacity);
    try {
      using var writer = new Utf8JsonWriter(buffer);
      writer.WriteStartObject();
      writer.WritePropertyName(name);
      if (scalar)
        WriteTopLevelScalar(writer, value);
      else
        WriteNestedValue(writer, value);
      writer.WriteEndObject();
      writer.Flush();
      return true;
    } catch (InvalidOperationException) when (buffer.Exceeded) {
      return false;
    }
  }

  private static void WriteTopLevelScalar(Utf8JsonWriter writer, JsonElement value) {
    if (value.ValueKind != JsonValueKind.String) {
      value.WriteTo(writer);
      return;
    }

    string text = value.GetString()!;
    if (text.Length > MaximumScalarStringBytes || Encoding.UTF8.GetByteCount(text) > MaximumScalarStringBytes)
      WriteTruncated(writer, text);
    else
      writer.WriteStringValue(text);
  }

  private static void WriteNestedValue(Utf8JsonWriter writer, JsonElement value) {
    switch (value.ValueKind) {
      case JsonValueKind.Object:
        writer.WriteStartObject();
        int omittedCount = 0;
        foreach (JsonProperty property in value.EnumerateObject()) {
          string name = property.Name;
          if (Encoding.UTF8.GetByteCount(name) > MaximumKeyBytes) {
            omittedCount++;
            continue;
          }
          writer.WritePropertyName(name);
          WriteNestedValue(writer, property.Value);
        }
        if (omittedCount > 0)
          writer.WriteNumber("$omitted", omittedCount);
        writer.WriteEndObject();
        break;
      case JsonValueKind.Array:
        WriteArray(writer, value);
        break;
      case JsonValueKind.String:
        string text = value.GetString()!;
        if (text.Length > MaximumNestedStringChars)
          WriteTruncated(writer, text);
        else
          writer.WriteStringValue(text);
        break;
      default:
        value.WriteTo(writer);
        break;
    }
  }

  private static void WriteArray(Utf8JsonWriter writer, JsonElement array) {
    writer.WriteStartArray();
    int count = array.GetArrayLength();
    int admitted = 0;
    foreach (JsonElement item in array.EnumerateArray()) {
      if (admitted == MaximumArrayItems)
        break;
      WriteNestedValue(writer, item);
      admitted++;
    }
    if (count > admitted)
      WriteArrayOmittedMarker(writer, count - admitted);
    writer.WriteEndArray();
  }

  private static void WriteArrayOmittedMarker(Utf8JsonWriter writer, int count) {
    writer.WriteStartObject();
    writer.WriteNumber("$omitted", count);
    writer.WriteEndObject();
  }

  private static void WriteTruncated(Utf8JsonWriter writer, string text) {
    writer.WriteStartObject();
    writer.WriteString("$truncated", SafePrefix(text));
    writer.WriteNumber("length", text.Length);
    writer.WriteEndObject();
  }

  private static string SafePrefix(string text) {
    Span<char> prefix = stackalloc char[MaximumPrefixChars];
    ReadOnlySpan<char> remaining = text.AsSpan();
    int written = 0;
    while (!remaining.IsEmpty) {
      System.Buffers.OperationStatus status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int consumed);
      if (status != System.Buffers.OperationStatus.Done) {
        rune = Rune.ReplacementChar;
        consumed = 1;
      }
      if (rune.Utf16SequenceLength > MaximumPrefixChars - written)
        break;
      rune.TryEncodeToUtf16(prefix[written..], out int encoded);
      written += encoded;
      remaining = remaining[consumed..];
    }
    return new string(prefix[..written]);
  }

  private static bool IsScalar(JsonValueKind kind) => kind is JsonValueKind.String or JsonValueKind.Number
      or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;

  private sealed class LimitedBufferWriter(byte[] buffer, int limit) : IBufferWriter<byte> {
    private int written;
    private int currentLimit = limit;

    internal bool Exceeded { get; private set; }
    internal int WrittenCount => written;
    internal ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, written);

    internal void Reset(int maximumBytes) {
      if ((uint)maximumBytes > (uint)buffer.Length)
        throw new ArgumentOutOfRangeException(nameof(maximumBytes));
      written = 0;
      currentLimit = maximumBytes;
      Exceeded = false;
    }

    internal void WriteByte(byte value) {
      if (written == currentLimit)
        throw Exceed();
      buffer[written++] = value;
    }

    internal void Write(ReadOnlySpan<byte> value) {
      if (value.Length > currentLimit - written)
        throw Exceed();
      value.CopyTo(buffer.AsSpan(written));
      written += value.Length;
    }

    private InvalidOperationException Exceed() {
      Exceeded = true;
      return new InvalidOperationException("The excerpt budget is exhausted.");
    }

    public void Advance(int count) {
      ArgumentOutOfRangeException.ThrowIfNegative(count);
      if (count > currentLimit - written)
        throw Exceed();
      written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => GetAvailableMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => GetAvailableMemory(sizeHint).Span;

    private Memory<byte> GetAvailableMemory(int sizeHint) {
      ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
      if (sizeHint == 0)
        sizeHint = 1;
      if (sizeHint > buffer.Length - written)
        throw Exceed();
      return buffer.AsMemory(written);
    }
  }
}
