using Urd.Core.Execution;

namespace Urd.Core.Sessions;

// Current Stop status; acknowledgement does not imply that owned cleanup has finished.
public sealed record StopReceipt(Guid RunId, StopStatus Status, ExecutionOutcome? Outcome = null);

public enum StopStatus {
  Requested,
  Settled,
  NotFound,
  Unavailable
}
