using Microsoft.Extensions.AI;

namespace Urd.Core.Compaction;

internal static class CompactionBoundarySelector {
  internal static int? FindFirstRetainedMessage(ModelHistory history, int retainedHistoryTokenLimit,
      CompactionReason reason) {
    int latestUserTurnIndex = FindLatestUserTurnIndex(history.Messages);
    int? oldestTurnWithinBudget = null;
    int? oldestAssistantWithinBudget = null;
    int? newestTurn = null;
    int? newestAssistantBeforeLatestTurn = null;
    long retainedTokens = 0;

    // Walk backwards to count each suffix once, without allocating a suffix-token array.
    // Index zero cannot remove any history, so it is never a valid cut.
    for (int index = history.Messages.Count - 1; index > 0; index--) {
      HistoryMessage message = history.Messages[index];
      retainedTokens = ContextTokenBudget.AddSaturating(retainedTokens,
          ContextTokenBudget.EstimateMessage(message.Message));
      if (!IsAfterPreviousCompaction(history, message)) continue;

      bool withinBudget = retainedTokens <= retainedHistoryTokenLimit;
      if (message.IsUserTurnStart) {
        newestTurn ??= index;
        if (withinBudget) oldestTurnWithinBudget = index;
        continue;
      }

      if (!message.IsAssistantContinuation) continue;
      if (index <= latestUserTurnIndex) newestAssistantBeforeLatestTurn ??= index;
      if (withinBudget) oldestAssistantWithinBudget = index;
    }

    // Prefer whole turns, then split an oversized turn before an assistant message.
    if (oldestTurnWithinBudget is { } turnIndex) return turnIndex;
    if (oldestAssistantWithinBudget is { } assistantIndex) return assistantIndex;

    // Only an explicit provider overflow may ignore the retained-history token limit.
    if (reason != CompactionReason.Overflow) return null;
    return newestTurn ?? newestAssistantBeforeLatestTurn;
  }

  private static bool IsAfterPreviousCompaction(ModelHistory history, HistoryMessage message) =>
      history.PreviousCompaction is not { } previous ||
      message.Location.IsAfter(previous.FirstRetainedMessageLocation);

  private static int FindLatestUserTurnIndex(IReadOnlyList<HistoryMessage> messages) {
    for (int index = messages.Count - 1; index >= 0; index--)
      if (messages[index].IsUserTurnStart) return index;
    return messages.Count - 1;
  }
}
