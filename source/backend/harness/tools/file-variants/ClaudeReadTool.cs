namespace Urd.Core.Tools;

internal sealed class ClaudeReadTool(AgentTool generic) : ReadFileToolVariant(generic, false) {
  public override string Name => "Read";
}
