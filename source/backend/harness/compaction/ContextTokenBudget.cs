using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Urd.Core.Compaction;

// Cheap heuristic estimates, not provider-tokenizer counts. Opaque content uses fixed allowances.
internal static class ContextTokenBudget {
  private const int MessageOverheadTokens = 8; // Allowance for message framing, separate from role and content.
  private const int RawRepresentationOverheadTokens = 8; // Extra allowance when a message or content has provider-native data.
  private const int FallbackEstimateTokens = 8; // Used for unknown content types or values that cannot be serialized.
  private const int ProtectedReasoningEstimateTokens = 1_024; // Stand-in for reasoning whose protected contents cannot be counted.
  private const int DataContentEstimateTokens = 1_024; // Fixed allowance per binary content item, such as an image.
  private const int CharactersPerEstimatedToken = 4; // Rough text-to-token ratio; estimates round up.
  private const int MaximumHeadroomTokens = 16_384; // Caps the space left unused at the compaction trigger.
  private const int HeadroomWindowDivisor = 5; // Reserve one fifth of the context window, subject to the headroom cap.
  private const int RetainedHistoryTriggerDivisor = 2; // Keep recent history within half the compaction trigger's token count.
  private const int SummaryOutputWindowDivisor = 4; // Limit summary output to one quarter of the context window, subject to its cap.
  private const int MaximumRetainedHistoryTokens = 20_000; // Cap on recent history kept alongside the summary; also the fallback limit.
  private const int MaximumSummaryOutputTokens = 8_192; // Summary output cap; also used when the context window is unknown.
  private const int MinimumBudgetTokens = 1; // Prevent headroom and summary output budgets from rounding down to zero.

  internal static long EstimateMessages(IEnumerable<ChatMessage> messages) {
    ArgumentNullException.ThrowIfNull(messages);
    long total = 0;
    foreach (ChatMessage message in messages)
      total = AddSaturating(total, EstimateMessage(message));
    return total;
  }

  internal static long EstimateMessage(ChatMessage message) {
    ArgumentNullException.ThrowIfNull(message);
    long total = MessageOverheadTokens + EstimateText(message.Role.ToString()) + EstimateText(message.AuthorName);
    if (message.RawRepresentation is not null) total = AddSaturating(total, RawRepresentationOverheadTokens);
    foreach (AIContent content in message.Contents)
      total = AddSaturating(total, EstimateContent(content));
    return total;
  }

  internal static int? CompactionTrigger(int? contextWindowTokens, int? preferredTokenCount = null) {
    if (preferredTokenCount is <= 0)
      throw new ArgumentOutOfRangeException(nameof(preferredTokenCount));
    if (contextWindowTokens is not > 0) return preferredTokenCount;

    int headroomTokens = Math.Min(MaximumHeadroomTokens,
        Math.Max(MinimumBudgetTokens, contextWindowTokens.Value / HeadroomWindowDivisor));
    int maximumTrigger = contextWindowTokens.Value - headroomTokens;
    return Math.Min(preferredTokenCount ?? maximumTrigger, maximumTrigger);
  }

  internal static int RetainedHistoryLimit(int? compactionTriggerTokenCount) => compactionTriggerTokenCount is { } trigger
      ? Math.Min(MaximumRetainedHistoryTokens, trigger / RetainedHistoryTriggerDivisor)
      : MaximumRetainedHistoryTokens;

  internal static int SummaryOutputLimit(int? contextWindowTokens) => contextWindowTokens is > 0
      ? Math.Min(MaximumSummaryOutputTokens,
          Math.Max(MinimumBudgetTokens, contextWindowTokens.Value / SummaryOutputWindowDivisor))
      : MaximumSummaryOutputTokens;

  internal static long EstimateText(string? text) => text is null
      ? 0
      : ((long)text.Length + CharactersPerEstimatedToken - 1) / CharactersPerEstimatedToken;

  internal static long AddSaturating(long left, long right) =>
      left > long.MaxValue - right ? long.MaxValue : left + right;

  private static long EstimateContent(AIContent content) {
    long total = content.RawRepresentation is null ? 0 : RawRepresentationOverheadTokens;
    return content switch {
      TextContent text => AddSaturating(total, EstimateText(text.Text)),
      TextReasoningContent reasoning => AddSaturating(total, reasoning.ProtectedData is null
          ? EstimateText(reasoning.Text) : ProtectedReasoningEstimateTokens),
      DataContent => AddSaturating(total, DataContentEstimateTokens),
      FunctionCallContent call => AddSaturating(total, EstimateJson(call.Arguments)),
      FunctionResultContent result => AddSaturating(total, EstimateResult(result.Result)),
      _ => AddSaturating(total, FallbackEstimateTokens)
    };
  }

  private static long EstimateResult(object? result) {
    if (result is string text) return EstimateText(text);
    if (result is IEnumerable<AIContent> contents) {
      long total = 0;
      foreach (AIContent content in contents)
        total = AddSaturating(total, EstimateContent(content));
      return total;
    }
    return EstimateJson(result);
  }

  private static long EstimateJson(object? value) {
    if (value is null) return 0;
    try {
      return EstimateText(JsonSerializer.Serialize(value));
    } catch (NotSupportedException) {
      return FallbackEstimateTokens;
    } catch (JsonException) {
      return FallbackEstimateTokens;
    }
  }
}
