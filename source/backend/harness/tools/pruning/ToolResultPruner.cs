using System.Text.Json;
using System.Text.RegularExpressions;

namespace Urd.Core.Tools;

// Produces the one result used for both persistence and model context.
internal static class ToolResultPruner {
  internal static ToolOutput Prune(string name, JsonElement arguments, ToolOutput output) {
    if (output.IsError || output.Images.Count != 0 || name is "read" or "request_capability" or "ask_user" || HasSpill(name, output.Details))
      return output;

    try {
      string normalized = TextResultRules.Normalize(output.Text);
      string reduced = CommandResultRules.GroupMatches(name, arguments, normalized);
      if (reduced == normalized) reduced = TextResultRules.CollapseDuplicates(normalized);
      return reduced == output.Text ? output : output with { Text = reduced };
    } catch (RegexMatchTimeoutException) {
      return output;
    }
  }

  private static bool HasSpill(string name, JsonElement? details) {
    if (details is not { ValueKind: JsonValueKind.Object } value) return false;
    string? key = name switch { "bash" => "fullOutputPath", "web_read" => "spillPath", _ => null };
    return key is not null && value.TryGetProperty(key, out JsonElement path) && path.ValueKind == JsonValueKind.String;
  }
}
