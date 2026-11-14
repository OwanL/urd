namespace Urd.Core.Compaction;

// Logical model-history order: execution, request within execution, then message within request.
// The ordinal belongs to the session's history revision; both within-parent indexes are zero-based.
// RequestId locates persisted content; it does not determine chronological order.
internal sealed record HistoryMessageLocation(
    Guid RequestId, long ExecutionOrdinal, int RequestIndexInExecution, int MessageIndexInRequest) {
  internal bool IsAfter(HistoryMessageLocation other) =>
      (ExecutionOrdinal, RequestIndexInExecution, MessageIndexInRequest)
          .CompareTo((other.ExecutionOrdinal, other.RequestIndexInExecution, other.MessageIndexInRequest)) > 0;
}
