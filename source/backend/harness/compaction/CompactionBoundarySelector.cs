using Microsoft.Extensions.AI;

namespace Urd.Core.Compaction;

internal static class CompactionBoundarySelector {
  internal static int? FindFirstRetainedMessage(ModelHistory history, int retainedHistoryTokenLimit,
      CompactionReason reason) {
    int latestUserTurnIndex = FindLatestUserTurn(history.Messages);
    int? firstTurnWithinBudget = null;
    int? firstAssistantWithinBudget = null;
    int? latestTurnForOverflow = null;
    int? latestAssistantForOverflow = null;
    long retainedTokens = 0;

    // Walk backwards to count each suffix once, without allocating a suffix-token array.
    // Index zero cannot remove any history, so it is never a valid cut.
    for (int index = history.Messages.Count - 1; index > 0; index--) {
      HistoryMessage message = history.Messages[index];
      retainedTokens = ContextTokenBudget.AddSaturating(retainedTokens, ContextTokenBudget.EstimateMessage(message.Message));
      if (history.PreviousCompaction is { } previous && !message.Location.IsAfter(previous.FirstRetainedMessageLocation))
        continue;

      if (message.IsUserTurnStart && index <= latestUserTurnIndex) {
        latestTurnForOverflow ??= index;
        if (retainedTokens <= retainedHistoryTokenLimit) firstTurnWithinBudget = index;
      } else if (message.IsAssistantContinuation) {
        if (index <= latestUserTurnIndex) latestAssistantForOverflow ??= index;
        if (retainedTokens <= retainedHistoryTokenLimit) firstAssistantWithinBudget = index;
      }
    }

    // Prefer whole turns, then split an oversized turn before an assistant message.
    // Only an explicit provider overflow may ignore the retained-history token limit.
    return firstTurnWithinBudget ?? firstAssistantWithinBudget ??
        (reason == CompactionReason.Overflow ? latestTurnForOverflow ?? latestAssistantForOverflow : null);
  }

  private static int FindLatestUserTurn(IReadOnlyList<HistoryMessage> messages) {
    for (int index = messages.Count - 1; index >= 0; index--)
      if (messages[index].IsUserTurnStart) return index;
    return messages.Count - 1;
  }
}
