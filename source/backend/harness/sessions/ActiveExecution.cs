using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
using Urd.Core.Concurrency;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Owns the current driver and its registration, stop and completion lifetime.
internal sealed class ActiveExecution(SessionBase session, UrdStore store, AsyncLock sessionLock,
    Func<ProviderConnection, LlmApi?, IChatClient> createChatClient, ToolCatalog tools, PendingToolCalls pending,
    ISubagentRunner? subagents) {
  private readonly StartingContextBuilder startingContextBuilder = new(tools, instructionDiscovery: store.InstructionDiscovery);

  internal SessionExecutionDriver? Current { get; private set; }
  internal Task? RetainedTask { get; private set; }

  // Caller holds the session lock; the start signal is released after admission unlocks.
  internal TaskCompletionSource Register(Guid executionId, LlmSelection selection,
      ManualCompactionRequest? compaction = null, LlmRequest? firstRequest = null, string? compactionSkipReason = null) {
    var executionStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var identity = new ExecutionIdentity(session.Id, executionId, session.HistoryRevision);
    var recorder = new SessionExecutionRecorder(session, store, sessionLock, identity, subagents);
    var work = new SessionExecution(recorder, session, createChatClient, selection,
        startingContextBuilder, tools, pending, compaction, compactionSkipReason,
        (subagents as SessionManager)?.RemoteMcpTools);
    Current = new SessionExecutionDriver(session, sessionLock, work);
    RetainedTask = Current.RunAsync(executionStart.Task, firstRequest);
    return executionStart;
  }

  // Caller holds the session lock.
  internal bool IsCurrent(Guid executionId) => Current?.ExecutionId == executionId;

  // Caller holds the session lock; retain the task after settlement for shutdown.
  internal void Clear() => Current = null;

  internal async Task StopAndDrainAsync() {
    Task pendingExecution;
    Task pendingSettlement;
    Task stopCallbacks = Task.CompletedTask;
    Exception? stopFailure = null;
    using (await sessionLock.AcquireAsync()) {
      pendingExecution = RetainedTask ?? Task.CompletedTask;
      pendingSettlement = Current?.Settlement ?? Task.CompletedTask;
      if (Current is not null) {
        try {
          StopReceipt stop = session.WriteToStore(
              () => store.Executions.RequestStop(session.Id, Current.ExecutionId, ownsExecution: true));
          if (stop.Status == StopStatus.Requested)
            session.PublishChange();
        } catch (Exception failure) {
          stopFailure = failure;
        }

        stopCallbacks = Current.StopAsync();
      }
    }

    Task stopSaved = stopFailure is null ? Task.CompletedTask : Task.FromException(stopFailure);
    Task drained = Task.WhenAll(stopSaved, stopCallbacks, pendingExecution, pendingSettlement);
    try {
      await drained;
    } catch (Exception failure) {
      throw new AggregateException($"Session '{session.Id}' could not shut down.", drained.Exception ?? failure);
    }
  }

  internal async Task<StopReceipt> RequestStopAsync(Guid executionId) {
    if (executionId == Guid.Empty)
      throw new ArgumentException("An execution ID is required.", nameof(executionId));

    using (await sessionLock.AcquireAsync()) {
      session.ThrowIfClosed();
      bool ownsExecution = IsCurrent(executionId);
      // Signal even if the Stop write fails. The driver owns joining the retained
      // callback task before settlement; this acknowledgement never means cleanup finished.
      if (ownsExecution)
        _ = Current!.StopAsync();

      StopReceipt receipt = session.WriteToStore(() => store.Executions.RequestStop(session.Id, executionId, ownsExecution));
      if (receipt.Status == StopStatus.Requested)
        session.PublishChange();
      return receipt;
    }
  }

  internal async Task<SessionExecutionResult> WaitForFinishAsync(Guid executionId, CancellationToken cancellation = default) {
    Task pendingExecution;
    Task<SessionExecutionResult> pendingSettlement;
    using (await sessionLock.AcquireAsync(cancellation)) {
      if (!IsCurrent(executionId)) {
        return store.Executions.ReadSettlement(session.Id, executionId)
            ?? throw new InvalidOperationException($"Execution '{executionId}' has no known settlement in this session.");
      }

      pendingExecution = RetainedTask!;
      pendingSettlement = Current!.Settlement;
    }

    await pendingExecution.WaitAsync(cancellation);
    return await pendingSettlement.WaitAsync(cancellation);
  }
}
