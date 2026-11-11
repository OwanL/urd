using System.Text.Json;
using Urd.Core.Providers;

namespace Urd.Core.Storage;

// Retains the complete secret-free selection, including the original display name.
internal static class LlmSelectionJson {
  private static readonly JsonSerializerOptions Options = new() {
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true
  };

  internal static LlmSelection? Read(string? json)
      => json is null ? null : JsonSerializer.Deserialize<LlmSelection>(json, Options);

  internal static string? Write(LlmSelection? selection)
      => selection is null ? null : JsonSerializer.Serialize(selection);

  // A rename is harmless for steering; every other record member remains binding.
  internal static bool SameOperation(LlmSelection? left, LlmSelection? right) {
    if (left is null || right is null)
      return left == right;

    return left with { Connection = left.Connection with { Name = string.Empty } }
        == right with { Connection = right.Connection with { Name = string.Empty } };
  }
}
