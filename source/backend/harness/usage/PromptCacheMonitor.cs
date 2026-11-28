using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Urd.Core.Execution;
using Urd.Core.Providers;

namespace Urd.Core.Usage;

// A live session keeps only digests and measured totals. The request body is never retained.
internal sealed class PromptCacheMonitor(TimeProvider? clock = null) {
  private const int MinimumPriorInput = 2048;
  private const int MaximumInputItems = 4096;
  private const decimal WarningShare = 0.60m;
  private const decimal MinimumExpectedShare = 0.65m;
  private static readonly TimeSpan coldGap = TimeSpan.FromMinutes(5);

  private readonly Lock gate = new();
  private readonly TimeProvider clock = clock ?? TimeProvider.System;
  private Attempt? pending;
  private Sample? prior;
  private long input, cached, requests;

  internal static bool Supports(ProviderState provider) => provider.Connection switch {
    { Api: LlmApi.OpenAIChatCompletions } => true,
    { Api: LlmApi.OpenAIResponses, Route: not ProviderRoute.Custom } => true,
    _ => false
  };

  internal Attempt? Begin(ProviderState provider, string model, ContextStamp stamp, JsonElement request) {
    lock (gate) {
      if (pending is not null) Clear();
      if (!Supports(provider) || !TryReadInput(request, provider.Connection.Api, out JsonElement items, out byte[] config)) {
        Clear();
        return null;
      }

      var connection = provider.Connection;
      var scope = new Scope(connection.Id, connection.Route, connection.Api, connection.Endpoint,
          provider.Generation, provider.CredentialVersion, model);
      int itemCount = items.GetArrayLength();
      Attempt? candidate = prior is { } previous && previous.Request.Scope == scope && previous.Request.Stamp == stamp &&
          previous.Request.ItemCount < itemCount && Fresh(previous) && config.AsSpan().SequenceEqual(previous.Request.Config)
          ? previous.Request : null;
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      bool appends = false;
      int index = 0;
      foreach (JsonElement item in items.EnumerateArray()) {
        if (item.ValueKind != JsonValueKind.Object || IsReference(item)) {
          Clear();
          return null;
        }
        Append(hash, JsonMarshal.GetRawUtf8Value(item));
        if (++index == candidate?.ItemCount)
          appends = hash.GetCurrentHash().AsSpan().SequenceEqual(candidate.Input);
      }
      if (!appends) Clear();
      return pending = new(scope, stamp, itemCount, config, hash.GetHashAndReset(), appends);
    }
  }

  internal void Complete(Attempt? attempt, long? inputTokens, long? cachedInputTokens, bool successful,
      string? reportedModel = null) {
    lock (gate) {
      if (!ReferenceEquals(attempt, pending) || attempt is null) return;
      pending = null;
      if (!successful || inputTokens is not > 0 || cachedInputTokens is not >= 0 || cachedInputTokens > inputTokens) {
        Clear();
        return;
      }

      bool comparable = attempt.Appends && prior is { } previous && previous.Model == reportedModel &&
          CanExpectReuse(previous.Input, inputTokens.Value);
      bool seenCacheRead = cachedInputTokens > 0 || comparable && prior!.SeenCacheRead;
      bool qualified = attempt.Scope.Route is ProviderRoute.OpenAI or ProviderRoute.ChatGpt || seenCacheRead;
      if (!comparable || !qualified || long.MaxValue - input < inputTokens.Value) {
        ClearTotals();
      } else {
        input += inputTokens.Value;
        cached += cachedInputTokens.Value;
        requests++;
      }
      prior = new(attempt, inputTokens.Value, seenCacheRead, reportedModel, clock.GetTimestamp());
    }
  }

  internal (ContextStamp Stamp, PromptCacheTotals Totals)? ReadWarning() {
    lock (gate) {
      if (pending is not null || prior is not { } sample || !Fresh(sample) || requests < 2 ||
          cached >= input * WarningShare)
        return null;
      return (sample.Request.Stamp, new(input, cached, requests, requests));
    }
  }

  internal void Reset() {
    lock (gate) Clear();
  }

  private bool Fresh(Sample sample) => clock.GetElapsedTime(sample.Timestamp) < coldGap;

  private void Clear() {
    pending = null;
    prior = null;
    ClearTotals();
  }

  private void ClearTotals() => input = cached = requests = 0;

  private static bool CanExpectReuse(long previous, long current) {
    if (previous < MinimumPriorInput || previous > current) return false;
    // Discount trailing template tokens and round down to the OpenAI cache block size.
    long reusable = (previous - 128) / 128 * 128;
    return reusable >= current * MinimumExpectedShare;
  }

  private static bool TryReadInput(JsonElement request, LlmApi api, out JsonElement items, out byte[] config) {
    items = default;
    config = [];
    if (request.ValueKind != JsonValueKind.Object) return false;
    bool responses = api == LlmApi.OpenAIResponses;
    string inputName = responses ? "input" : "messages";
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (JsonProperty property in request.EnumerateObject()) {
      JsonElement value = property.Value;
      if (property.NameEquals(inputName)) {
        if (items.ValueKind != JsonValueKind.Undefined || value.ValueKind != JsonValueKind.Array) return false;
        items = value;
      } else {
        if (responses && value.ValueKind != JsonValueKind.Null &&
            (property.NameEquals("previous_response_id") || property.NameEquals("conversation")))
          return false;
        Append(hash, JsonMarshal.GetRawUtf8PropertyName(property));
        Append(hash, JsonMarshal.GetRawUtf8Value(value));
      }
    }
    if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is 0 or > MaximumInputItems) return false;
    config = hash.GetHashAndReset();
    return true;
  }

  private static bool IsReference(JsonElement item) => item.TryGetProperty("type", out JsonElement type) &&
      type.ValueKind == JsonValueKind.String && type.ValueEquals("item_reference");

  private static void Append(IncrementalHash hash, ReadOnlySpan<byte> bytes) {
    Span<byte> length = stackalloc byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
    hash.AppendData(length);
    hash.AppendData(bytes);
  }

  internal sealed record Attempt(Scope Scope, ContextStamp Stamp, int ItemCount, byte[] Config, byte[] Input, bool Appends);
  internal sealed record Scope(Guid Connection, ProviderRoute Route, LlmApi Api, Uri Endpoint,
      long Generation, long CredentialVersion, string Model);
  private sealed record Sample(Attempt Request, long Input, bool SeenCacheRead, string? Model, long Timestamp);
}
