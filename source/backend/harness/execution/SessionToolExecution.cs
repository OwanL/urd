using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Sessions;
using Urd.Core.Tools;

namespace Urd.Core.Execution;

// Invokes foreground and background tools, saves their results, and drains background work.
internal sealed class SessionToolExecution {
  private readonly SessionBase session;
  private readonly SessionExecutionRecorder recorder;
  private IReadOnlyList<AgentTool> tools; // Frozen after remote discovery, before the first request.
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

  internal void AddRemoteTools(IReadOnlyList<AgentTool> remote) => tools = [.. tools, .. remote];

  private Guid ExecutionId => recorder.Identity.ExecutionId;
  private bool IsStopping => stopToken.IsCancellationRequested;
  internal bool HasRunningBackgroundTools => Volatile.Read(ref runningBackgroundTools) != 0;
  internal void CancelBackgroundTools() => pendingToolCalls.RequestCancelExecution(ExecutionId);
  internal Task JoinAsync() => pendingToolCalls.JoinExecutionAsync(ExecutionId);

  // The effective background mode is decided before the durable claim: a tool whose arguments
  // force it, such as an ask with wake false, must be claimed as background work directly.
  private bool RunsInBackground(LlmToolCall call) {
    AgentTool? tool = tools.FirstOrDefault(candidate =>
        string.Equals(candidate.Name, call.Name, StringComparison.Ordinal));
    return tool is not null && (tool.ForcesBackground(call.Arguments) || tool.SupportsBackgroundOption &&
        call.Arguments.TryGetProperty("background", out JsonElement option) && option.ValueKind == JsonValueKind.True);
  }

  internal async Task<ExecutionOutcome> RunBatchAsync(Guid requestId, IReadOnlyList<LlmToolCall> calls,
      IReadOnlyList<AIFunctionDeclaration> offeredDefinitions, ContextStamp? contextStamp) {
    var foregroundTasks = new List<Task>(calls.Count);
    var failures = new List<Exception>();
    try {
      foreach (LlmToolCall call in calls) {
        if (IsStopping)
          break;
        bool runInBackground = RunsInBackground(call);
        if (!await recorder.TryStartToolAsync(requestId, call.CallId, runInBackground)) {
          if (IsStopping)
            break;
          throw new InvalidOperationException("The tool call could not be started.");
        }

        // Invoke immediately after the durable claim, before claiming the next call. File tools
        // admit to their shared path queue synchronously, preserving emitted order without Task.Run.
        if (runInBackground)
          StartBackgroundTool(requestId, call, offeredDefinitions, runInBackground, contextStamp);
        else
          foregroundTasks.Add(InvokeAndSaveToolAsync(requestId, call, offeredDefinitions, runInBackground,
              contextStamp, stopToken));
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
      IReadOnlyList<AIFunctionDeclaration> offeredDefinitions, bool runInBackground, ContextStamp? contextStamp) {
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
    _ = RunBackgroundToolAsync(requestId, call, offeredDefinitions, completion, runInBackground, contextStamp,
        cancellation.Token);
  }

  private async Task RunBackgroundToolAsync(Guid requestId, LlmToolCall call,
      IReadOnlyList<AIFunctionDeclaration> offeredDefinitions, TaskCompletionSource completion,
      bool runInBackground, ContextStamp? contextStamp, CancellationToken cancellation) {
    Exception? failure = null;
    try {
      await InvokeAndSaveToolAsync(requestId, call, offeredDefinitions, runInBackground, contextStamp, cancellation);
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
      IReadOnlyList<AIFunctionDeclaration> offeredDefinitions, bool runInBackground, ContextStamp? contextStamp,
      CancellationToken cancellation) {
    try {
      int reportedProgress = 0;
      Action<ToolOutput> reportProgress = output => {
        if (cancellation.IsCancellationRequested)
          return;
        Interlocked.Exchange(ref reportedProgress, 1);
        recorder.ReportToolProgress(requestId, call.CallId, output);
      };
      try {
        LlmToolResult result = await ToolInvoker.InvokeAsync(requestId, call, tools, offeredDefinitions,
            session.WorkingDirectory, supportsImages, runInBackground, cancellation, contextStamp,
            recorder.CreateHarnessToolContext, reportProgress);
        // A rejected or failed save leaves effects uncertain, never an ordinary tool error.
        if (!await recorder.CompleteToolAsync(requestId, result))
          throw new InvalidOperationException("The tool result could not be saved.");
      } finally {
        if (Volatile.Read(ref reportedProgress) != 0)
          recorder.ClearToolProgress(requestId, call.CallId);
      }
    } catch (Exception failure) {
      await session.ReportToolOutcomeAsync(ExecutionId, failure);
      throw;
    }
  }

}
