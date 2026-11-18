using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Urd.Core.Compaction;

namespace Urd.Core.Tools;

// Produces the one result used for both persistence and model context.
internal static class ToolResultPruner {
  private static readonly UTF8Encoding exactUtf8 = new(false, true);
  private const int MinimumRecallTokensSaved = 256;

  internal static async Task<ToolOutput> PruneAsync(string name, JsonElement arguments, ToolOutput output,
      bool canRecall, string? tempDirectory = null) {
    if (output.IsError || output.Images.Count != 0 || name is "read" or "request_capability" || HasSpill(name, output.Details))
      return output;

    ToolOutput fallback = output;
    try {
      string normalized = TextResultRules.Normalize(output.Text);
      if (normalized != output.Text) fallback = output with { Text = normalized };
      string grouped = CommandResultRules.GroupMatches(name, arguments, normalized);
      if (grouped != normalized) return output with { Text = grouped };
      string counted = TextResultRules.CollapseDuplicates(normalized);
      if (counted != normalized) fallback = output with { Text = counted };
      if (!canRecall) return fallback;

      var applied = new List<string>();
      string reduced = CommandResultRules.Summarize(name, arguments, normalized, applied);
      if (applied.Count == 0) return fallback;

      string path = Path.Combine(tempDirectory ?? Path.GetTempPath(), $"urd-result-{Guid.NewGuid():N}.txt");
      string marked = $"[Pruned: {string.Join(", ", applied)}. Read original: {path}]\n{reduced}";
      if (ContextTokenBudget.EstimateText(fallback.Text) - ContextTokenBudget.EstimateText(marked) < MinimumRecallTokensSaved)
        return fallback;
      await SaveOriginalAsync(path, output.Text).ConfigureAwait(false);
      return output with { Text = marked };
    } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or RegexMatchTimeoutException) {
      return fallback;
    }
  }

  private static bool HasSpill(string name, JsonElement? details) {
    if (details is not { ValueKind: JsonValueKind.Object } value) return false;
    string? key = name switch { "bash" => "fullOutputPath", "web_read" => "spillPath", _ => null };
    return key is not null && value.TryGetProperty(key, out JsonElement path) && path.ValueKind == JsonValueKind.String;
  }

  private static async Task SaveOriginalAsync(string path, string text) {
    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    var file = new FileStream(path, options);
    try {
      await using var writer = new StreamWriter(file, exactUtf8);
      await writer.WriteAsync(text).ConfigureAwait(false);
    } catch {
      File.Delete(path);
      throw;
    }
  }
}
