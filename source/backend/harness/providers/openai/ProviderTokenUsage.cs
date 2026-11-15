using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;

#pragma warning disable OPENAI001, SCME0001, MEAI001 // Preserve presence from the pinned native SDK model patches.

namespace Urd.Core.Providers;

internal static class ProviderTokenUsage {
  internal static UsageDetails? Read(object? nativeUpdate) => nativeUpdate switch {
    StreamingResponseCompletedUpdate completed => Response(completed.Response?.Usage),
    StreamingResponseIncompleteUpdate incomplete => Response(incomplete.Response?.Usage),
    StreamingResponseFailedUpdate failed => Response(failed.Response?.Usage),
    StreamingChatCompletionUpdate chat => Chat(chat.Usage),
    _ => null
  };

  internal static UsageDetails? Response(ResponseTokenUsage? usage) => usage is null ? null :
      Create(usage.Patch, "$.input_tokens"u8, "$.output_tokens"u8,
          usage.InputTokenDetails?.Patch, usage.OutputTokenDetails?.Patch);

  internal static UsageDetails? Chat(ChatTokenUsage? usage) => usage is null ? null :
      Create(usage.Patch, "$.prompt_tokens"u8, "$.completion_tokens"u8,
          usage.InputTokenDetails?.Patch, usage.OutputTokenDetails?.Patch);

  private static UsageDetails Create(JsonPatch patch, ReadOnlySpan<byte> inputTokens, ReadOnlySpan<byte> outputTokens,
      JsonPatch? inputDetails, JsonPatch? outputDetails) {
    var details = new UsageDetails {
      InputTokenCount = Tokens(patch, inputTokens),
      OutputTokenCount = Tokens(patch, outputTokens),
      TotalTokenCount = Tokens(patch, "$.total_tokens"u8),
      CachedInputTokenCount = Tokens(inputDetails, "$.cached_tokens"u8),
      ReasoningTokenCount = Tokens(outputDetails, "$.reasoning_tokens"u8),
      InputAudioTokenCount = Tokens(inputDetails, "$.audio_tokens"u8),
      OutputAudioTokenCount = Tokens(outputDetails, "$.audio_tokens"u8)
    };
    var writes = inputDetails is JsonPatch input ? CacheWrites(input, details.InputTokenCount, details.CachedInputTokenCount) :
        (ThirtyMinutes: 0L, TwelveHours: 0L);
    var additionalCounts = new AdditionalPropertiesDictionary<long> {
      ["cache_write_30m_tokens"] = writes.ThirtyMinutes,
      ["cache_write_12h_tokens"] = writes.TwelveHours
    };
    if (details.InputAudioTokenCount > 0 || details.OutputAudioTokenCount > 0)
      additionalCounts["unsupported_pricing_tokens"] = 1;
    details.AdditionalCounts = additionalCounts;
    return details;
  }

  internal static ProviderUsage? ToProviderUsage(UsageDetails? usage) => usage is null ? null :
      new(usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount, usage.CachedInputTokenCount);

  private static (long ThirtyMinutes, long TwelveHours) CacheWrites(JsonPatch patch, long? input, long? cached) {
    bool hasTotal = patch.TryGetValue("$.cache_write_tokens"u8, out long total);
    bool hasThirtyMinutes = patch.TryGetValue("$.cache_write_tokens_details.ttl_30m_tokens"u8, out long thirtyMinutes);
    bool hasTwelveHours = patch.TryGetValue("$.cache_write_tokens_details.ttl_12h_tokens"u8, out long twelveHours);
    if (!hasTotal) return hasThirtyMinutes && thirtyMinutes != 0 || hasTwelveHours && twelveHours != 0 ? (-1, -1) : (0, 0);
    if (total < 0 || thirtyMinutes < 0 || twelveHours < 0) return (-1, -1);
    if (total == 0) return thirtyMinutes == 0 && twelveHours == 0 ? (0, 0) : (-1, -1);
    if (!hasThirtyMinutes || !hasTwelveHours || thirtyMinutes > total || twelveHours != total - thirtyMinutes)
      return (-1, -1);

    long writes = thirtyMinutes + twelveHours;
    if (input is long inputCount && (cached is long cachedCount
          ? cachedCount > inputCount || writes > inputCount - cachedCount
          : writes > inputCount)) return (-1, -1);
    return (thirtyMinutes, twelveHours);
  }

  private static long? Tokens(JsonPatch? patch, ReadOnlySpan<byte> path) =>
      patch is JsonPatch details && details.TryGetValue(path, out long value) && value >= 0 ? value : null;
}

#pragma warning restore OPENAI001, SCME0001, MEAI001
