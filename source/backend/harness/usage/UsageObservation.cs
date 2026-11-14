using System.Diagnostics;
using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Storage;

namespace Urd.Core.Usage;

// One model exchange. Progress replaces its own amount; final usage never adds a second charge.
internal sealed class UsageObservation {
  private readonly UsageStorage storage;
  private readonly ProviderConnection connection;
  private readonly UsageTokenCounter counter;
  private readonly long? estimatedInput;
  private readonly Stopwatch checkpoint = Stopwatch.StartNew();
  private UsageRequest request;
  private UsageDetails? reported;
  private bool finished, responseObserved;

  internal UsageObservation(UsageStorage storage, LlmSelection selection, Guid? sessionId,
      Guid? executionId, string purpose, IReadOnlyList<ChatMessage> messages, ChatOptions options) {
    this.storage = storage;
    connection = selection.Connection;
    string model = options.ModelId ?? selection.ModelId;
    counter = new(model);
    estimatedInput = counter.CountInput(messages, options);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    request = new(Guid.NewGuid(), sessionId, connection.Name, model, purpose, now, null,
        null, true, "Live usage is provisional.", null, UsagePricing.Find(connection, model, now),
        connection.Id, executionId);
    request = Snapshot(final: false);
    storage.Begin(request);
  }

  internal void Append(ChatResponseUpdate update) {
    if (finished) throw new InvalidOperationException("Usage is already settled.");
    if (!string.IsNullOrWhiteSpace(update.ModelId) && update.ModelId != request.Model)
      request = request with {
        Model = update.ModelId,
        Rates = UsagePricing.Find(connection, update.ModelId, request.StartedAt)
      };
    responseObserved |= update.FinishReason is not null
        || update.Contents.Any(content => content is TextContent or TextReasoningContent or FunctionCallContent);
    counter.Append(update);
    foreach (UsageContent usage in update.Contents.OfType<UsageContent>())
      (reported ??= new()).Add(usage.Details);
    // No per-token writes or prefix tokenization. Final observations always bypass this throttle.
    if (checkpoint.ElapsedMilliseconds >= 250 || update.Contents.Any(content => content is UsageContent)) {
      request = Snapshot(final: false);
      storage.Update(request);
      checkpoint.Restart();
    }
  }

  internal void Finish() {
    if (finished) return;
    request = Snapshot(final: true) with { FinishedAt = DateTimeOffset.UtcNow };
    storage.Update(request);
    finished = true;
  }

  private UsageRequest Snapshot(bool final) {
    var tokens = new UsageTokens(reported?.InputTokenCount ?? estimatedInput,
        reported?.OutputTokenCount ?? counter.OutputTokens, reported?.CachedInputTokenCount,
        Additional("cache_write_30m_tokens"), Additional("cache_write_12h_tokens"),
        reported?.ReasoningTokenCount, reported?.TotalTokenCount);
    bool complete = reported?.InputTokenCount is not null && reported.OutputTokenCount is not null
        && tokens.CachedInputTokens is >= 0 && tokens.CacheWrite30mTokens is >= 0 && tokens.CacheWrite12hTokens is >= 0;
    // Quote missing cache decisions provisionally, but never persist an unknown quantity as zero.
    UsageTokens quote = tokens with {
      CachedInputTokens = tokens.CachedInputTokens ?? 0,
      CacheWrite30mTokens = tokens.CacheWrite30mTokens ?? 0,
      CacheWrite12hTokens = tokens.CacheWrite12hTokens ?? 0
    };
    bool unsupported = Additional("unsupported_pricing_tokens") is > 0;
    bool noEvidence = final && reported is null && !responseObserved;
    long? nanoDollars = unsupported || noEvidence ? null : UsagePricing.NanoDollars(request.Rates, quote);
    string? problem = ExplainProblem(final, complete, unsupported, nanoDollars);
    return request with {
      Tokens = tokens,
      CostUsd = nanoDollars / 1_000_000_000m,
      IsEstimate = !final || !complete,
      Problem = problem
    };
  }

  private string? ExplainProblem(bool final, bool complete, bool unsupported, long? nanoDollars) {
    if (request.Rates is null) return "No verified API rate for this provider/model.";
    if (unsupported) return "Usage includes unsupported pricing dimensions.";
    if (nanoDollars is null) return "Usage quantities are incomplete or invalid.";
    if (complete) return null;
    return final ? "Provider did not report complete final usage."
        : "Live estimate; cache, framing and hidden output usage are not yet confirmed.";
  }

  private long? Additional(string name) => reported?.AdditionalCounts is { } counts
      && counts.TryGetValue(name, out long count) ? count : null;
}
