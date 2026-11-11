using System.Collections.ObjectModel;
using Urd.Core.Providers;

namespace Urd.Core.Tools;

// The configured generic capabilities; family-specific names can replace them later.
internal sealed class ToolCatalog {
  private readonly ReadOnlyCollection<AgentTool> tools;

  internal ToolCatalog(IReadOnlyList<AgentTool> tools) {
    ArgumentNullException.ThrowIfNull(tools);
    this.tools = Array.AsReadOnly(tools.ToArray());
    if (this.tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != this.tools.Count)
      throw new ArgumentException("Tool names must be unique.", nameof(tools));
  }

  public IReadOnlyList<AgentTool> For(ModelFamily family) {
    _ = family; // Version one offers the same generic capabilities to every family.
    return tools;
  }
}
