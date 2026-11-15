namespace Urd.Core.Compaction;

// Selects the first model-history message to keep verbatim when older history is compacted.
// Messages before that index are summarized. A boundary must advance past the previous
// compaction and must not separate a tool call from its result.
internal static class CompactionBoundarySelector {
  // Returns null when there is no safe boundary. Whole turns are preferred; if none fits
  // the budget, an assistant continuation can split an oversized turn. Only provider overflow
  // may use a boundary that exceeds retainedHistoryTokenLimit.
  internal static int? FindFirstRetainedMessageIndex(ModelHistory history, int retainedHistoryTokenLimit,
      CompactionReason reason) {
    int newestUserTurnIndex = FindNewestUserTurnIndex(history.Messages);

    // The scan visits newest to oldest. Overflow candidates keep their first value;
    // affordable candidates are replaced until the oldest affordable boundary is found.
    int? oldestTurnWithinBudget = null;
    int? oldestAssistantWithinBudget = null;
    int? newestTurnForOverflow = null;
    int? newestAssistantForOverflow = null;
    long suffixTokens = 0;

    // Count each suffix once, without storing token counts for every message. Cutting at
    // index zero would remove no history, so it is never a valid boundary.
    for (int index = history.Messages.Count - 1; index > 0; index--) {
      HistoryMessage message = history.Messages[index];
      suffixTokens = ContextTokenBudget.AddSaturating(suffixTokens,
          ContextTokenBudget.EstimateMessage(message.Message));
      if (!IsAfterPreviousCompaction(history, message)) continue;

      bool suffixWithinBudget = suffixTokens <= retainedHistoryTokenLimit;
      if (message.IsUserTurnStart) {
        newestTurnForOverflow ??= index;
        if (suffixWithinBudget) oldestTurnWithinBudget = index;
        continue;
      }

      if (!message.IsAssistantContinuation) continue;

      // An affordable assistant may split the newest turn. Overflow may not cut away the
      // newest user request, so its assistant fallback must be at or before that turn.
      if (index <= newestUserTurnIndex) newestAssistantForOverflow ??= index;
      if (suffixWithinBudget) oldestAssistantWithinBudget = index;
    }

    // Whole turns take priority over assistant splits, even when the split is newer.
    if (oldestTurnWithinBudget is { } turnIndex) return turnIndex;
    if (oldestAssistantWithinBudget is { } assistantIndex) return assistantIndex;

    if (reason != CompactionReason.Overflow) return null;
    return newestTurnForOverflow ?? newestAssistantForOverflow;
  }

  private static bool IsAfterPreviousCompaction(ModelHistory history, HistoryMessage message) =>
      history.PreviousCompaction is not { } previous ||
      message.Location.IsAfter(previous.FirstRetainedMessageLocation);

  // With no user turn, the final index allows assistant-only history to use its newest
  // assistant continuation as an overflow boundary.
  private static int FindNewestUserTurnIndex(IReadOnlyList<HistoryMessage> messages) {
    for (int index = messages.Count - 1; index >= 0; index--)
      if (messages[index].IsUserTurnStart) return index;
    return messages.Count - 1;
  }
}
