using System.Text.Json;

namespace Urd.Core.Context;

// Reads the selector model's {"keptIds": [...]} answer.
internal static class PruningAnswer {
  // Ids that were not offered are ignored. Anything other than exactly one keptIds string array is invalid.
  internal static HashSet<string> ReadKeptIds(string answer, IEnumerable<string> offeredIds) {
    using JsonDocument document = JsonDocument.Parse(answer);
    JsonElement root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
      throw InvalidAnswer();

    JsonElement[] keptIdProperties = root.EnumerateObject()
        .Where(property => property.NameEquals("keptIds")).Select(property => property.Value).ToArray();
    if (keptIdProperties is not [{ ValueKind: JsonValueKind.Array } keptIds])
      throw InvalidAnswer();

    var offered = new HashSet<string>(offeredIds, StringComparer.Ordinal);
    var kept = new HashSet<string>(StringComparer.Ordinal);
    foreach (JsonElement value in keptIds.EnumerateArray()) {
      if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } id)
        throw InvalidAnswer();
      if (offered.Contains(id))
        kept.Add(id);
    }
    return kept;
  }

  private static InvalidDataException InvalidAnswer() =>
      new("The pruning response must contain exactly one keptIds string array.");
}
