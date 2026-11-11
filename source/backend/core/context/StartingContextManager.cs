using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Tools;

namespace Urd.Core.Context;

// Composes the initial model instructions and available tools without loading external context.
internal sealed class StartingContextManager {
  private const string BaseInstructions = "You are Urd, a general-purpose assistant. Help with the user's request, be accurate, and make uncertainty clear.";

  private readonly ToolCatalog toolCatalog;

  internal StartingContextManager(ToolCatalog toolCatalog) {
    this.toolCatalog = toolCatalog;
  }

  internal Task<StartingContext> CreateAsync(StartingContextInput input, CancellationToken cancellation) {
    if (cancellation.IsCancellationRequested)
      return Task.FromCanceled<StartingContext>(cancellation);

    string sessionInstructions = input.SessionKind switch {
      SessionKind.Primary => "Help the user directly and keep their request and goals central.",
      SessionKind.Subagent => "Complete only the assigned task, stay within its scope, and return a concise result with findings, verification, and blockers.",
      _ => throw new ArgumentOutOfRangeException(nameof(input), input.SessionKind, "Unsupported session kind.")
    };

    var instructions = new List<ChatMessage> {
      new(ChatRole.System, BaseInstructions),
      new(ChatRole.System, sessionInstructions)
    };
    if (input.WorkingDirectory is { } workingDirectory)
      instructions.Add(new(ChatRole.System, $"Working directory: {workingDirectory}"));
    if (!string.IsNullOrWhiteSpace(input.AssignmentContext))
      instructions.Add(new(ChatRole.System, $"Assignment context:\n{input.AssignmentContext}"));

    AIFunctionDeclaration[] tools = toolCatalog.For(input.ModelFamily)
        .Select(AgentToolDeclaration.Create)
        .ToArray();
    return Task.FromResult(new StartingContext(
        Array.AsReadOnly(instructions.ToArray()), Array.AsReadOnly(tools)));
  }
}
