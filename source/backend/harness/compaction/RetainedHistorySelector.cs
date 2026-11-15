namespace Urd.Core.Compaction;

internal static class RetainedHistorySelector {
  internal static int? FindFirstRetainedIndex(ModelHistory history, int retainedHistoryTokenLimit, CompactionReason reason) {
    int? startWithinBudget = FindOldestStartWithinBudget(history, retainedHistoryTokenLimit);
    if (startWithinBudget is not null || reason != CompactionReason.Overflow) return startWithinBudget;
    return FindNewestStartForOverflow(history);
  }

  private static int? FindOldestStartWithinBudget(ModelHistory history, int retainedHistoryTokenLimit) {
    int? oldestTurnStart = null;
    int? oldestAssistantContinuation = null;
    long retainedTokens = 0;

    for (int index = history.Messages.Count - 1; index > 0; index--) {
      HistoryMessage message = history.Messages[index];
      retainedTokens = ContextTokenBudget.AddSaturating(retainedTokens,
          ContextTokenBudget.EstimateMessage(message.Message));
      if (retainedTokens > retainedHistoryTokenLimit) break;
      if (!CanStartRetainedHistory(history, message)) continue;

      if (message.IsUserTurnStart) oldestTurnStart = index;
      else oldestAssistantContinuation = index;
    }

    return oldestTurnStart ?? oldestAssistantContinuation;
  }

  private static int? FindNewestStartForOverflow(ModelHistory history) {
    int? newestUserTurnIndex = FindNewestUserTurnIndex(history.Messages);
    int? newestAssistantContinuation = null;

    for (int index = history.Messages.Count - 1; index > 0; index--) {
      HistoryMessage message = history.Messages[index];
      if (!CanStartRetainedHistory(history, message)) continue;
      if (message.IsUserTurnStart) return index;

      bool retainsNewestUserRequest = newestUserTurnIndex is null || index <= newestUserTurnIndex;
      if (retainsNewestUserRequest) newestAssistantContinuation ??= index;
    }

    return newestAssistantContinuation;
  }

  private static bool CanStartRetainedHistory(ModelHistory history, HistoryMessage message) =>
      (message.IsUserTurnStart || message.IsAssistantContinuation) && IsAfterPreviousCompaction(history, message);

  private static bool IsAfterPreviousCompaction(ModelHistory history, HistoryMessage message) =>
      history.PreviousCompaction is not { } previous ||
      message.Location.IsAfter(previous.FirstRetainedMessageLocation);

  private static int? FindNewestUserTurnIndex(IReadOnlyList<HistoryMessage> messages) {
    for (int index = messages.Count - 1; index >= 0; index--)
      if (messages[index].IsUserTurnStart) return index;
    return null;
  }
}
