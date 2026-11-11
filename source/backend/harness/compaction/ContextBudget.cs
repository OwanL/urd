using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Urd.Core.Compaction;

internal static class ContextBudget {
  internal const int RecentLimit = 20_000;
  internal const int OutputLimit = 8_192;

  internal static long Estimate(IEnumerable<ChatMessage> messages) {
    ArgumentNullException.ThrowIfNull(messages);
    long total = 0;
    foreach (ChatMessage message in messages)
      total = Add(total, Count(message));
    return total;
  }

  internal static long Count(ChatMessage message) {
    ArgumentNullException.ThrowIfNull(message);
    long total = 8 + CountText(message.Role.ToString()) + CountText(message.AuthorName);
    if (message.RawRepresentation is not null) total = Add(total, 8);
    foreach (AIContent content in message.Contents)
      total = Add(total, Count(content));
    return total;
  }

  internal static int Threshold(int window) {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(window);
    return window - Math.Min(16_384, Math.Max(1, window / 5));
  }

  internal static int Recent(int? window) => window is > 0
      ? Math.Min(RecentLimit, Math.Max(1, Threshold(window.Value) / 2))
      : RecentLimit;

  internal static int OutputBudget(int? window) => window is > 0
      ? Math.Min(OutputLimit, Math.Max(1, window.Value / 4))
      : OutputLimit;

  internal static long CountText(string? text) => text is null ? 0 : ((long)text.Length + 3) / 4;

  internal static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;

  private static long Count(AIContent content) {
    long total = content.RawRepresentation is null ? 0 : 8;
    return content switch {
      TextContent text => Add(total, CountText(text.Text)),
      TextReasoningContent reasoning => Add(total, reasoning.ProtectedData is null
          ? CountText(reasoning.Text) : 1_024),
      DataContent => Add(total, 1_024),
      FunctionCallContent call => Add(total, CountJson(call.Arguments)),
      FunctionResultContent result => Add(total, CountResult(result.Result)),
      _ => Add(total, 8)
    };
  }

  private static long CountResult(object? result) {
    if (result is string text) return CountText(text);
    if (result is IEnumerable<AIContent> contents) {
      long total = 0;
      foreach (AIContent content in contents)
        total = Add(total, Count(content));
      return total;
    }
    return CountJson(result);
  }

  private static long CountJson(object? value) {
    if (value is null) return 0;
    try {
      return CountText(JsonSerializer.Serialize(value));
    } catch (NotSupportedException) {
      return 8;
    } catch (JsonException) {
      return 8;
    }
  }
}
