using System.Text.Json;
using Urd.Core.Sessions;

namespace Urd.Core.Tools;

internal sealed class AskUserTool(SessionManager sessions) : HarnessTool {
  public override string Name => "ask_user";
  public override ToolRelevance RelevantWhen => ToolRelevance.Always;
  public override string Description => "Ask the user one focused question. They can pick a suggested answer or write their own. "
      + "Returns only their answer. Set background to true when you can keep working before the answer arrives.";
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "question": { "type": "string", "minLength": 1, "maxLength": 8000 },
        "context": { "type": "string", "maxLength": 8000, "description": "Optional context that helps the user decide." },
        "answers": { "type": "array", "maxItems": 6, "uniqueItems": true,
          "description": "Short suggested answers. Use an empty array for free text only.",
          "items": { "type": "string", "minLength": 1, "maxLength": 8000 } }
      },
      "required": ["question", "answers"],
      "additionalProperties": false
    }
    """);

  public const int MaxTextLength = 8000;

  public override async Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context,
      CancellationToken cancellation) {
    string question = ReadRequiredText(arguments.GetProperty("question").GetString(), "Question");
    string? explanation = arguments.TryGetProperty("context", out JsonElement supplied) ? supplied.GetString() : null;
    if (explanation?.Length > MaxTextLength)
      throw new ArgumentException($"Context must be at most {MaxTextLength} characters.");
    JsonElement options = arguments.GetProperty("answers");
    if (options.GetArrayLength() > 6)
      throw new ArgumentException("Supply at most six suggested answers.");
    string[] answers = options.EnumerateArray().Select(option => ReadRequiredText(option.GetString(), "Answer")).ToArray();
    if (answers.Distinct(StringComparer.Ordinal).Count() != answers.Length)
      throw new ArgumentException("Suggested answers must be distinct.");

    string answer = await sessions.AskUserAsync(context, question, explanation, answers, cancellation);
    return new ToolOutput(answer, []);
  }

  internal static string ReadRequiredText(string? text, string label) =>
      !string.IsNullOrWhiteSpace(text) && text.Length <= MaxTextLength ? text
          : throw new ArgumentException($"{label} must be nonblank and at most {MaxTextLength} characters.");
}
