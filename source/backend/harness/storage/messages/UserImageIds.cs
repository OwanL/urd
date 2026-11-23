using System.Text.Json;

namespace Urd.Core.Storage;

internal static class UserImageIds {
  internal static IReadOnlyList<Guid>? Read(string? json) => json is null ? null : Array.AsReadOnly(
      JsonSerializer.Deserialize<Guid[]>(json) ?? throw new InvalidDataException("Stored image IDs are invalid."));

  internal static string? Write(IReadOnlyList<Guid>? ids) => ids is { Count: > 0 } ? JsonSerializer.Serialize(ids) : null;
}
