using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Urd.Core.Context;
using Urd.Core.Sessions;
using Urd.Core.Providers;
using Urd.Core.Tools;

namespace Urd.Core.Execution;

// Sends an execution's requests to the LLM and joins each authorized parallel tool batch.
// The session owns locking and storage; this class owns the LLM call and its cleanup.
internal sealed class SessionExecution : IDisposable {
  private readonly SessionBase session;
  private readonly Func<ProviderConnection, IChatClient> createChatClient; // Returns borrowed clients.
  private readonly LlmSelection selection;
  private readonly StartingContextManager startingContextManager;
  private StartingContext? startingContext;
  private IChatClient? chatClient;
  private readonly IReadOnlyList<AgentTool> tools; // Frozen offered capabilities for this execution.
  private readonly CancellationTokenSource stop = new();
  private readonly PendingToolCalls pending;
  private Task? stopCallbacks;
  private int backgroundCalls;

  internal SessionExecution(Guid executionId, SessionBase session, Func<ProviderConnection, IChatClient> createChatClient,
      LlmSelection selection, StartingContextManager startingContextManager,
      IReadOnlyList<AgentTool>? tools = null, PendingToolCalls? pending = null) {
    ExecutionId = executionId;
    this.session = session;
    this.createChatClient = createChatClient;
    this.selection = selection;
    this.startingContextManager = startingContextManager;
    this.tools = tools ?? [];
    this.pending = pending ?? new PendingToolCalls();
  }

  internal Guid ExecutionId { get; }
  internal bool IsStopping => stop.IsCancellationRequested;
  internal bool HasPendingTools => Volatile.Read(ref backgroundCalls) != 0;
  internal Task JoinPendingToolsAsync() => pending.JoinExecutionAsync(ExecutionId);

  // Completes once every callback registered on the stop token has finished.
  internal Task StopCallbacks => stopCallbacks ?? Task.CompletedTask;

  // Called by the session while it holds its lock. Repeated calls share one task.
  internal Task StopAsync() {
    if (stopCallbacks is not null)
      return stopCallbacks;
    stopCallbacks = stop.CancelAsync();
    pending.RequestCancelExecution(ExecutionId);
    return stopCallbacks;
  }

  // Called by the session only after every request and StopCallbacks have finished
  // and the execution's result has been saved.
  public void Dispose() => stop.Dispose();

  // Called sequentially outside the session lock. Foreground calls join here;
  // registered background calls remain owned until the session drains them.
  internal async Task<ExecutionOutcome> RunRequestAsync(LlmRequest request) {
    LlmRequestResult outcome;
    bool saved;
    // Keep the source alive through the outcome save, as disposal can wait for callbacks.
    // Tools use the execution token, never this already-cleaned-up provider-request source.
    using (var requestStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) {
      outcome = await SendRequestAsync(request, requestStop);
      saved = await session.Recorder.CompleteRequestAsync(ExecutionId, outcome);
    }

    if (!saved)
      return ExecutionOutcome.Cancelled;
    if (outcome.Outcome != ExecutionOutcome.Completed)
      return outcome.Outcome;

    return await RunToolsAsync(request.RequestId, outcome.ToolCalls ?? []);
  }

  private async Task<ExecutionOutcome> RunToolsAsync(Guid requestId, IReadOnlyList<LlmToolCall> calls) {
    var foreground = new List<Task>(calls.Count);
    var failures = new List<Exception>();
    try {
      foreach (LlmToolCall call in calls) {
        if (IsStopping)
          break;
        bool background = call.Arguments.TryGetProperty("background", out JsonElement option)
            && option.ValueKind == JsonValueKind.True;
        if (!await session.Recorder.TryStartToolAsync(ExecutionId, requestId, call.CallId, background)) {
          if (IsStopping)
            break;
          throw new InvalidOperationException("The tool call could not be started.");
        }

        // Invoke immediately, before claiming the next call. File tools admit to their
        // shared path queue synchronously, preserving emitted order without Task.Run.
        if (background)
          StartBackgroundTool(requestId, call);
        else
          foreground.Add(InvokeAndSaveToolAsync(requestId, call, stop.Token));
      }
    } catch (Exception failure) {
      failures.Add(failure);
      await session.BackgroundToolCompletedAsync(ExecutionId, failure);
    }

    // Even a failed start/save/encoding must join all launched siblings before fencing.
    Task joined = Task.WhenAll(foreground);
    try {
      await joined;
    } catch (Exception failure) {
      failures.Add(joined.Exception ?? failure);
    }
    if (failures.Count != 0)
      throw new AggregateException("The tool batch could not be completed safely.", failures);

    return IsStopping ? ExecutionOutcome.Cancelled : ExecutionOutcome.Completed;
  }

