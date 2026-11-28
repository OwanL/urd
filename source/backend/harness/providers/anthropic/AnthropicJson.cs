using System.Text.Json;

namespace Urd.Core.Providers;

internal static class AnthropicJson {
  internal static readonly JsonSerializerOptions Strict = new() { AllowDuplicateProperties = false };

  internal static string? ReadString(JsonElement value, string name) =>
      value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) &&
      field.ValueKind == JsonValueKind.String ? field.GetString() : null;

  internal static string RequiredString(JsonElement value, string name, bool allowEmpty = false) {
    string? field = ReadString(value, name);
    if (field is null || (!allowEmpty && string.IsNullOrWhiteSpace(field)))
      throw Invalid("missing or invalid protocol string");
    return field;
  }

  internal static ProviderOperationException Invalid(string reason) => new(ProviderFailure.InvalidProviderResponse, reason);
}
