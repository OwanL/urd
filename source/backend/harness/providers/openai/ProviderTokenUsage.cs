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

  internal static UsageDetails? Response(ResponseTokenUsage? usage) {
    if (usage is null) return null;
    JsonPatch inputDetails = usage.InputTokenDetails?.Patch ?? default;
    var details = new UsageDetails {
      InputTokenCount = Tokens(usage.Patch, "$.input_tokens"u8),
      OutputTokenCount = Tokens(usage.Patch, "$.output_tokens"u8),
      TotalTokenCount = Tokens(usage.Patch, "$.total_tokens"u8),
      CachedInputTokenCount = usage.InputTokenDetails is null ? null : Tokens(inputDetails, "$.cached_tokens"u8),
      ReasoningTokenCount = usage.OutputTokenDetails is null ? null :
          Tokens(usage.OutputTokenDetails.Patch, "$.reasoning_tokens"u8),
      InputAudioTokenCount = usage.InputTokenDetails is null ? null : Tokens(inputDetails, "$.audio_tokens"u8),
      OutputAudioTokenCount = usage.OutputTokenDetails is null ? null :
          Tokens(usage.OutputTokenDetails.Patch, "$.audio_tokens"u8)
    };
    return EnrichMetadata(details, inputDetails, usage.InputTokenDetails is not null);
  }

  internal static UsageDetails? Chat(ChatTokenUsage? usage) {
    if (usage is null) return null;
    JsonPatch inputDetails = usage.InputTokenDetails?.Patch ?? default;
    var details = new UsageDetails {
      InputTokenCount = Tokens(usage.Patch, "$.prompt_tokens"u8),
      OutputTokenCount = Tokens(usage.Patch, "$.completion_tokens"u8),
      TotalTokenCount = Tokens(usage.Patch, "$.total_tokens"u8),
      CachedInputTokenCount = usage.InputTokenDetails is null ? null : Tokens(inputDetails, "$.cached_tokens"u8),
      ReasoningTokenCount = usage.OutputTokenDetails is null ? null :
          Tokens(usage.OutputTokenDetails.Patch, "$.reasoning_tokens"u8),
      InputAudioTokenCount = usage.InputTokenDetails is null ? null : Tokens(inputDetails, "$.audio_tokens"u8),
      OutputAudioTokenCount = usage.OutputTokenDetails is null ? null :
          Tokens(usage.OutputTokenDetails.Patch, "$.audio_tokens"u8)
    };
    return EnrichMetadata(details, inputDetails, usage.InputTokenDetails is not null);
  }

  internal static ProviderUsage? ToProviderUsage(UsageDetails? usage) => usage is null ? null :
      new(usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount, usage.CachedInputTokenCount);

  private static UsageDetails EnrichMetadata(UsageDetails usage, JsonPatch inputDetails, bool hasInputDetails) {
    var writes = hasInputDetails ? CacheWrites(inputDetails, usage.InputTokenCount, usage.CachedInputTokenCount) :
        (ThirtyMinutes: 0L, TwelveHours: 0L);
    var additionalCounts = new AdditionalPropertiesDictionary<long> {
      ["cache_write_30m_tokens"] = writes.ThirtyMinutes,
      ["cache_write_12h_tokens"] = writes.TwelveHours
    };
    if (usage.InputAudioTokenCount > 0 || usage.OutputAudioTokenCount > 0)
      additionalCounts["unsupported_pricing_tokens"] = 1;
    usage.AdditionalCounts = additionalCounts;
    return usage;
  }

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

  private static long? Tokens(JsonPatch patch, ReadOnlySpan<byte> path) =>
      patch.TryGetValue(path, out long value) && value >= 0 ? value : null;
}

#pragma warning restore OPENAI001, SCME0001, MEAI001
