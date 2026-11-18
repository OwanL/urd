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
      Func<string, bool, HarnessToolContext>? createHarnessContext = null) {
    ToolOutput output;
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
      JsonElement arguments = StripBackgroundOption(call.Arguments);
      if (tool is HarnessTool harnessTool) {
        HarnessToolContext context = createHarnessContext?.Invoke(call.CallId, background)
            ?? throw new InvalidOperationException("Harness tools require a session execution.");
        output = await harnessTool.RunAsync(arguments, context with { RequestId = requestId }, cancellation);
      } else {
        string directory = workingDirectory ?? (tool is WebSearchTool or WebReadTool or ListPendingCallsTool or CancelPendingCallsTool
            ? string.Empty : throw new InvalidOperationException("The chat has no saved working folder."));
        // Progress is intentionally inert until transcript progress integration is available.
        var context = new ToolContext(directory, requestId, call.CallId, supportsImages, static _ => { });
        output = await tool.RunAsync(arguments, context, cancellation);
      }
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      return CreateToolErrorResult(call, ExecutionOutcome.Cancelled,
          "Tool execution was cancelled; effects may have occurred.");
    } catch (Exception exception) when (exception is not ToolCleanupException) {
      return CreateToolErrorResult(call, ExecutionOutcome.Failed, exception.Message);
    }

    // Storage freezes output and publishes images outside the ordinary invocation-error catch.
    // A tool may return an error instead of throwing after cancellation; Stop still takes priority.
    ExecutionOutcome outcome = output.IsError ? ExecutionOutcome.Failed : ExecutionOutcome.Completed;
    if (cancellation.IsCancellationRequested)
      outcome = ExecutionOutcome.Cancelled;
    return new(call.CallId, outcome, output);
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

  private static LlmToolResult CreateToolErrorResult(LlmToolCall call, ExecutionOutcome outcome, string message) {
    // Bound even arbitrary exception messages, allowing for JSON escaping overhead.
    string error = message[..Math.Min(message.Length, 1024)];
    return new(call.CallId, outcome, new ToolOutput(error, [], IsError: true));
  }
}
