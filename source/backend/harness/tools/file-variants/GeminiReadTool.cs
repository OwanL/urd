namespace Urd.Core.Tools;

internal sealed class GeminiReadTool(AgentTool generic) : ReadFileToolVariant(generic, true) {
  public override string Name => "read_file";
}
