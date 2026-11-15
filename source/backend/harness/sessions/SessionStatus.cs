using Urd.Core.Execution;

namespace Urd.Core.Sessions;

// Projects guarded session state without owning admission or execution decisions.
internal sealed class SessionStatus(long historyRevision) {
  private bool hasUnownedExecution;
  private bool historyRevisionChanged;
  private SessionExecutionResult? lastSettlement;

  internal void Update(SessionSnapshot stored, SessionExecutionDriver? execution) {
    hasUnownedExecution = stored.UnsettledExecutionId is { } executionId && execution?.ExecutionId != executionId;
    historyRevisionChanged = stored.HistoryRevision != historyRevision;
    lastSettlement = stored.LastSettlement;
  }

  internal void Replaced(long revision) {
    historyRevision = revision;
    historyRevisionChanged = false;
    hasUnownedExecution = false;
    lastSettlement = null;
  }

  internal void Settled(SessionExecutionResult saved) {
    hasUnownedExecution = false;
    lastSettlement = saved;
  }

  internal SessionSnapshot WithLiveState(SessionSnapshot stored, bool acceptingUserMessages, SessionExecutionDriver? execution) {
    bool ownsExecution = stored.UnsettledExecutionId is { } executionId && execution?.ExecutionId == executionId;
    bool fenced = !acceptingUserMessages || stored.HistoryRevision != historyRevision
        || (stored.UnsettledExecutionId is not null && !ownsExecution);
    bool stopping = stored.StopRequested || (ownsExecution && execution!.IsStopping);
    return stored with { AdmissionFenced = fenced, StopRequested = stopping };
  }

  internal string CurrentStatus(SessionSnapshot stored, bool acceptingUserMessages, bool needsAttention, SessionExecutionDriver? execution) {
    SessionSnapshot snapshot = WithLiveState(stored, acceptingUserMessages, execution);
    return Read(needsAttention, snapshot.AdmissionFenced, execution, snapshot.LastSettlement?.Outcome);
  }

  internal string ReadCurrent(bool acceptingUserMessages, bool needsAttention, SessionExecutionDriver? execution) =>
      Read(needsAttention, !acceptingUserMessages || hasUnownedExecution || historyRevisionChanged,
          execution, lastSettlement?.Outcome);

  private static string Read(bool needsAttention, bool admissionFenced, SessionExecutionDriver? execution, ExecutionOutcome? lastOutcome) {
    if (needsAttention)
      return "needs-attention";
    if (admissionFenced)
      return "fenced";
    if (execution is not null) {
      if (execution.IsCompacting)
        return "compacting";
      return execution.IsWaiting ? "waiting" : "running";
    }
    return lastOutcome == ExecutionOutcome.Failed ? "failed" : "idle";
  }
}
