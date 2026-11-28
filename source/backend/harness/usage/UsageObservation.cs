using System.Diagnostics;
using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Storage;

namespace Urd.Core.Usage;

// One model exchange. Progress replaces its own amount; final usage never adds a second charge.
internal sealed class UsageObservation {
  private readonly UsageStorage storage;
  private readonly ProviderConnection connection;
  private readonly ModelPriceCatalog? prices;
  private readonly PromptCacheCapture? promptCache;
  private readonly UsageTokenCounter counter;
  private readonly long? estimatedInput;
  private readonly Stopwatch checkpoint = Stopwatch.StartNew();
  private UsageRequest request;
  private UsageDetails? reported;
  private string? reportedModel;
  private bool finished, responseObserved, terminalObserved, modelChanged;
  private int usageReports;

  internal UsageObservation(UsageStorage storage, ProviderConnection connection, string model,
      UsageContext context, IReadOnlyList<ChatMessage> messages, ChatOptions options, ModelPriceCatalog? prices,
      PromptCacheCapture? promptCache = null) {
    this.storage = storage;
    this.connection = connection;
    this.prices = prices;
    this.promptCache = promptCache;
    ArgumentException.ThrowIfNullOrWhiteSpace(model);
    ArgumentNullException.ThrowIfNull(context);
    ArgumentException.ThrowIfNullOrWhiteSpace(context.Purpose);
    counter = new(model);
    estimatedInput = counter.CountInput(messages, options);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    request = new(Guid.NewGuid(), context.SessionId, connection.Name, model, context.Purpose, now, null,
        null, true, "Live usage is provisional.", null, FindRates(model),
        connection.Id, context.ExecutionId);
    request = Snapshot(final: false);
    storage.Begin(request);
  }

  internal void Append(ChatResponseUpdate update) {
    if (finished) throw new InvalidOperationException("Usage is already settled.");
    if (!string.IsNullOrWhiteSpace(update.ModelId)) {
      modelChanged |= reportedModel is not null && reportedModel != update.ModelId;
      reportedModel = update.ModelId;
      if (update.ModelId != request.Model)
        request = request with { Model = update.ModelId, Rates = FindRates(update.ModelId) };
    }
    terminalObserved |= update.FinishReason is not null;
    responseObserved |= terminalObserved
        || update.Contents.Any(content => content is TextContent or TextReasoningContent or FunctionCallContent);
    counter.Append(update);
    foreach (UsageContent usage in update.Contents.OfType<UsageContent>()) {
      usageReports++;
      (reported ??= new()).Add(usage.Details);
    }
    // No per-token writes or prefix tokenization. Final observations always bypass this throttle.
    if (checkpoint.ElapsedMilliseconds >= 250 || update.Contents.Any(content => content is UsageContent)) {
      request = Snapshot(final: false);
      storage.Update(request);
      checkpoint.Restart();
    }
  }

  internal void Finish(bool streamEnded = false) {
    if (finished) return;
    if (request.Rates is null && prices is not null)
      request = request with { Rates = prices.FindRates(connection, request.Model, allowRefresh: false) };
    request = Snapshot(final: true) with { FinishedAt = DateTimeOffset.UtcNow };
    storage.Update(request);
    finished = true;
    // The full-replay routes report one terminal usage snapshot, not deltas to sum for eligibility.
    promptCache?.Complete(reported?.InputTokenCount, request.Tokens?.CachedInputTokens,
        successful: streamEnded && terminalObserved && usageReports == 1 && !request.IsEstimate && !modelChanged,
        reportedModel);
  }

  private UsageRates? FindRates(string model) => prices is null
      ? ModelPriceCatalog.Bundled.FindRates(connection, model)
      : prices.FindRates(connection, model);

  private UsageRequest Snapshot(bool final) {
    long? cached = reported?.CachedInputTokenCount;
    long? write30 = Additional("cache_write_30m_tokens");
    long? write12 = Additional("cache_write_12h_tokens");
    bool invalidCached = Additional(ProviderTokenUsage.InvalidCachedInput) is > 0 ||
        cached < 0 || cached > reported?.InputTokenCount;
    bool invalidWrites = Additional(ProviderTokenUsage.InvalidCacheWrites) is > 0 || write30 < 0 || write12 < 0 ||
        (decimal)(write30 ?? 0) + (write12 ?? 0) + (invalidCached ? 0 : cached ?? 0) > reported?.InputTokenCount;
    var tokens = new UsageTokens(reported?.InputTokenCount ?? estimatedInput,
        reported?.OutputTokenCount ?? counter.OutputTokens, invalidCached ? null : cached,
        invalidWrites ? null : write30, invalidWrites ? null : write12,
        reported?.ReasoningTokenCount, reported?.TotalTokenCount);
    bool hasCompleteReportedUsage = reported?.InputTokenCount is not null && reported.OutputTokenCount is not null
        && tokens.CachedInputTokens is >= 0 && tokens.CacheWrite30mTokens is >= 0 && tokens.CacheWrite12hTokens is >= 0;
    // Quote missing cache decisions provisionally, but never persist an unknown quantity as zero.
    UsageTokens quote = tokens with {
      CachedInputTokens = tokens.CachedInputTokens ?? 0,
      CacheWrite30mTokens = tokens.CacheWrite30mTokens ?? 0,
      CacheWrite12hTokens = tokens.CacheWrite12hTokens ?? 0
    };
    bool hasUnsupportedPricingTokens = Additional("unsupported_pricing_tokens") is > 0;
    bool noEvidence = final && reported is null && !responseObserved;
    long? nanoDollars = hasUnsupportedPricingTokens || invalidCached || invalidWrites || noEvidence
        ? null : UsagePricing.NanoDollars(request.Rates, quote);
    string? problem = ExplainProblem(final, hasCompleteReportedUsage, hasUnsupportedPricingTokens, nanoDollars);
    return request with {
      Tokens = tokens,
      CostUsd = nanoDollars / 1_000_000_000m,
      IsEstimate = !final || !hasCompleteReportedUsage,
      Problem = problem
    };
  }

  private string? ExplainProblem(bool final, bool hasCompleteReportedUsage, bool hasUnsupportedPricingTokens, long? nanoDollars) {
    if (request.Rates is null) return "No verified API-equivalent rate for this model.";
    if (hasUnsupportedPricingTokens) return "Usage includes unsupported pricing dimensions.";
    if (nanoDollars is null) return "Usage quantities or applicable rates are incomplete or invalid.";
    if (hasCompleteReportedUsage) return null;
    return final ? "Provider did not report complete final usage."
        : "Live estimate; cache, framing and hidden output usage are not yet confirmed.";
  }

  private long? Additional(string name) => reported?.AdditionalCounts is { } counts
      && counts.TryGetValue(name, out long count) ? count : null;
}
