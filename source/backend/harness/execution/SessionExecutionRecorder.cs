using Urd.Core.Context;
using Urd.Core.Sessions;
using Urd.Core.Storage;

namespace Urd.Core.Execution;

// Persists request and tool progress under the session gate, rejecting stale or stopped work.
internal sealed class SessionExecutionRecorder(SessionBase session, UrdStore store, AsyncLock gate) {
  internal async Task<bool> StartRequestAsync(Guid executionId, Guid requestId, StartingContext startingContext) {
    using var held = await gate.AcquireAsync();
    if (!session.CanRecordExecution(executionId))
      return false;

    bool started = session.WriteToStore(() => store.Executions.StartRequest(
        session.Id, executionId, session.HistoryRevision, requestId, startingContext));
    if (started)
      session.PublishChange();
    return started;
  }

  internal async Task<bool> RecordOutputAsync(Guid executionId, LlmResponseChunk chunk) {
    using var held = await gate.AcquireAsync();
    if (!session.CanRecordExecution(executionId)
        || !store.LlmResponses.Append(session.Id, executionId, session.HistoryRevision, chunk))
      return false;

    session.PublishChange();
    return true;
  }

  internal async Task<bool> ObserveToolAsync(Guid executionId, Guid requestId, string callId, string? name) {
    using var held = await gate.AcquireAsync();
    if (!session.CanRecordExecution(executionId)
        || !store.LlmResponses.ObserveTool(session.Id, executionId, session.HistoryRevision, requestId, callId, name))
      return false;

    session.PublishChange();
    return true;
  }

  internal async Task<bool> CompleteRequestAsync(Guid executionId, LlmRequestResult outcome) {
    using var held = await gate.AcquireAsync();
    bool allowStopping = outcome.Outcome != ExecutionOutcome.Completed;
    if (!session.CanRecordExecution(executionId, allowStopping)
        || !store.LlmResponses.CompleteRequest(session.Id, executionId, session.HistoryRevision, outcome))
      return false;

    session.PublishChange();
    return true;
  }

  internal async Task<bool> TryStartToolAsync(Guid executionId, Guid requestId, string callId, bool background = false) {
    using var held = await gate.AcquireAsync();
    if (!session.CanRecordExecution(executionId)
        || !store.Tools.TryStart(session.Id, executionId, session.HistoryRevision, requestId, callId, background))
      return false;

    session.PublishChange();
    return true;
  }

  internal async Task<bool> CompleteToolAsync(Guid executionId, Guid requestId, LlmToolResult result) {
    using var held = await gate.AcquireAsync();
    if (!session.CanRecordExecution(executionId, allowStopping: true)
        || !store.Tools.Complete(session.Id, executionId, session.HistoryRevision, requestId, result))
      return false;

    session.PublishChange();
    return true;
  }
}
