using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Tools;

namespace Urd.Core.Execution;

// Invokes foreground and background tools, saves their results, and drains background work.
internal sealed class SessionToolExecution {
  private readonly SessionBase session;
  private readonly SessionExecutionRecorder recorder;
  private readonly IReadOnlyList<AgentTool> tools; // Frozen offered capabilities for this execution.
  private readonly PendingToolCalls pendingToolCalls; // Shared registry; only this execution's entries are managed here.
  private readonly CancellationToken stopToken; // Borrowed; SessionExecution owns cancellation and disposal.
  private readonly bool supportsImages;
  private int runningBackgroundTools;

  internal SessionToolExecution(SessionBase session, SessionExecutionRecorder recorder,
      IReadOnlyList<AgentTool> tools, PendingToolCalls pendingToolCalls, bool supportsImages,
      CancellationToken stopToken) {
    this.session = session;
    this.recorder = recorder;
    this.tools = tools;
    this.pendingToolCalls = pendingToolCalls;
    this.stopToken = stopToken;
    this.supportsImages = supportsImages;
  }

  private Guid ExecutionId => recorder.Identity.ExecutionId;
  private bool IsStopping => stopToken.IsCancellationRequested;
  internal bool HasRunningBackgroundTools => Volatile.Read(ref runningBackgroundTools) != 0;
  internal void CancelBackgroundTools() => pendingToolCalls.RequestCancelExecution(ExecutionId);
  internal Task JoinBackgroundToolsAsync() => pendingToolCalls.JoinExecutionAsync(ExecutionId);

  internal async Task<ExecutionOutcome> RunBatchAsync(Guid requestId, IReadOnlyList<LlmToolCall> calls,
      IReadOnlyList<AIFunctionDeclaration> offeredDefinitions) {
    var foregroundTasks = new List<Task>(calls.Count);
    var failures = new List<Exception>();
    try {
      foreach (LlmToolCall call in calls) {
        if (IsStopping)
          break;
        bool runInBackground = call.Arguments.TryGetProperty("background", out JsonElement option)
            && option.ValueKind == JsonValueKind.True;
        if (!await recorder.TryStartToolAsync(requestId, call.CallId, runInBackground)) {
          if (IsStopping)
            break;
          throw new InvalidOperationException("The tool call could not be started.");
        }

        // Invoke immediately after the durable claim, before claiming the next call. File tools
        // admit to their shared path queue synchronously, preserving emitted order without Task.Run.
        if (runInBackground)
          StartBackgroundTool(requestId, call, offeredDefinitions);
        else
          foregroundTasks.Add(InvokeAndSaveToolAsync(requestId, call, offeredDefinitions, stopToken));
      }
    } catch (Exception failure) {
      failures.Add(failure);
      await session.ReportToolOutcomeAsync(ExecutionId, failure);
    }

    // Even a failed start/save/encoding must join all launched siblings before fencing.
    Task foregroundCompletion = Task.WhenAll(foregroundTasks);
    try {
      await foregroundCompletion;
    } catch (Exception failure) {
      failures.Add(foregroundCompletion.Exception ?? failure);
    }
    if (failures.Count != 0)
      throw new AggregateException("The tool batch could not be completed safely.", failures);

    return IsStopping ? ExecutionOutcome.Cancelled : ExecutionOutcome.Completed;
  }

  private void StartBackgroundTool(Guid requestId, LlmToolCall call,
      IReadOnlyList<AIFunctionDeclaration> offeredDefinitions) {
    // The registry is the only cancellation owner; linked-source callbacks would bypass its join.
    var cancellation = new CancellationTokenSource();
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Interlocked.Increment(ref runningBackgroundTools);
    try {
      pendingToolCalls.Add(ExecutionId, requestId, call, completion.Task, cancellation,
          failure => session.ReportToolOutcomeAsync(ExecutionId, failure));
    } catch {
      Interlocked.Decrement(ref runningBackgroundTools);
      cancellation.Dispose();
      throw;
    }
    // Stop may have raced the durable start before registration. Cancel the newly owned
    // source before invocation; later Stops see it in the registry.
    if (IsStopping)
      CancelBackgroundTools();
    // Registration precedes invocation, including tools that finish synchronously.
    _ = RunBackgroundToolAsync(requestId, call, offeredDefinitions, completion, cancellation.Token);
  }

  private async Task RunBackgroundToolAsync(Guid requestId, LlmToolCall call,
      IReadOnlyList<AIFunctionDeclaration> offeredDefinitions, TaskCompletionSource completion,
      CancellationToken cancellation) {
    Exception? failure = null;
    try {
      await InvokeAndSaveToolAsync(requestId, call, offeredDefinitions, cancellation);
    } catch (Exception error) {
      failure = error;
    }
    // Update the wait predicate before signalling under the session gate. The registry
    // still owns this task and all callback cleanup, so settlement must join it outside the gate.
    Interlocked.Decrement(ref runningBackgroundTools);
    try {
      await session.ReportToolOutcomeAsync(ExecutionId, failure);
    } catch (Exception error) {
      failure = failure is null ? error : new AggregateException(failure, error);
    }
    if (failure is null)
      completion.TrySetResult();
    else
      completion.TrySetException(failure);
  }

  private async Task InvokeAndSaveToolAsync(Guid requestId, LlmToolCall call,
      IReadOnlyList<AIFunctionDeclaration> offeredDefinitions, CancellationToken cancellation) {
    try {
      LlmToolResult result = await InvokeToolAsync(requestId, call, offeredDefinitions, cancellation);
      // A rejected or failed save leaves effects uncertain, never an ordinary tool error.
      if (!await recorder.CompleteToolAsync(requestId, result))
        throw new InvalidOperationException("The tool result could not be saved.");
    } catch (Exception failure) {
      await session.ReportToolOutcomeAsync(ExecutionId, failure);
      throw;
    }
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

  private async Task<LlmToolResult> InvokeToolAsync(Guid requestId, LlmToolCall call,
      IReadOnlyList<AIFunctionDeclaration> offeredDefinitions, CancellationToken cancellation) {
    ToolOutput output;
    try {
      cancellation.ThrowIfCancellationRequested();
      AgentTool tool = tools.FirstOrDefault(tool =>
          string.Equals(tool.Name, call.Name, StringComparison.Ordinal))
          ?? throw new InvalidOperationException($"Unknown tool '{call.Name}'.");
      AIFunctionDeclaration? offered = offeredDefinitions.FirstOrDefault(declaration =>
          string.Equals(declaration.Name, call.Name, StringComparison.Ordinal));
      AIFunctionDeclaration current = AgentToolDeclaration.Create(tool);
      if (offered is null || offered.Description != current.Description
          || offered.JsonSchema.GetRawText() != current.JsonSchema.GetRawText())
        throw new InvalidOperationException($"Tool '{call.Name}' no longer matches its offered definition.");
      string directory = session.WorkingDirectory
          ?? throw new InvalidOperationException("The chat has no saved working folder.");
      // Progress is intentionally inert until transcript progress integration is available.
      var context = new ToolContext(directory, requestId, call.CallId, supportsImages, static _ => { });
      output = await tool.RunAsync(StripBackgroundOption(call.Arguments), context, cancellation);
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

  private static LlmToolResult CreateToolErrorResult(LlmToolCall call, ExecutionOutcome outcome, string message) {
    // Bound even arbitrary exception messages, allowing for JSON escaping overhead.
    string error = message[..Math.Min(message.Length, 1024)];
    return new(call.CallId, outcome, new ToolOutput(error, [], IsError: true));
  }
}