  private void StartBackgroundTool(Guid requestId, LlmToolCall call) {
    // The registry is the only cancellation owner; linked-source callbacks would bypass its join.
    var cancellation = new CancellationTokenSource();
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Interlocked.Increment(ref backgroundCalls);
    try {
      pending.Add(ExecutionId, requestId, call, completion.Task, cancellation,
          failure => session.BackgroundToolCompletedAsync(ExecutionId, failure));
    } catch {
      Interlocked.Decrement(ref backgroundCalls);
      cancellation.Dispose();
      throw;
    }
    // Stop may have raced the durable start before registration. Cancel the newly owned
    // source before invocation; later Stops see it in the registry.
    if (IsStopping)
      pending.RequestCancelExecution(ExecutionId);
    // Registration precedes invocation, including tools that finish synchronously.
    _ = RunBackgroundToolAsync(requestId, call, completion, cancellation.Token);
  }

  private async Task RunBackgroundToolAsync(Guid requestId, LlmToolCall call, TaskCompletionSource completion,
      CancellationToken cancellation) {
    Exception? failure = null;
    try {
      await InvokeAndSaveToolAsync(requestId, call, cancellation);
    } catch (Exception error) {
      failure = error;
    }
    // Update the wait predicate before signalling under the session gate. The registry
    // still owns this task and all callback cleanup, so settlement must join it outside the gate.
    Interlocked.Decrement(ref backgroundCalls);
    try {
      await session.BackgroundToolCompletedAsync(ExecutionId, failure);
    } catch (Exception error) {
      failure = failure is null ? error : new AggregateException(failure, error);
    }
    if (failure is null)
      completion.TrySetResult();
    else
      completion.TrySetException(failure);
  }

  private async Task InvokeAndSaveToolAsync(Guid requestId, LlmToolCall call, CancellationToken cancellation) {
    try {
      LlmToolResult result = await InvokeToolAsync(requestId, call, cancellation);
      // A rejected or failed save leaves effects uncertain, never an ordinary tool error.
      if (!await session.Recorder.CompleteToolAsync(ExecutionId, requestId, result))
        throw new InvalidOperationException("The tool result could not be saved.");
    } catch (Exception failure) {
      await session.BackgroundToolCompletedAsync(ExecutionId, failure);
      throw;
    }
  }

