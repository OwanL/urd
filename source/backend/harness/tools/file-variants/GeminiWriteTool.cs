namespace Urd.Core.Tools;

internal sealed class GeminiWriteTool(AgentTool generic) : WriteFileToolVariant(generic) {
  public override string Name => "write_file";
}
