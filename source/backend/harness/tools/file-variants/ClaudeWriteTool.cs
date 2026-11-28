namespace Urd.Core.Tools;

internal sealed class ClaudeWriteTool(AgentTool generic) : WriteFileToolVariant(generic) {
  public override string Name => "Write";
}
