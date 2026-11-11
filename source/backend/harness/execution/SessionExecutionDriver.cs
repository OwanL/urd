using Urd.Core.Sessions;

namespace Urd.Core.Execution;

// Drives one execution through requests, background waits and saved settlement.
// Live state uses the owning session's gate; provider work and cleanup joins run outside it.
internal sealed class SessionExecutionDriver(SessionBase session, AsyncLock gate, SessionExecution execution) {
  private readonly TaskCompletionSource<SessionExecutionResult> settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private TaskCompletionSource? executionWake;
  private Exception? executionFailure;

  internal Guid ExecutionId => execution.ExecutionId;
  internal bool IsStopping => execution.IsStopping;
  internal bool IsCompacting => execution.IsCompacting;
  internal void SetCompacting(bool value) => execution.IsCompacting = value;
  internal bool IsWaiting { get; private set; }
  internal Task<SessionExecutionResult> Settlement => settlement.Task;

  // Caller holds the session gate. The driver joins callbacks before settlement.
  internal Task StopAsync() {
    Task callbacks = execution.StopAsync();
    Wake();
    return callbacks;
  }

  // Caller holds the session gate. Notifications never join their own tool task.
  internal void BackgroundToolCompleted(Exception? failure) {
    if (failure is not null) {
      executionFailure ??= failure;
      session.MarkNeedsAttention();
      _ = execution.StopAsync();
    }
    Wake();
  }

  // Caller holds the session gate.
  internal void Wake() {
    executionWake?.TrySetResult();
    executionWake = null;
  }

  // Called under the session gate only after cleanup and successful settlement persistence.
  internal void Release() {
    execution.Dispose();
    IsWaiting = false;
    Wake();
  }

  internal async Task RunAsync(Task lockReleased) {
    try {
      await lockReleased;
      if (execution.IsCompactionOnly) {
        await AdvanceAsync(await execution.RunCompactionAsync());
        return;
      }
      LlmRequest? request;
      using (await gate.AcquireAsync())
        request = ClaimContinuation();
      if (request is null) {
        if (!IsStopping)
          throw new InvalidOperationException("The execution has no initial request and was not stopped.");

        await AdvanceAsync(ExecutionOutcome.Cancelled);
        return;
      }

      while (request is not null) {
        ExecutionOutcome outcome = await execution.RunRequestAsync(request);
        request = await AdvanceAsync(outcome);
      }
    } catch (Exception failure) {
      // A save/cleanup fault fences immediately, but still owns every task and callback.
      Task stopCallbacks;
      using (await gate.AcquireAsync()) {
        session.MarkNeedsAttention();
        stopCallbacks = StopAsync();
      }
      Task drained = Task.WhenAll(stopCallbacks, execution.JoinPendingToolsAsync());
      try {
        await drained;
      } catch (Exception cleanupFailure) {
        failure = new AggregateException(failure, drained.Exception ?? cleanupFailure);
      }
      using var held = await gate.AcquireAsync();
      settlement.TrySetException(failure);
      _ = settlement.Task.Exception; // Observed even if nobody is waiting yet.
    }
  }

  private async Task<LlmRequest?> AdvanceAsync(ExecutionOutcome outcome) {
    while (true) {
      Task? wake = null;
      Task stopCallbacks;
      using (await gate.AcquireAsync()) {
        ThrowIfExecutionFailed();
        if (outcome == ExecutionOutcome.Completed && !IsStopping) {
          LlmRequest? next = ClaimContinuation();
          if (next is not null)
            return next;
          if (execution.HasPendingTools) {
            // Install the wait under the same gate as all input/completion/Stop signals.
            executionWake ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            wake = executionWake.Task;
            if (!IsWaiting) {
              IsWaiting = true;
              session.PublishChange();
            }
          }
        }
        stopCallbacks = outcome != ExecutionOutcome.Completed || IsStopping
            ? execution.StopAsync() : execution.StopCallbacks;
      }
      if (wake is not null) {
        await wake;
        continue; // Notifications are hints; always recheck durable request readiness.
      }

      // Terminal provider failure and Stop cancel first. Even successful completion joins
      // remaining callback/notification ownership before the final guarded decision.
      await Task.WhenAll(stopCallbacks, execution.JoinPendingToolsAsync());
      using (await gate.AcquireAsync()) {
        ThrowIfExecutionFailed();
        if (!execution.StopCallbacks.IsCompletedSuccessfully)
          continue; // A late Stop must join its newly installed callback task too.
        if (outcome == ExecutionOutcome.Completed && !IsStopping) {
          LlmRequest? next = ClaimContinuation();
          if (next is not null)
            return next;
        }
        SessionExecutionResult saved = session.SettleExecution(this, outcome == ExecutionOutcome.Completed && IsStopping
            ? ExecutionOutcome.Cancelled : outcome);
        settlement.TrySetResult(saved);
        return null;
      }
    }
  }

  // Caller holds the session gate. Admission cannot slip between recheck and settlement.
  private LlmRequest? ClaimContinuation() {
    LlmRequest? next = execution.IsCompactionOnly ? null : session.ClaimNextRequest(ExecutionId);
    if (next is not null) {
      IsWaiting = false;
      session.PublishChange();
    }
    return next;
  }

  private void ThrowIfExecutionFailed() {
    if (executionFailure is not null)
      throw new AggregateException("Tool cleanup or result recording failed.", executionFailure);
  }
}
