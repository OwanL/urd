using Urd.Core.Providers;

namespace Urd.Core.Tools;

// Wrap only the built-in file tools; other tools retain their identity and behavior.
internal static class FileToolVariants {
  internal static AgentTool For(AgentTool generic, ModelFamily family) => (generic, family) switch {
    (ReadTool, ModelFamily.Anthropic) => new ClaudeReadTool(generic),
    (WriteTool, ModelFamily.Anthropic) => new ClaudeWriteTool(generic),
    (EditTool, ModelFamily.Anthropic) => new ClaudeEditTool(generic),
    (ReadTool, ModelFamily.Google) => new GeminiReadTool(generic),
    (WriteTool, ModelFamily.Google) => new GeminiWriteTool(generic),
    (EditTool, ModelFamily.Google) => new GeminiReplaceTool(generic),
    _ => generic
  };
}
