using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;

#pragma warning disable OPENAI001, SCME0001, MEAI001 // Preserve presence from the pinned native SDK model patches.

namespace Urd.Core.Providers;

internal static class ProviderTokenUsage {
  internal const string InvalidCachedInput = "invalid_cached_input_tokens";
  internal const string InvalidCacheWrites = "invalid_cache_write_tokens";

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
    long? inputCount = Tokens(patch, inputTokens);
    long? cached = CacheCount(inputDetails, "$.cached_tokens"u8);
    bool invalidCached = cached < 0 || cached > inputCount;
    var details = new UsageDetails {
      InputTokenCount = inputCount,
      OutputTokenCount = Tokens(patch, outputTokens),
      TotalTokenCount = Tokens(patch, "$.total_tokens"u8),
      CachedInputTokenCount = invalidCached ? null : cached,
      ReasoningTokenCount = Tokens(outputDetails, "$.reasoning_tokens"u8),
      InputAudioTokenCount = Tokens(inputDetails, "$.audio_tokens"u8),
      OutputAudioTokenCount = Tokens(outputDetails, "$.audio_tokens"u8)
    };
    var writes = inputDetails is JsonPatch input ? CacheWrites(input, inputCount, details.CachedInputTokenCount) :
        (ThirtyMinutes: 0L, TwelveHours: 0L);
    var additionalCounts = new AdditionalPropertiesDictionary<long>();
    if (writes is { } buckets) {
      additionalCounts["cache_write_30m_tokens"] = buckets.ThirtyMinutes;
      additionalCounts["cache_write_12h_tokens"] = buckets.TwelveHours;
    } else {
      additionalCounts[InvalidCacheWrites] = 1;
    }
    if (invalidCached) additionalCounts[InvalidCachedInput] = 1;
    if (details.InputAudioTokenCount > 0 || details.OutputAudioTokenCount > 0)
      additionalCounts["unsupported_pricing_tokens"] = 1;
    details.AdditionalCounts = additionalCounts;
    return details;
  }

  internal static ProviderUsage? ToProviderUsage(UsageDetails? usage) => usage is null ? null :
      new(usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount, usage.CachedInputTokenCount);

  private static (long ThirtyMinutes, long TwelveHours)? CacheWrites(JsonPatch patch, long? input, long? cached) {
    if (patch.TryGetJson("$.cache_write_tokens_details"u8, out ReadOnlyMemory<byte> breakdown)) {
      var reader = new Utf8JsonReader(breakdown.Span);
      if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
    }
    long total = CacheCount(patch, "$.cache_write_tokens"u8) ?? 0;
    long? thirtyMinutes = CacheCount(patch, "$.cache_write_tokens_details.ttl_30m_tokens"u8);
    long? twelveHours = CacheCount(patch, "$.cache_write_tokens_details.ttl_12h_tokens"u8);
    if (total < 0 || thirtyMinutes < 0 || twelveHours < 0) return null;
    if (total == 0) return (thirtyMinutes ?? 0) == 0 && (twelveHours ?? 0) == 0 ? (0, 0) : null;
    if (thirtyMinutes is not long shortWrite || twelveHours is not long longWrite ||
        shortWrite > total || longWrite != total - shortWrite || total > input - (cached ?? 0))
      return null;
    return (shortWrite, longWrite);
  }

  // Missing is null; a present malformed count must never be interpreted as an absent cache decision.
  private static long? CacheCount(JsonPatch? patch, ReadOnlySpan<byte> path) {
    if (patch is not JsonPatch details || !details.TryGetJson(path, out ReadOnlyMemory<byte> json)) return null;
    var reader = new Utf8JsonReader(json.Span);
    return reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long value) && value >= 0
        ? value : -1;
  }

  private static long? Tokens(JsonPatch? patch, ReadOnlySpan<byte> path) =>
      patch is JsonPatch details && details.TryGetValue(path, out long value) && value >= 0 ? value : null;
}

#pragma warning restore OPENAI001, SCME0001, MEAI001
