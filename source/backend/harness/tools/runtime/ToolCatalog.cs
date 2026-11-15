namespace Urd.Core.Tools;

// The frozen generic capabilities offered to every model family.
internal sealed class ToolCatalog {
  public IReadOnlyList<AgentTool> Tools { get; }

  internal ToolCatalog(IReadOnlyList<AgentTool> tools) {
    ArgumentNullException.ThrowIfNull(tools);
    Tools = Array.AsReadOnly(tools.ToArray());
    if (Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != Tools.Count)
      throw new ArgumentException("Tool names must be unique.", nameof(tools));
  }
}
