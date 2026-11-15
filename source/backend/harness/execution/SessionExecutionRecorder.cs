using Urd.Core.Context;
using Urd.Core.Concurrency;
using Urd.Core.Sessions;
using Urd.Core.Storage;

namespace Urd.Core.Execution;

// Persists request and tool progress under the session lock, rejecting stale or stopped work.
internal sealed class SessionExecutionRecorder(SessionBase session, UrdStore store, AsyncLock sessionLock,
    ExecutionIdentity identity) {
  internal ExecutionIdentity Identity { get; } = identity;

  internal async Task<bool> StartRequestAsync(Guid requestId, StartingContext startingContext) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity))
      return false;

    bool started = session.WriteToStore(() => store.Executions.StartRequest(Identity, requestId, startingContext));
    if (started)
      session.PublishChange();
    return started;
  }

  internal async Task<bool> RecordOutputAsync(LlmResponseChunk chunk) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity)
        || !store.LlmResponses.Append(Identity, chunk))
      return false;

    session.PublishChange();
    return true;
  }

  internal async Task<bool> ObserveToolAsync(Guid requestId, string callId, string? name) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity)
        || !store.LlmResponses.RecordToolCall(Identity, requestId, callId, name))
      return false;

    session.PublishChange();
    return true;
  }

  internal async Task<bool> CompleteRequestAsync(LlmRequestResult outcome) {
    using var _ = await sessionLock.AcquireAsync();
    bool allowStopping = outcome.Outcome != ExecutionOutcome.Completed;
    if (!session.CanRecordExecution(Identity, allowStopping)
        || !store.LlmResponses.CompleteRequest(Identity, outcome))
      return false;

    session.PublishChange();
    return true;
  }

  internal async Task<bool> TryStartToolAsync(Guid requestId, string callId, bool background = false) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity)
        || !store.Tools.TryStart(Identity, requestId, callId, background))
      return false;

    session.PublishChange();
    return true;
  }

  internal async Task<bool> CompleteToolAsync(Guid requestId, LlmToolResult result) {
    using var _ = await sessionLock.AcquireAsync();
    if (!session.CanRecordExecution(Identity, allowStopping: true)
        || !store.Tools.Complete(Identity, requestId, result))
      return false;

    session.PublishChange();
    return true;
  }
}
