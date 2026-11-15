namespace Urd.Core.Compaction;

// Chooses the first message to keep verbatim; earlier messages will be summarized.
// Null means no suitable split. Index zero is excluded so compaction always removes some history.
internal static class HistoryCompactionBoundary {
  internal static int? FindFirstRetainedMessageIndex(ModelHistory history, int retainedHistoryTokenLimit,
      CompactionReason reason) {
    int? boundaryWithinBudget = FindBoundaryWithinTokenBudget(history, retainedHistoryTokenLimit);
    if (boundaryWithinBudget is not null || reason != CompactionReason.Overflow)
      return boundaryWithinBudget;

    // Overflow recovery may exceed the retention budget rather than leave all history unchanged.
    return FindBoundaryPreservingLatestUserTurn(history);
  }

  private static int? FindBoundaryWithinTokenBudget(ModelHistory history, int retainedHistoryTokenLimit) {
    int? oldestUserTurnIndexWithinBudget = null;
    int? oldestAssistantIndexWithinBudget = null;
    long retainedHistoryTokens = 0;

    for (int messageIndex = history.Messages.Count - 1; messageIndex > 0; messageIndex--) {
      HistoryMessage message = history.Messages[messageIndex];
      retainedHistoryTokens = ContextTokenBudget.AddSaturating(retainedHistoryTokens,
          ContextTokenBudget.EstimateMessage(message.Message));
      if (retainedHistoryTokens > retainedHistoryTokenLimit)
        break;
      if (!CanBeginRetainedHistory(history, message))
        continue;

      if (message.IsUserTurnStart)
        oldestUserTurnIndexWithinBudget = messageIndex;
      else
        oldestAssistantIndexWithinBudget = messageIndex;
    }

    // Prefer whole user turns; split before an assistant message only when no whole turn fits.
    return oldestUserTurnIndexWithinBudget ?? oldestAssistantIndexWithinBudget;
  }

  private static int? FindBoundaryPreservingLatestUserTurn(ModelHistory history) {
    int? latestUserTurnIndex = FindLatestUserTurnIndex(history.Messages);
    int? latestAssistantIndexKeepingUserTurn = null;

    for (int messageIndex = history.Messages.Count - 1; messageIndex > 0; messageIndex--) {
      HistoryMessage message = history.Messages[messageIndex];
      if (!CanBeginRetainedHistory(history, message))
        continue;
      if (message.IsUserTurnStart)
        return messageIndex;

      bool keepsLatestUserTurn = latestUserTurnIndex is null || messageIndex <= latestUserTurnIndex;
      if (keepsLatestUserTurn)
        latestAssistantIndexKeepingUserTurn ??= messageIndex;
    }

    return latestAssistantIndexKeepingUserTurn;
  }

  private static bool CanBeginRetainedHistory(ModelHistory history, HistoryMessage message) =>
      (message.IsUserTurnStart || message.IsAssistantContinuation) && AdvancesPreviousBoundary(history, message);

  private static bool AdvancesPreviousBoundary(ModelHistory history, HistoryMessage message) =>
      history.PreviousCompaction is not { } previous ||
      message.Location.IsAfter(previous.FirstRetainedMessageLocation);

  private static int? FindLatestUserTurnIndex(IReadOnlyList<HistoryMessage> messages) {
    for (int messageIndex = messages.Count - 1; messageIndex >= 0; messageIndex--)
      if (messages[messageIndex].IsUserTurnStart)
        return messageIndex;
    return null;
  }
}
