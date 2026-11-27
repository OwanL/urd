using Urd.Core.Execution;

namespace Urd.Core.Storage;

// Shared fencing and identity parameters for writes owned by the current execution.
internal static class ExecutionSql {
  internal const string CurrentRunCondition =
      "r.history_revision = $revision AND s.history_revision = $revision AND r.stopping = 0 AND r.outcome IS NULL "
      + "AND r.queued_after_compaction IS NULL";

  internal static (string Name, object? Value)[] Parameters(ExecutionIdentity execution,
      params (string Name, object? Value)[] extra) =>
      [("$session", execution.SessionId), ("$run", execution.ExecutionId), ("$revision", execution.HistoryRevision), .. extra];
}
