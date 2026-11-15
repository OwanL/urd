namespace Urd.Core.Compaction;

internal static class CompactionBoundarySelector {
  internal static int? FindFirstRetainedMessageIndex(ModelHistory history, int retainedHistoryTokenLimit,
      CompactionReason reason) {
    BoundaryCandidates candidates = FindBoundaryCandidates(history, retainedHistoryTokenLimit);
    return SelectPreferredBoundary(candidates, reason);
  }

  private static BoundaryCandidates FindBoundaryCandidates(ModelHistory history, int retainedHistoryTokenLimit) {
    int? newestUserTurnIndex = FindNewestUserTurnIndex(history.Messages);
    int? oldestAffordableTurnStart = null;
    int? oldestAffordableAssistantContinuation = null;
    int? newestOverflowTurnStart = null;
    int? newestOverflowAssistantContinuation = null;
    long suffixTokens = 0;

    for (int index = history.Messages.Count - 1; index > 0; index--) {
      HistoryMessage message = history.Messages[index];
      suffixTokens = ContextTokenBudget.AddSaturating(suffixTokens,
          ContextTokenBudget.EstimateMessage(message.Message));
      if (!AdvancesPreviousCompaction(history, message)) continue;

      bool suffixWithinBudget = suffixTokens <= retainedHistoryTokenLimit;
      if (message.IsUserTurnStart) {
        newestOverflowTurnStart ??= index;
        if (suffixWithinBudget) oldestAffordableTurnStart = index;
        continue;
      }

      if (!message.IsAssistantContinuation) continue;

      bool retainsNewestUserRequest = newestUserTurnIndex is null || index <= newestUserTurnIndex;
      if (retainsNewestUserRequest) newestOverflowAssistantContinuation ??= index;
      if (suffixWithinBudget) oldestAffordableAssistantContinuation = index;
    }

    return new BoundaryCandidates(oldestAffordableTurnStart, oldestAffordableAssistantContinuation,
        newestOverflowTurnStart, newestOverflowAssistantContinuation);
  }

  private static int? SelectPreferredBoundary(BoundaryCandidates candidates, CompactionReason reason) {
    if (candidates.OldestAffordableTurnStart is { } turnStart) return turnStart;
    if (candidates.OldestAffordableAssistantContinuation is { } assistantContinuation) return assistantContinuation;
    if (reason != CompactionReason.Overflow) return null;
    return candidates.NewestOverflowTurnStart ?? candidates.NewestOverflowAssistantContinuation;
  }

  private static bool AdvancesPreviousCompaction(ModelHistory history, HistoryMessage message) =>
      history.PreviousCompaction is not { } previous ||
      message.Location.IsAfter(previous.FirstRetainedMessageLocation);

  private static int? FindNewestUserTurnIndex(IReadOnlyList<HistoryMessage> messages) {
    for (int index = messages.Count - 1; index >= 0; index--)
      if (messages[index].IsUserTurnStart) return index;
    return null;
  }

  private readonly record struct BoundaryCandidates(
      int? OldestAffordableTurnStart, int? OldestAffordableAssistantContinuation,
      int? NewestOverflowTurnStart, int? NewestOverflowAssistantContinuation);
}
