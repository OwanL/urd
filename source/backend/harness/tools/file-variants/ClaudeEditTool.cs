namespace Urd.Core.Tools;

internal sealed class ClaudeEditTool(AgentTool generic) : EditFileToolVariant(generic, false) {
  public override string Name => "Edit";
}
