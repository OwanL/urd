using Urd.Core.Sessions;
using Urd.Core.Concurrency;

namespace Urd.Core.Execution;

// Drives one execution through requests, background waits and saved settlement.
// Live state uses the owning session's lock; provider work and cleanup joins run outside it.
internal sealed class SessionExecutionDriver(SessionBase session, AsyncLock sessionLock, SessionExecution execution) {
  private readonly TaskCompletionSource<SessionExecutionResult> settlement = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private TaskCompletionSource? executionWake;
  private Exception? backgroundToolFailure;

  internal ExecutionIdentity Identity => execution.Identity;
  internal SessionExecutionRecorder Recorder => execution.Recorder;
  internal Guid ExecutionId => Identity.ExecutionId;
  internal bool IsStopping => execution.IsStopping;
  internal bool IsCompacting => execution.IsCompacting;
  internal bool IsManualCompaction => execution.IsManualCompaction;
  internal RequestContextEstimate? ContextEstimate => execution.ContextEstimate;
  internal void RetireContextEstimate() => execution.RetireContextEstimate();
  internal void SetCompacting(bool value) => execution.IsCompacting = value;
  internal bool IsWaitingForBackgroundTools { get; private set; }
  internal Task<SessionExecutionResult> Settlement => settlement.Task;

  // Caller holds the session lock. The driver joins callbacks before settlement.
  internal Task StopAsync() {
    Wake();
    session.Questions.FailPresentedQuestion();
    return execution.StopAsync();
  }

  // Caller holds the session lock. Notifications never join their own tool task.
  internal void ReportToolOutcome(Exception? failure) {
    if (failure is not null) {
      backgroundToolFailure ??= failure;
      session.MarkNeedsAttention();
      _ = StopAsync();
    } else {
      Wake();
    }
  }

  // Caller holds the session lock.
  internal void Wake() {
    if (executionWake is null)
      return;
    session.Questions.UnparkBackgroundWait(ExecutionId);
    executionWake.TrySetResult();
    executionWake = null;
  }

  // Called under the session lock only after cleanup and successful settlement persistence.
  internal void Release() {
    execution.Dispose();
    IsWaitingForBackgroundTools = false;
    Wake();
  }

  internal async Task RunAsync(Task lockReleased, LlmRequest? firstRequest) {
    try {
      await lockReleased;
      if (execution.IsManualCompaction) {
        await AdvanceAsync(await execution.RunCompactionAsync());
        return;
      }
      LlmRequest? request;
      using (await sessionLock.AcquireAsync())
        request = IsStopping ? null : firstRequest ?? ClaimContinuation();
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
      using (await sessionLock.AcquireAsync()) {
        session.MarkNeedsAttention();
        stopCallbacks = StopAsync();
      }
      Task drained = Task.WhenAll(stopCallbacks, execution.JoinOwnedWorkAsync());
      try {
        await drained;
      } catch (Exception cleanupFailure) {
        failure = new AggregateException(failure, drained.Exception ?? cleanupFailure);
      }
      try {
        execution.CloseSocket();
      } catch (Exception cleanupFailure) {
        failure = new AggregateException(failure, cleanupFailure);
      }
      using (await sessionLock.AcquireAsync()) {
        settlement.TrySetException(failure);
        _ = settlement.Task.Exception; // Observed even if nobody is waiting yet.
      }
    }
  }

  private async Task<LlmRequest?> AdvanceAsync(ExecutionOutcome outcome) {
    bool settled = false;
    TaskCompletionSource? wakeStart = null;
    while (!settled) {
      Task? wake = null;
      Task stopCallbacks;
      using (await sessionLock.AcquireAsync()) {
        ThrowIfExecutionFailed();
        if (outcome == ExecutionOutcome.Completed && !IsStopping) {
          LlmRequest? next = ClaimContinuation();
          if (next is not null)
            return next;
          bool hasPendingTools = execution.HasPendingTools;
          if (hasPendingTools || execution.HasPendingAuxiliary) {
            // Install the wait under the same lock as all input/completion/Stop signals.
            executionWake = new(TaskCreationOptions.RunContinuationsAsynchronously);
            wake = executionWake.Task;
            if (hasPendingTools)
              session.Questions.ParkBackgroundWait(ExecutionId);
            if (hasPendingTools && !IsWaitingForBackgroundTools) {
              IsWaitingForBackgroundTools = true;
              session.PublishChange();
            }
          }
        }
        stopCallbacks = outcome != ExecutionOutcome.Completed || IsStopping
            ? StopAsync() : execution.StopCallbacks;
      }
      await WaitOrJoinAsync(wake, stopCallbacks);
      // Notifications are hints; always recheck durable request readiness.
      if (wake is null) {
        using (await sessionLock.AcquireAsync()) {
          ThrowIfExecutionFailed();
          // A late Stop must join its newly installed callback task too.
          settled = execution.StopCallbacks.IsCompletedSuccessfully;
          if (settled) {
            if (outcome == ExecutionOutcome.Completed && !IsStopping) {
              LlmRequest? next = ClaimContinuation();
              if (next is not null)
                return next;
            }
            SessionExecutionResult saved = session.FinishExecution(this, outcome == ExecutionOutcome.Completed && IsStopping
                ? ExecutionOutcome.Cancelled : outcome, out wakeStart);
            settlement.TrySetResult(saved);
          }
        }
      }
    }
    // The waking run gates on this start signal, so release it only outside the settlement lock.
    wakeStart?.SetResult();
    return null;
  }

  // Terminal provider failure and Stop cancel first. Even successful completion joins
  // remaining callback/notification ownership before the final guarded decision.
  private Task WaitOrJoinAsync(Task? wake, Task stopCallbacks) =>
      wake ?? Task.WhenAll(stopCallbacks, execution.JoinOwnedWorkAsync());

  // Caller holds the session lock. Admission cannot slip between recheck and settlement.
  private LlmRequest? ClaimContinuation() {
    LlmRequest? next = execution.IsManualCompaction ? null : session.Questions.CreateNextRequest(Identity);
    if (next is not null) {
      IsWaitingForBackgroundTools = false;
      session.PublishChange();
    }
    return next;
  }

  private void ThrowIfExecutionFailed() {
    if (backgroundToolFailure is not null)
      throw new AggregateException("Tool cleanup or result recording failed.", backgroundToolFailure);
  }
}
