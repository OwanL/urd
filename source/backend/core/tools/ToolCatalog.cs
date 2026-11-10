using Urd.Core.Providers;

namespace Urd.Core.Tools;

// The configured generic capabilities; family-specific names can replace them later.
internal sealed class ToolCatalog {
  private readonly IReadOnlyList<AgentTool> tools;
  private readonly Dictionary<string, AgentTool> byName;

  internal ToolCatalog(IReadOnlyList<AgentTool> tools) {
    ArgumentNullException.ThrowIfNull(tools);
    this.tools = Array.AsReadOnly(tools.ToArray());
    byName = this.tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
  }

  public IReadOnlyList<AgentTool> For(ModelFamily family) {
    _ = family; // Version one offers the same generic capabilities to every family.
    return tools;
  }

  public AgentTool? Find(string name) => byName.GetValueOrDefault(name);
}
