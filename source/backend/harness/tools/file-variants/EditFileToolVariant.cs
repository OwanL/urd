using System.Text.Json;

namespace Urd.Core.Tools;

internal abstract class EditFileToolVariant(AgentTool generic, bool gemini) : FileToolVariant(generic) {
  public override string Description => "Replace one unique match in a UTF-8 file. CRLF and LF are equivalent; no fuzzy matching or replace-all.";
  public override JsonElement ParametersSchema => gemini ? geminiSchema : claudeSchema;
  private static readonly JsonElement claudeSchema = ToolSchema.Parse("""
    {"type":"object","properties":{
      "file_path":{"type":"string","description":"Path to the file (relative or absolute)."},
      "old_string":{"type":"string","minLength":1,"description":"Unique text to replace. CRLF and LF are equivalent."},
      "new_string":{"type":"string","description":"Replacement text."}
    },"required":["file_path","old_string","new_string"],"additionalProperties":false}
    """);
  private static readonly JsonElement geminiSchema = ToolSchema.Parse("""
    {"type":"object","properties":{
      "file_path":{"type":"string","description":"Path to the file (relative or absolute)."},
      "instruction":{"type":"string","description":"Description of the intended change; informational only."},
      "old_string":{"type":"string","minLength":1,"description":"Unique text to replace. CRLF and LF are equivalent."},
      "new_string":{"type":"string","description":"Replacement text."}
    },"required":["file_path","instruction","old_string","new_string"],"additionalProperties":false}
    """);
  protected override JsonElement Translate(JsonElement arguments) {
    string path = ToolArguments.RequireString(arguments, "file_path");
    if (gemini)
      _ = ToolArguments.RequireString(arguments, "instruction");
    string oldText = ToolArguments.RequireString(arguments, "old_string");
    if (oldText.Length == 0)
      throw new ArgumentException("old_string must not be empty.");
    string newText = ToolArguments.RequireString(arguments, "new_string");
    return JsonSerializer.SerializeToElement(new { path, edits = new[] { new { oldText, newText } } });
  }
}
