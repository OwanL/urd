namespace Urd.Core.Tools;

internal sealed class GeminiReplaceTool(AgentTool generic) : EditFileToolVariant(generic, true) {
  public override string Name => "replace";
}
