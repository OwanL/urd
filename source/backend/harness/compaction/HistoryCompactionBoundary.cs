namespace Urd.Core.Compaction;

// Chooses the first message to keep verbatim; earlier messages will be summarized.
// Null means no suitable split. Index zero is excluded so compaction always removes some history.
internal static class HistoryCompactionBoundary {
  // The retention target is soft. If no safe cut fits it, preserve the latest turn
  // intact and still make room by summarizing older history.
  internal static int? FindFirstRetainedMessageIndex(ModelHistory history, int retainedHistoryTokenLimit) =>
      FindBoundaryWithinTokenBudget(history.Messages, retainedHistoryTokenLimit,
          minimumIndex: 1, previousBoundary: history.PreviousCompaction?.FirstRetainedMessageLocation)
      ?? FindBoundaryPreservingLatestUserTurn(history);

  // A copy may keep all available history, including the first message of an existing suffix.
  internal static int? FindRecentSuffix(IReadOnlyList<HistoryMessage> messages, int retainedHistoryTokenLimit) =>
      FindBoundaryWithinTokenBudget(messages, retainedHistoryTokenLimit, minimumIndex: 0, previousBoundary: null);

  private static int? FindBoundaryWithinTokenBudget(IReadOnlyList<HistoryMessage> messages,
      int retainedHistoryTokenLimit, int minimumIndex, HistoryMessageLocation? previousBoundary) {
    int? oldestUserTurnIndexWithinBudget = null;
    int? oldestAssistantIndexWithinBudget = null;
    long retainedHistoryTokens = 0;

    for (int messageIndex = messages.Count - 1; messageIndex >= minimumIndex; messageIndex--) {
      HistoryMessage message = messages[messageIndex];
      retainedHistoryTokens = ContextTokenBudget.AddSaturating(retainedHistoryTokens,
          ContextTokenBudget.EstimateMessage(message.Message));
      if (retainedHistoryTokens > retainedHistoryTokenLimit)
        break;
      if (!CanBeginRetainedHistory(message, previousBoundary))
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
      if (!CanBeginRetainedHistory(message, history.PreviousCompaction?.FirstRetainedMessageLocation))
        continue;
      if (message.IsUserTurnStart)
        return messageIndex;

      bool keepsLatestUserTurn = latestUserTurnIndex is null || messageIndex <= latestUserTurnIndex;
      if (keepsLatestUserTurn)
        latestAssistantIndexKeepingUserTurn ??= messageIndex;
    }

    return latestAssistantIndexKeepingUserTurn;
  }

  private static bool CanBeginRetainedHistory(HistoryMessage message, HistoryMessageLocation? previousBoundary) =>
      (message.IsUserTurnStart || message.IsAssistantContinuation) &&
      (previousBoundary is null || message.Location.IsAfter(previousBoundary));

  private static int? FindLatestUserTurnIndex(IReadOnlyList<HistoryMessage> messages) {
    for (int messageIndex = messages.Count - 1; messageIndex >= 0; messageIndex--)
      if (messages[messageIndex].IsUserTurnStart)
        return messageIndex;
    return null;
  }
}
