using System.Text.Json;

namespace Urd.Core.Tools;

internal abstract class WriteFileToolVariant(AgentTool generic) : FileToolVariant(generic) {
  public override string Description => "Write UTF-8 content to a file, creating parent directories. Use for new files or complete rewrites.";
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {"type":"object","properties":{
      "file_path":{"type":"string","description":"Path to the file (relative or absolute)."},
      "content":{"type":"string","description":"UTF-8 content to write."}
    },"required":["file_path","content"],"additionalProperties":false}
    """);
  protected override JsonElement Translate(JsonElement arguments) => JsonSerializer.SerializeToElement(new {
    path = ToolArguments.RequireString(arguments, "file_path"),
    content = ToolArguments.RequireString(arguments, "content")
  });
}
