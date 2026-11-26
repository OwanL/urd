using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
using Urd.Core.Usage;

namespace Urd.Core.Context;

// The JSON candidate list shown to the selector model. Files are grouped by directory and carry
// short text (instruction content or skill description) within a shared byte budget.
internal static class PruningManifest {
  private const int TextBudgetBytes = 16 * 1024;
  private const int MaxTextBytes = 2 * 1024;

  internal static object[] ForInstructions(IReadOnlyList<PruningCandidate<DiscoveredInstructionDocument>> candidates) {
    // Identical files are worth flagging even when their content is omitted.
    Dictionary<string, InlineText> texts = AllocateText(
        candidates.Select(candidate => (candidate.ModelId, candidate.Item.Text)),
        "Content", "ContentRef", referToOmittedText: true);
    return GroupByDirectory(candidates, document => document.Path, candidate => {
      DiscoveredInstructionDocument document = candidate.Item;
      InlineText text = texts[candidate.ModelId];
      return new InstructionEntry(candidate.ModelId, Path.GetFileName(document.Path),
          Encoding.UTF8.GetByteCount(document.Text), document.Scope, text.Value, text.Reference);
    });
  }

  internal static object[] ForSkills(IReadOnlyList<PruningCandidate<DiscoveredSkill>> candidates) {
    Dictionary<string, InlineText> texts = AllocateText(
        candidates.Select(candidate => (candidate.ModelId, candidate.Item.Description)),
        "Description", "DescriptionRef", referToOmittedText: false);
    return GroupByDirectory(candidates, skill => skill.Path, candidate => {
      DiscoveredSkill skill = candidate.Item;
      InlineText text = texts[candidate.ModelId];
      return new SkillEntry(candidate.ModelId, Path.GetFileName(skill.Path), skill.SizeBytes, skill.Name,
          text.Value, text.Reference);
    });
  }

  internal static object[] ForTools(IReadOnlyList<PruningCandidate<AIFunctionDeclaration>> candidates) => candidates
      .Select(candidate => (object)new ToolEntry(candidate.ModelId, "tool", candidate.Item.Name,
          candidate.Item.Description, candidate.Item.JsonSchema))
      .ToArray();

  internal static string Serialize(object[] manifest) => JsonSerializer.Serialize(manifest);

  // Counts the candidate JSON only: there is no user task, model or provider framing to include.
  internal static int EstimateTokens(object[] manifest) {
    string json = Serialize(manifest);
    return (int)Math.Min(int.MaxValue, O200kTokens.Estimate(json, json.Length) ?? ContextTokenBudget.EstimateText(json));
  }

  private static object[] GroupByDirectory<T>(IEnumerable<PruningCandidate<T>> candidates, Func<T, string> path,
      Func<PruningCandidate<T>, object> createEntry) => candidates
      .GroupBy(candidate => Path.GetDirectoryName(path(candidate.Item)) ?? "")
      .Select(directory => (object)new DirectoryEntry(directory.Key, directory.Select(createEntry).ToArray()))
      .ToArray();

  // Inlines the shortest texts first until the budget is spent. A text seen before refers to the first
  // candidate with it instead of repeating it. Each property costs its serialized value plus a comma,
  // quoted name and colon.
  private static Dictionary<string, InlineText> AllocateText(IEnumerable<(string Id, string Text)> candidates,
      string textProperty, string referenceProperty, bool referToOmittedText) {
    var allocated = new Dictionary<string, InlineText>(StringComparer.Ordinal);
    var firstIdByText = new Dictionary<string, string>(StringComparer.Ordinal);
    int remaining = TextBudgetBytes;

    bool TrySpend(string property, int valueBytes) {
      int cost = property.Length + 4 + valueBytes;
      if (cost > remaining)
        return false;
      remaining -= cost;
      return true;
    }

    var bySize = candidates.Select(candidate => (candidate.Id, candidate.Text, Bytes: InlineBytes(candidate.Text)))
        .OrderBy(candidate => candidate.Bytes);
    foreach (var (id, text, bytes) in bySize) {
      if (firstIdByText.TryGetValue(text, out string? firstId)) {
        bool referenced = TrySpend(referenceProperty, SerializedBytes(firstId));
        allocated.Add(id, referenced ? new(null, firstId) : default);
        continue;
      }

      bool inlined = bytes <= MaxTextBytes && TrySpend(textProperty, bytes);
      allocated.Add(id, inlined ? new(text, null) : default);
      if (inlined || referToOmittedText)
        firstIdByText.Add(text, id);
    }
    return allocated;
  }

  // Text length is a lower bound on serialized bytes, so oversized text needs no serialization.
  private static int InlineBytes(string text) => text.Length > MaxTextBytes ? int.MaxValue : SerializedBytes(text);

  private static int SerializedBytes(string value) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value));

  // A candidate's own text, a reference to an identical candidate's text, or neither.
  private readonly record struct InlineText(string? Value, string? Reference);

  // Property names below are the JSON the selector model reads.
  private sealed record DirectoryEntry(string Directory, object[] Files);
  private sealed record InstructionEntry(string Id, string File, long SizeBytes, string Scope,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentRef);
  private sealed record SkillEntry(string Id, string File, long? SizeBytes, string Name,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
      [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DescriptionRef);
  private sealed record ToolEntry(string Id, string Type, string Name, string? Description, JsonElement Schema);
}
