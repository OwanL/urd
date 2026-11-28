using Microsoft.Extensions.AI;
using Urd.Core.Providers;

namespace Urd.Core.Tools;

// Each slot is one capability, with one model-facing tool per family. Saved names identify
// the original surface even when a later execution uses a different family.
internal sealed class ToolCatalog {
  private readonly Dictionary<string, (int Slot, AgentTool Surface)> names = new(StringComparer.Ordinal);
  private readonly IReadOnlyList<AgentTool> anthropic;
  private readonly IReadOnlyList<AgentTool> google;
  public IReadOnlyList<AgentTool> Tools { get; }

  internal ToolCatalog(IReadOnlyList<AgentTool> tools) {
    ArgumentNullException.ThrowIfNull(tools);
    Tools = Array.AsReadOnly(tools.ToArray());
    for (int i = 0; i < Tools.Count; i++)
      if (!names.TryAdd(Tools[i].Name, (i, Tools[i])))
        throw new ArgumentException("Tool names must be unique.", nameof(tools));
    anthropic = CreateVariants(ModelFamily.Anthropic);
    google = CreateVariants(ModelFamily.Google);
  }

  internal IReadOnlyList<AgentTool> ForFamily(ModelFamily family) => family switch {
    ModelFamily.Anthropic => anthropic,
    ModelFamily.Google => google,
    _ => Tools
  };

  internal string CapabilityName(string name) => names.TryGetValue(name, out var entry) ? Tools[entry.Slot].Name : name;

  // Preserve order, pruning and frozen same-name declarations. Never revive a removed tool
  // or give a stale declaration new meaning simply because the caller switched families.
  internal IReadOnlyList<AIFunctionDeclaration> Restore(IReadOnlyList<AIFunctionDeclaration> saved, ModelFamily family) {
    IReadOnlyList<AgentTool> selected = ForFamily(family);
    var restored = new List<AIFunctionDeclaration>(saved.Count);
    foreach (AIFunctionDeclaration definition in saved) {
      if (!names.TryGetValue(definition.Name, out var entry))
        continue;
      AgentTool target = selected[entry.Slot];
      if (target.Name == definition.Name)
        restored.Add(definition);
      else if (AgentToolDeclaration.Matches(entry.Surface, definition))
        restored.Add(AgentToolDeclaration.Create(target));
    }
    return restored.AsReadOnly();
  }

  private IReadOnlyList<AgentTool> CreateVariants(ModelFamily family) {
    AgentTool[]? variants = null;
    for (int i = 0; i < Tools.Count; i++) {
      AgentTool tool = FileToolVariants.For(Tools[i], family);
      if (ReferenceEquals(tool, Tools[i]))
        continue;
      if (!names.TryAdd(tool.Name, (i, tool)))
        throw new ArgumentException($"Duplicate tool name: {tool.Name}.");
      (variants ??= Tools.ToArray())[i] = tool;
    }
    return variants is null ? Tools : Array.AsReadOnly(variants);
  }
}
