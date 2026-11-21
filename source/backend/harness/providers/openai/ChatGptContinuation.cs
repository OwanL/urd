using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Urd.Core.Providers;

internal sealed class ChatGptContinuation {
  private byte[]? digest;
  private int count;
  private string? responseId;
  private IncrementalHash? pending;
  private int pendingCount;

  internal byte[] CreateFrame(JsonElement request) {
    try {
      if (pending is not null)
        Reset();
      if (request.ValueKind != JsonValueKind.Object)
        throw new ArgumentException("Request must be an object.", nameof(request));

      JsonElement input = default;
      int inputs = 0;
      using IncrementalHash prefix = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      pending = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      foreach (JsonProperty property in request.EnumerateObject()) {
        if (property.NameEquals("input")) {
          input = property.Value;
          inputs++;
          continue;
        }
        if (property.NameEquals("type") || property.NameEquals("previous_response_id"))
          throw new ArgumentException("Request contains a reserved property.", nameof(request));
        Add(pending, prefix, 1, Encoding.UTF8.GetBytes(property.Name));
        Add(pending, prefix, 2, Encoding.UTF8.GetBytes(property.Value.GetRawText()));
      }
      if (inputs != 1 || input.ValueKind != JsonValueKind.Array)
        throw new ArgumentException("Request must contain one input array.", nameof(request));

      int length = input.GetArrayLength();
      int index = 0;
      foreach (JsonElement item in input.EnumerateArray()) {
        byte[] canonical = Canonical(item);
        Add(pending, index++ < count ? prefix : null, 3, canonical);
      }
      bool append = digest is not null && responseId is not null && length > count &&
          CryptographicOperations.FixedTimeEquals(digest, prefix.GetHashAndReset());
      string? previousId = append ? responseId : null;
      int previousCount = count;
      // A checkpoint cannot survive an exchange that does not complete.
      digest = null;
      responseId = null;
      count = 0;
      pendingCount = length;

      using var stream = new MemoryStream();
      using (var writer = new Utf8JsonWriter(stream)) {
        writer.WriteStartObject();
        writer.WriteString("type", "response.create");
        foreach (JsonProperty property in request.EnumerateObject()) {
          if (!property.NameEquals("input") || !append) {
            property.WriteTo(writer);
            continue;
          }
          writer.WritePropertyName("input");
          writer.WriteStartArray();
          index = 0;
          foreach (JsonElement item in input.EnumerateArray()) {
            if (index++ >= previousCount)
              item.WriteTo(writer);
          }
          writer.WriteEndArray();
        }
        if (append)
          writer.WriteString("previous_response_id", previousId);
        writer.WriteEndObject();
      }
      return stream.ToArray();
    } catch {
      Reset();
      throw;
    }
  }

  internal void Complete(JsonElement response) {
    try {
      if (pending is null || response.ValueKind != JsonValueKind.Object ||
          !response.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String ||
          string.IsNullOrEmpty(id.GetString()) ||
          !response.TryGetProperty("status", out JsonElement status) ||
          status.ValueKind != JsonValueKind.String || status.GetString() != "completed" ||
          !response.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array ||
          output.GetArrayLength() == 0) {
        Reset();
        return;
      }
      foreach (JsonElement item in output.EnumerateArray())
        Add(pending, null, 3, Canonical(item));
      digest = pending.GetHashAndReset();
      count = checked(pendingCount + output.GetArrayLength());
      responseId = id.GetString();
      pending.Dispose();
      pending = null;
      pendingCount = 0;
    } catch {
      Reset();
      throw;
    }
  }

  internal void Reset() {
    pending?.Dispose();
    pending = null;
    pendingCount = 0;
    digest = null;
    count = 0;
    responseId = null;
  }

  private static byte[] Canonical(JsonElement item) {
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
      WriteCanonical(writer, item);
    return stream.ToArray();
  }

  private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value) {
    switch (value.ValueKind) {
      case JsonValueKind.Object:
        writer.WriteStartObject();
        foreach (JsonProperty property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)) {
          writer.WritePropertyName(property.Name);
          WriteCanonical(writer, property.Value);
        }
        writer.WriteEndObject();
        break;
      case JsonValueKind.Array:
        writer.WriteStartArray();
        foreach (JsonElement item in value.EnumerateArray())
          WriteCanonical(writer, item);
        writer.WriteEndArray();
        break;
      case JsonValueKind.String:
        writer.WriteStringValue(value.GetString());
        break;
      default:
        value.WriteTo(writer);
        break;
    }
  }

  private static void Add(IncrementalHash hash, IncrementalHash? other, byte tag, byte[] bytes) {
    Span<byte> header = stackalloc byte[1 + sizeof(int)];
    header[0] = tag;
    BinaryPrimitives.WriteInt32BigEndian(header[1..], bytes.Length);
    hash.AppendData(header);
    hash.AppendData(bytes);
    other?.AppendData(header);
    other?.AppendData(bytes);
  }
}