  private static JsonElement ToolArguments(JsonElement arguments) {
    if (!arguments.TryGetProperty("background", out JsonElement option))
      return arguments;
    if (option.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
      throw new ArgumentException("The background option must be a boolean.", nameof(arguments));
    JsonObject values = JsonNode.Parse(arguments.GetRawText())!.AsObject();
    values.Remove("background");
    return JsonSerializer.SerializeToElement(values);
  }

  private async Task<LlmToolResult> InvokeToolAsync(Guid requestId, LlmToolCall call, CancellationToken cancellation) {
    ToolOutput output;
    try {
      cancellation.ThrowIfCancellationRequested();
      AgentTool tool = tools.FirstOrDefault(tool =>
          string.Equals(tool.Name, call.Name, StringComparison.Ordinal))
          ?? throw new InvalidOperationException($"Unknown tool '{call.Name}'.");
      AIFunctionDeclaration? offered = startingContext?.Tools.FirstOrDefault(declaration =>
          string.Equals(declaration.Name, call.Name, StringComparison.Ordinal));
      AIFunctionDeclaration current = AgentToolDeclaration.Create(tool);
      if (offered is null || offered.Description != current.Description
          || offered.JsonSchema.GetRawText() != current.JsonSchema.GetRawText())
        throw new InvalidOperationException($"Tool '{call.Name}' no longer matches its offered definition.");
      string directory = session.WorkingDirectory
          ?? throw new InvalidOperationException("The chat has no saved working folder.");
      // Progress is intentionally inert until transcript progress integration is available.
      var context = new ToolContext(directory, requestId, call.CallId, selection.SupportsImages, static _ => { });
      output = await tool.RunAsync(ToolArguments(call.Arguments), context, cancellation);
    } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
      return ToolError(call, ExecutionOutcome.Cancelled,
          "Tool execution was cancelled; effects may have occurred.");
    } catch (Exception exception) when (exception is not ToolCleanupException) {
      return ToolError(call, ExecutionOutcome.Failed, exception.Message);
    }

    // Storage freezes output and publishes images outside the ordinary invocation-error catch.
    // A tool may return an error instead of throwing after cancellation; Stop still takes priority.
    ExecutionOutcome outcome = output.IsError ? ExecutionOutcome.Failed : ExecutionOutcome.Completed;
    if (cancellation.IsCancellationRequested)
      outcome = ExecutionOutcome.Cancelled;
    return new(call.CallId, outcome, output);
  }

  private static LlmToolResult ToolError(LlmToolCall call, ExecutionOutcome outcome, string message) {
    // Bound even arbitrary exception messages, allowing for JSON escaping overhead.
    string error = message[..Math.Min(message.Length, 1024)];
    return new(call.CallId, outcome, new ToolOutput(error, [], IsError: true));
  }

  private async Task<LlmRequestResult> SendRequestAsync(LlmRequest request, CancellationTokenSource requestStop) {
    var writer = new LlmResponseWriter(request.RequestId, SaveChunkAsync,
        (callId, name) => ObserveToolAsync(request.RequestId, callId, name));
    var response = new LlmResponseAccumulator(writer);
    IAsyncEnumerator<ChatResponseUpdate>? updates = null;
    Task<bool>? pendingRead = null;
    Exception? failure = null;
    try {
      CancellationToken stopToken = stop.Token;
      stopToken.ThrowIfCancellationRequested();
      startingContext ??= session.ReadStartingContext() ?? await startingContextManager.CreateAsync(new(
          session.Kind, selection.Family, session.WorkingDirectory,
          request.Messages.FirstOrDefault(message => message.Role == ChatRole.User)?.Text ?? "", null), stopToken);
      stopToken.ThrowIfCancellationRequested();
      chatClient ??= createChatClient(selection.Connection);
      var options = new ChatOptions {
        ModelId = selection.ModelId,
        Reasoning = selection.ReadReasoningEffort() is { } effort
            ? new ReasoningOptions { Effort = effort } : null,
        Tools = [.. startingContext.Tools]
      };
      await StartRequestAsync(request.RequestId, startingContext);
      updates = chatClient
          .GetStreamingResponseAsync(LlmProviderContext.ForModel(
              [.. startingContext.Instructions, .. request.Messages], selection.SupportsImages), options, requestStop.Token)
          .GetAsyncEnumerator(requestStop.Token);

      while (await WaitForNextUpdateAsync(writer, pendingRead ??= updates.MoveNextAsync().AsTask())) {
        pendingRead = null;
        stopToken.ThrowIfCancellationRequested();
        await response.AppendAsync(updates.Current);
      }

      pendingRead = null;
      stopToken.ThrowIfCancellationRequested();
      await response.FinishAsync();
    } catch (Exception exception) {
      failure = exception;
    }

    await CleanUpAsync(requestStop, pendingRead, updates, failure);
    LlmRequestResult outcome = ToRequestResult(request.RequestId, failure);
    return outcome with {
      ToolCalls = outcome.Outcome == ExecutionOutcome.Completed ? response.ToolCalls : null,
      ProviderResponse = outcome.Outcome == ExecutionOutcome.Completed ? response.ProviderResponse : null,
      Usage = response.Usage
    };
  }

