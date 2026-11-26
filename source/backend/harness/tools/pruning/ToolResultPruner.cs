using System.Text.Json;
using System.Text.RegularExpressions;

namespace Urd.Core.Tools;

// Produces the one result used for both persistence and model context.
internal static class ToolResultPruner {
  internal static ToolOutput Prune(string name, JsonElement arguments, ToolOutput output) {
    // Browser diffs depend on the preceding page text being delivered verbatim.
    if (output.IsError || output.Images.Count != 0 || name is "browser" or "read" or "ask_user" || output.Spill is not null)
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

}
