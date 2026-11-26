using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Tools;

namespace Urd.Core.Execution;

// Invokes one call using only the capabilities and model definitions supplied by its owner.
internal static class ToolInvoker {
  internal static async Task<LlmToolResult> InvokeAsync(Guid requestId, LlmToolCall call,
      IReadOnlyList<AgentTool> allowedTools, IReadOnlyList<AIFunctionDeclaration> offeredDefinitions,
      string? workingDirectory, bool supportsImages, bool background, CancellationToken cancellation,
      ContextStamp? contextStamp = null, Func<string, bool, HarnessToolContext>? createHarnessContext = null) {
    ToolOutput output;
    ExecutionOutcome? failure = null;
    try {
      cancellation.ThrowIfCancellationRequested();
      AgentTool tool = allowedTools.FirstOrDefault(tool =>
          string.Equals(tool.Name, call.Name, StringComparison.Ordinal))
          ?? throw new InvalidOperationException($"Unknown tool '{call.Name}'.");
      AIFunctionDeclaration? offered = offeredDefinitions.FirstOrDefault(declaration =>
          string.Equals(declaration.Name, call.Name, StringComparison.Ordinal));
      AIFunctionDeclaration current = AgentToolDeclaration.Create(tool);
      if (offered is null || offered.Description != current.Description
          || offered.JsonSchema.GetRawText() != current.JsonSchema.GetRawText())
        throw new InvalidOperationException($"Tool '{call.Name}' no longer matches its offered definition.");
      JsonElement arguments = tool.SupportsBackgroundOption ? StripBackgroundOption(call.Arguments) : call.Arguments;
      if (tool is HarnessTool harnessTool) {
        HarnessToolContext context = createHarnessContext?.Invoke(call.CallId, background)
            ?? throw new InvalidOperationException("Harness tools require a session execution.");
        output = await harnessTool.RunAsync(arguments,
            context with { RequestId = requestId, BrowserContextStamp = contextStamp }, cancellation);
      } else {
        string directory = workingDirectory ?? (tool.RequiresWorkingDirectory
            ? throw new InvalidOperationException("The chat has no saved working folder.") : string.Empty);
        // Progress is intentionally inert until transcript progress integration is available.
        var context = new ToolContext(directory, requestId, call.CallId, supportsImages, static _ => { });
        output = await tool.RunAsync(arguments, context, cancellation);
      }
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      output = new ToolOutput("Tool execution was cancelled; effects may have occurred.", [], IsError: true);
      failure = ExecutionOutcome.Cancelled;
    } catch (Exception exception) when (exception is not ToolCleanupException) {
      output = new ToolOutput(exception.Message, [], IsError: true);
      failure = ExecutionOutcome.Failed;
    }

    // Spill before pruning so the saved text is complete, including errors and mixed image results.
    // A spill failure is a recording fault, not a second tool error that hides the original result.
    ToolOutput limited = await ToolOutputLimit.ApplyAsync(call.Name, output);
    if (ReferenceEquals(limited, output) && !cancellation.IsCancellationRequested)
      limited = ToolResultPruner.Prune(call.Name, call.Arguments, limited);
    ExecutionOutcome outcome = failure ?? (limited.IsError ? ExecutionOutcome.Failed : ExecutionOutcome.Completed);
    if (cancellation.IsCancellationRequested)
      outcome = ExecutionOutcome.Cancelled;
    return new(call.CallId, outcome, limited);
  }

  private static JsonElement StripBackgroundOption(JsonElement arguments) {
    if (!arguments.TryGetProperty("background", out JsonElement option))
      return arguments;
    if (option.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
      throw new ArgumentException("The background option must be a boolean.", nameof(arguments));
    JsonObject values = JsonNode.Parse(arguments.GetRawText())!.AsObject();
    values.Remove("background");
    return JsonSerializer.SerializeToElement(values);
  }
}