  private async Task<bool> WaitForNextUpdateAsync(LlmResponseWriter writer, Task<bool> pendingRead) {
    Task? flushDelay = writer.FlushDelay;
    if (flushDelay is not null &&
        (flushDelay.IsCompleted || await Task.WhenAny(pendingRead, flushDelay) == flushDelay)) {
      stop.Token.ThrowIfCancellationRequested();
      // Flushing clears the deadline; only another update can start a new one.
      await writer.FlushAsync();
    }

    return await pendingRead;
  }

  private LlmRequestResult ToRequestResult(Guid requestId, Exception? failure) {
    // A failed save is a session failure, not a failed LLM request.
    if (failure is OutputSaveException or RequestStartSaveException)
      ExceptionDispatchInfo.Throw(failure);

    bool stopped = stop.IsCancellationRequested;
    ExecutionOutcome outcome = failure switch {
      null when stopped => ExecutionOutcome.Cancelled,
      null => ExecutionOutcome.Completed,
      OutputRejectedException => ExecutionOutcome.Cancelled,
      OperationCanceledException when stopped => ExecutionOutcome.Cancelled,
      _ => ExecutionOutcome.Failed
    };
    string? error = outcome == ExecutionOutcome.Failed ? failure!.Message : null;
    return new(requestId, outcome, error);
  }

  private async Task StartRequestAsync(Guid requestId, StartingContext startingContext) {
    bool started;
    try {
      started = await session.Recorder.StartRequestAsync(ExecutionId, requestId, startingContext);
    } catch (Exception exception) {
      throw new RequestStartSaveException(exception);
    }

    if (!started)
      throw new OutputRejectedException();
  }

  private async Task SaveChunkAsync(LlmResponseChunk chunk) {
    bool saved;
    try {
      saved = await session.Recorder.RecordOutputAsync(ExecutionId, chunk);
    } catch (Exception exception) {
      throw new OutputSaveException(exception);
    }

    if (!saved)
      throw new OutputRejectedException();
  }

  private async Task ObserveToolAsync(Guid requestId, string callId, string? name) {
    bool saved;
    try {
      saved = await session.Recorder.ObserveToolAsync(ExecutionId, requestId, callId, name);
    } catch (Exception exception) {
      throw new OutputSaveException(exception);
    }

    if (!saved)
      throw new OutputRejectedException();
  }

  // Attempts every step even when an earlier one fails. A cleanup failure leaves the
  // execution unsettled, so it is thrown to the session instead of becoming a failed request.
  private static async Task CleanUpAsync(CancellationTokenSource requestStop, Task<bool>? pendingRead,
      IAsyncEnumerator<ChatResponseUpdate>? updates, Exception? requestFailure) {
    Exception? cancelFailure = await CaptureFailureAsync(() => new ValueTask(requestStop.CancelAsync()));
    if (pendingRead is not null) {
      // A read abandoned by cancellation is expected to fail. Wait for it anyway,
      // because the enumerator must be idle before it is disposed.
      _ = await CaptureFailureAsync(() => new ValueTask(pendingRead));
    }

    Exception? updatesFailure = updates is null ? null : await CaptureFailureAsync(updates.DisposeAsync);
    if (cancelFailure is null && updatesFailure is null)
      return;

    Exception?[] failures = [requestFailure, cancelFailure, updatesFailure];
    throw new AggregateException("The request could not be cleaned up.", failures.OfType<Exception>());
  }

  private static async Task<Exception?> CaptureFailureAsync(Func<ValueTask> action) {
    try {
      await action();
      return null;
    } catch (Exception exception) {
      return exception;
    }
  }

#pragma warning disable S3871 // Private driver signals, not public exception contracts.
  // The session no longer accepts output from this execution.
  private sealed class OutputRejectedException : Exception;

  private sealed class OutputSaveException(Exception inner) : Exception("Saving response output failed.", inner);

  private sealed class RequestStartSaveException(Exception inner) : Exception("Saving the request attempt failed.", inner);
#pragma warning restore S3871
}
