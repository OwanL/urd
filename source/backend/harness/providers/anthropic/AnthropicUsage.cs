using System.Text.Json;
using Microsoft.Extensions.AI;
using static Urd.Core.Providers.AnthropicJson;

namespace Urd.Core.Providers;

// Claude reports cumulative counts; UsageStream expects additive updates.
internal sealed class AnthropicUsage {
  private long? regularInput, output, cacheReads, cacheWrites;
  private long? input, total;

  internal UsageDetails? Append(JsonElement envelope) {
    if (!envelope.TryGetProperty("usage", out var usage)) return null;
    if (usage.ValueKind != JsonValueKind.Object) throw Invalid("invalid Anthropic usage");
    long? cachedDelta = Read(usage, "cache_read_input_tokens", ref cacheReads);
    long? writeDelta = Read(usage, "cache_creation_input_tokens", ref cacheWrites);
    long? outputDelta = Read(usage, "output_tokens", ref output);
    Read(usage, "input_tokens", ref regularInput);
    long? nextInput = regularInput is long count ? Add(Add(count, cacheReads ?? 0), cacheWrites ?? 0) : null;
    long? inputDelta = Advance(ref input, nextInput);
    long? totalDelta = Advance(ref total, nextInput is long prompt && output is long completion ? Add(prompt, completion) : null);
    var details = new UsageDetails {
      InputTokenCount = inputDelta,
      CachedInputTokenCount = cachedDelta,
      OutputTokenCount = outputDelta,
      TotalTokenCount = totalDelta
    };
    if (writeDelta == 0 && cacheWrites == 0) {
      details.AdditionalCounts = new() { ["cache_write_30m_tokens"] = 0, ["cache_write_12h_tokens"] = 0 };
    } else if (writeDelta > 0) {
      // Claude's 5-minute and 1-hour writes do not correspond to Urd's priced cache buckets.
      details.AdditionalCounts = new() { [ProviderTokenUsage.InvalidCacheWrites] = 1, ["unsupported_pricing_tokens"] = 1 };
    }
    return (inputDelta, cachedDelta, writeDelta, outputDelta, totalDelta) == (null, null, null, null, null) ? null : details;
  }

  private static long? Read(JsonElement usage, string key, ref long? previous) {
    if (!usage.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long count) || count < 0)
      throw Invalid("invalid Anthropic token count");
    return Advance(ref previous, count);
  }

  private static long? Advance(ref long? previous, long? next) {
    if (next is not long count || next == previous) return null;
    if (count < previous) throw Invalid("decreasing cumulative Anthropic token count");
    long delta = count - (previous ?? 0);
    previous = count;
    return delta;
  }

  private static long Add(long count, long delta) => count <= long.MaxValue - delta
      ? count + delta : throw Invalid("Anthropic token count overflow");
}
