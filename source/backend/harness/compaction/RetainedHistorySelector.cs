namespace Urd.Core.Compaction;

internal static class RetainedHistorySelector {
  internal static int? SelectFirstMessageIndex(ModelHistory history, int retainedHistoryTokenLimit,
      CompactionReason reason) {
    FirstMessageIndexCandidates candidates = FindFirstMessageIndexCandidates(history, retainedHistoryTokenLimit);
    return SelectPreferredFirstMessageIndex(candidates, reason);
  }

  private static FirstMessageIndexCandidates FindFirstMessageIndexCandidates(ModelHistory history,
      int retainedHistoryTokenLimit) {
    int? newestUserTurnIndex = FindNewestUserTurnIndex(history.Messages);
    int? oldestAffordableTurnStart = null;
    int? oldestAffordableAssistantContinuation = null;
    int? newestOverflowTurnStart = null;
    int? newestOverflowAssistantContinuation = null;
    long candidateHistoryTokens = 0;

    for (int index = history.Messages.Count - 1; index > 0; index--) {
      HistoryMessage message = history.Messages[index];
      candidateHistoryTokens = ContextTokenBudget.AddSaturating(candidateHistoryTokens,
          ContextTokenBudget.EstimateMessage(message.Message));
      if (!AdvancesPastPreviousCompaction(history, message)) continue;

      bool candidateWithinBudget = candidateHistoryTokens <= retainedHistoryTokenLimit;
      if (message.IsUserTurnStart) {
        newestOverflowTurnStart ??= index;
        if (candidateWithinBudget) oldestAffordableTurnStart = index;
        continue;
      }

      if (!message.IsAssistantContinuation) continue;

      bool retainsNewestUserRequest = newestUserTurnIndex is null || index <= newestUserTurnIndex;
      if (retainsNewestUserRequest) newestOverflowAssistantContinuation ??= index;
      if (candidateWithinBudget) oldestAffordableAssistantContinuation = index;
    }

    return new FirstMessageIndexCandidates(oldestAffordableTurnStart, oldestAffordableAssistantContinuation,
        newestOverflowTurnStart, newestOverflowAssistantContinuation);
  }

  private static int? SelectPreferredFirstMessageIndex(FirstMessageIndexCandidates candidates,
      CompactionReason reason) {
    if (candidates.OldestAffordableTurnStart is { } turnStart) return turnStart;
    if (candidates.OldestAffordableAssistantContinuation is { } assistantContinuation) return assistantContinuation;
    if (reason != CompactionReason.Overflow) return null;
    return candidates.NewestOverflowTurnStart ?? candidates.NewestOverflowAssistantContinuation;
  }

  private static bool AdvancesPastPreviousCompaction(ModelHistory history, HistoryMessage message) =>
      history.PreviousCompaction is not { } previous ||
      message.Location.IsAfter(previous.FirstRetainedMessageLocation);

  private static int? FindNewestUserTurnIndex(IReadOnlyList<HistoryMessage> messages) {
    for (int index = messages.Count - 1; index >= 0; index--)
      if (messages[index].IsUserTurnStart) return index;
    return null;
  }

  private readonly record struct FirstMessageIndexCandidates(
      int? OldestAffordableTurnStart, int? OldestAffordableAssistantContinuation,
      int? NewestOverflowTurnStart, int? NewestOverflowAssistantContinuation);
}
