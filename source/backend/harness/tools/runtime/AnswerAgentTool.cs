using System.Text.Json;
using Urd.Core.Sessions;

namespace Urd.Core.Tools;

// Answers the question currently presented to the calling agent; the asker receives this text
// as its ask result. Answering continues the recipient's own task.
internal sealed class AnswerAgentTool : HarnessTool {
  public override string Name => "answer_agent";
  public override ToolRelevance RelevantWhen => ToolRelevance.Always;
  public override string Description => "Answer the question another agent asked you; its asker receives this text as the ask result.";
  public override JsonElement ParametersSchema { get; } = ToolSchema.Parse("""
    {
      "type": "object",
      "properties": {
        "answer": { "type": "string" }
      },
      "required": ["answer"],
      "additionalProperties": false
    }
    """);

  public override Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context, CancellationToken cancellation) {
    string answer = arguments.GetProperty("answer").GetString()
        ?? throw new ArgumentException("Answer is required.");
    bool answered = context.Sessions.AnswerAgent(context, answer);
    return Task.FromResult(answered
        ? new ToolOutput("The answer was delivered.", [])
        : new ToolOutput("There is no current question to answer.", [], IsError: true));
  }
}
