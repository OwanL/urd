using System.Text.Json;
using Urd.Core.Sessions;

namespace Urd.Core.Tools;

internal sealed class AskUserTool(IUserQuestions userQuestions) : HarnessTool {
  public override string Name => "ask_user";
  public override ToolRelevance RelevantWhen => ToolRelevance.Always;
  public override string Description => "Ask the user one focused question and return their answer verbatim.";
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "question": { "type": "string" },
        "context": { "type": "string", "description": "Optional context that helps the user decide." },
        "choices": { "type": "array", "items": { "type": "string" },
          "description": "Up to six distinct suggested answers. Use an empty array for free text only." }
      },
      "required": ["question", "choices"],
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
    JsonElement choices = arguments.GetProperty("choices");
    if (choices.GetArrayLength() > 6)
      throw new ArgumentException("Supply at most six suggested answers.");
    string[] answers = choices.EnumerateArray().Select(choice => ReadRequiredText(choice.GetString(), "Answer")).ToArray();
    if (answers.Distinct(StringComparer.Ordinal).Count() != answers.Length)
      throw new ArgumentException("Suggested answers must be distinct.");

    string answer = await userQuestions.AskUserAsync(context, question, explanation, answers, cancellation);
    return new ToolOutput(answer, []);
  }

  internal static string ReadRequiredText(string? text, string label) =>
      !string.IsNullOrWhiteSpace(text) && text.Length <= MaxTextLength ? text
          : throw new ArgumentException($"{label} must be nonblank and at most {MaxTextLength} characters.");
}
