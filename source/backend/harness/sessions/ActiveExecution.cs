using Microsoft.Extensions.AI;
using Urd.Core.Compaction;
using Urd.Core.Concurrency;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Owns the current driver and retains its task until shutdown has joined its tail.
internal sealed class ActiveExecution(SessionBase session, UrdStore store, AsyncLock sessionLock,
    Func<ProviderConnection, IChatClient> createChatClient, ToolCatalog tools, PendingToolCalls pending,
    ISubagentRunner? subagents) {
  private readonly StartingContextBuilder startingContextBuilder = new(tools, instructionDiscovery: store.InstructionDiscovery);

  internal SessionExecutionDriver? Current { get; private set; }
  internal Task? RetainedTask { get; private set; }

  // Caller holds the session lock; the start signal is released after admission unlocks.
  internal TaskCompletionSource Register(Guid executionId, LlmSelection selection,
      ManualCompactionRequest? compaction = null, LlmRequest? firstRequest = null) {
    var executionStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var identity = new ExecutionIdentity(session.Id, executionId, session.HistoryRevision);
    var recorder = new SessionExecutionRecorder(session, store, sessionLock, identity, subagents);
    var work = new SessionExecution(recorder, session, createChatClient, selection,
        startingContextBuilder, tools.Tools, pending, compaction);
    Current = new SessionExecutionDriver(session, sessionLock, work);
    RetainedTask = Current.RunAsync(executionStart.Task, firstRequest);
    return executionStart;
  }

  // Caller holds the session lock.
  internal bool IsCurrent(Guid executionId) => Current?.ExecutionId == executionId;

  // Caller holds the session lock; retain the task after settlement for shutdown.
  internal void Clear() => Current = null;
}
