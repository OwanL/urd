using System.Text.Json;

namespace Urd.Core.Tools;

internal abstract class ReadFileToolVariant(AgentTool generic, bool gemini) : FileToolVariant(generic) {
  public override string Description => "Read UTF-8 text or a supported image. Text is paged and subject to output limits.";
  public override JsonElement ParametersSchema => gemini ? geminiSchema : claudeSchema;
  private static readonly JsonElement claudeSchema = ToolSchema.Parse("""
    {"type":"object","properties":{
      "file_path":{"type":"string","description":"Path to the file (relative or absolute)."},
      "offset":{"type":"integer","minimum":1,"description":"First line, 1-based."},
      "limit":{"type":"integer","minimum":1,"description":"Maximum number of lines."}
    },"required":["file_path"],"additionalProperties":false}
    """);
  private static readonly JsonElement geminiSchema = ToolSchema.Parse("""
    {"type":"object","properties":{
      "file_path":{"type":"string","description":"Path to the file (relative or absolute)."},
      "start_line":{"type":"integer","minimum":1,"description":"First line, 1-based and inclusive."},
      "end_line":{"type":"integer","minimum":1,"description":"Last line, 1-based and inclusive."}
    },"required":["file_path"],"additionalProperties":false}
    """);

  protected override JsonElement Translate(JsonElement arguments) {
    var translated = new Dictionary<string, object> { ["path"] = ToolArguments.RequireString(arguments, "file_path") };
    if (gemini) {
      int start = ToolArguments.OptionalInteger(arguments, "start_line", 1, 1);
      translated["offset"] = start;
      if (arguments.TryGetProperty("end_line", out _)) {
        int end = ToolArguments.OptionalInteger(arguments, "end_line", 1, 1);
        if (end < start)
          throw new ArgumentException("end_line must be greater than or equal to start_line.");
        translated["limit"] = end - start + 1;
      }
    } else {
      if (arguments.TryGetProperty("offset", out _))
        translated["offset"] = ToolArguments.OptionalInteger(arguments, "offset", 1, 1);
      if (arguments.TryGetProperty("limit", out _))
        translated["limit"] = ToolArguments.OptionalInteger(arguments, "limit", 1, 1);
    }
    return JsonSerializer.SerializeToElement(translated);
  }

  protected override Task<ToolOutput> RunTranslated(AgentTool tool, JsonElement arguments, ToolContext context,
      CancellationToken cancellation) => gemini
    ? ((ReadTool)tool).ReadAsync(arguments, context, "start_line", cancellation)
    : tool.RunAsync(arguments, context, cancellation);
}
