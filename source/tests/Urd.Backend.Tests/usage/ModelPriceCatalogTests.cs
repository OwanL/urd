using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Core.Usage;
using Xunit;
using static Urd.Backend.Tests.Support.ModelsDevFeed;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Usage;

public sealed class ModelPriceCatalogTests {
  private static readonly ProviderConnection OpenAI = Connection("https://api.openai.com/v1/", ProviderRoute.OpenAI);
  private static readonly ProviderConnection Copilot = Connection("https://api.githubcopilot.com/v1");
  private static readonly ProviderConnection Ollama = Connection("https://ollama.com/v1");
  private static readonly ProviderConnection Other = Connection("https://fixture.invalid/v1");

  [Fact]
  public void BundledRatesShareOneReferenceAndKeepVersionAndVariantBoundaries() {
    ModelPriceSnapshot snapshot = ModelPriceCatalog.Bundled;
    var sol = Assert.IsType<UsageRates>(snapshot.FindRates(OpenAI, "gpt-6.2-sol"));
    Assert.Equal("openai/gpt-6.2-sol", sol.Model);
    Assert.Contains("#openai/gpt-6.2-sol", sol.Source);
    Assert.Equal((2m, 10m, 272_000L), (sol.Input, sol.Output, sol.LongContextThreshold));
    Assert.Equal((2.5m, 5m, 5m, 10m), (sol.CacheWrite30m, sol.CacheWrite12h, sol.LongCacheWrite30m, sol.LongCacheWrite12h));
    Assert.Equal(sol, snapshot.FindRates(Copilot, "6.2sol"));
    Assert.Equal(sol, snapshot.FindRates(OpenAI, "6.2-sol"));
    Assert.Equal(sol, snapshot.FindRates(Other, "GPT-6.2-Sol"));
    Assert.Null(snapshot.FindRates(Other, "6.2sol"));
    Assert.Null(snapshot.FindRates(OpenAI, "GPT-62-Sol"));
    Assert.Null(snapshot.FindRates(OpenAI, "GPT-6.2-Sol-Pro"));

    var beam = Assert.IsType<UsageRates>(snapshot.FindRates(Ollama, "beam"));
    Assert.Contains("#openrouter/", beam.Source);
    Assert.Equal(beam, snapshot.FindRates(Other, "reflection/beam-1.0-501b-a23b"));
    Assert.Equal(0.25m, beam.Input);
    var flash = Assert.IsType<UsageRates>(snapshot.FindRates(Ollama, "deepseek-v4.1-flash:1017"));
    Assert.Contains("#mistral/", flash.Source); // The maker and OpenRouter listings switch rates by time of day.
    Assert.Equal(.3m, flash.Input);
    var grok = Assert.IsType<UsageRates>(snapshot.FindRates(Copilot, "grok-4.5"));
    Assert.Equal(.3m, grok.CachedInput);
    Assert.Equal(199_999, grok.LongContextThreshold); // This source uses >=, not >.
    var opus = Assert.IsType<UsageRates>(snapshot.FindRates(Copilot, "claude-opus-4.8"));
    Assert.Null(opus.CacheWrite30m); // Anthropic's 5-minute bucket is not the OpenAI 30-minute bucket.
    Assert.Equal(5m, opus.Input);
    Assert.Equal(opus, snapshot.FindRates(Copilot, "Claude Opus 4.8 (fast mode) (preview)"));
    Assert.Contains("#github-copilot/mai-code-2-flash", snapshot.FindRates(Copilot, "mai-code-2-flash")?.Source);
  }

  [Fact]
  public void NormalizedCollisionIsUnknownEvenWhenOneCandidateIsUnpriced() {
    var date = ModelPriceCatalog.Bundled.Data.CapturedAt;
    var rate = new UsageRates("maker/one", "test", date, 1, 1, null);
    var snapshot = new ModelPriceSnapshot(new(ModelPriceData.CurrentVersion, date, [rate], [
        new("", "GPT-6.2 Sol", "maker/one"), new("", "GPT 6.2-sol", "other/two"),
        new("ollama-cloud", "gpt-6.2-sol", "maker/one", IsId: true),
        new("ollama-cloud", "gpt-6.2-sol", "other/two")
    ]));
    Assert.Null(snapshot.FindRates(Other, "GPT-6.2 Sol"));
    Assert.Equal(rate, snapshot.FindRates(Ollama, "gpt-6.2-sol"));
    Assert.Null(snapshot.FindRates(Ollama, "gpt-6.2sol"));
  }

  [Fact]
  public async Task RefreshIsSharedPersistsAcrossReopenAndDoesNotReplaceLastGoodOnInvalidFeed() {
    using var profile = new TemporaryDatabase("urd-model-prices");
    string path = Path.ChangeExtension(profile.Path, ".prices.json");
    var clock = new ManualClock();
    var entered = Signal();
    var release = Signal();
    using var handler = new ModelsDevFeed(async (call, token) => {
      if (call > 1) return "{\"openai\":{\"models\":{}}}";
      entered.TrySetResult();
      await release.Task.WaitAsync(token);
      return ChatCatalog;
    });
    await using (var catalog = new ModelPriceCatalog(path, handler, clock)) {
      Assert.Null(catalog.FindRates(Ollama, "fixture-chat"));
      await entered.Task.WaitAsync(TestTimeout);
      for (int i = 0; i < 5; i++) Assert.Null(catalog.FindRates(OpenAI, $"missing-{i}"));
      Assert.Equal(1, handler.Calls);
      release.TrySetResult();
      await catalog.RefreshAsync().WaitAsync(TestTimeout);
      Assert.Equal(1, handler.Calls);
      var rate = Assert.IsType<UsageRates>(catalog.FindRates(Ollama, "fixture-chat"));
      Assert.Equal(3m, rate.Input);
      Assert.Contains("#openrouter/fixture/chat-model", rate.Source);
      Assert.Equal(1m, catalog.FindRates(Ollama, "fixed-chat")?.Input);

      clock.Now = clock.Now.AddHours(2);
      Assert.Null(catalog.FindRates(Ollama, "missing"));
      await catalog.RefreshAsync().WaitAsync(TestTimeout);
      Assert.Equal(2, handler.Calls);
      Assert.Equal(rate, catalog.FindRates(Ollama, "fixture-chat"));
    }

    using var offline = new ModelsDevFeed(static (_, _) => throw new InvalidOperationException("Unexpected catalog network call."));
    await using var reopened = new ModelPriceCatalog(path, offline, clock);
    Assert.Equal(3m, reopened.FindRates(Ollama, "fixture-chat")?.Input);
    Assert.Equal(0, offline.Calls);
  }

  [Fact]
  public async Task ShutdownCancelsAndJoinsRefresh() {
    using var profile = new TemporaryDatabase("urd-model-price-shutdown");
    string path = Path.ChangeExtension(profile.Path, ".prices.json");
    var entered = Signal();
    var exited = Signal();
    using var handler = new ModelsDevFeed(async (_, token) => {
      entered.TrySetResult();
      try {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return ChatCatalog;
      } finally {
        exited.TrySetResult();
      }
    });
    await using var catalog = new ModelPriceCatalog(path, handler, new ManualClock());
    _ = catalog.FindRates(Ollama, "missing");
    await entered.Task.WaitAsync(TestTimeout);
    await catalog.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    Assert.True(exited.Task.IsCompletedSuccessfully);
    Assert.False(File.Exists(path));
  }

  [Fact]
  public async Task SettlementCanUseAnArrivedRateButDoesNotChangePinnedOrFinishedQuotes() {
    using var database = new TemporaryDatabase("urd-usage-price-settlement");
    using UrdStore store = UrdStore.Open(database.Path);
    var release = Signal();
    using var handler = new ModelsDevFeed(async (_, token) => {
      await release.Task.WaitAsync(token);
      return ChatCatalog;
    });
    await using var catalog = new ModelPriceCatalog(Path.ChangeExtension(database.Path, ".prices.json"), handler, new ManualClock());
    Guid session = Guid.NewGuid();
    UsageObservation Start(string purpose, ProviderConnection provider, string requested) =>
        new(store.Usage, provider, requested, new(session, null, purpose), [], new ChatOptions(), catalog);

    var pinned = Start("pinned", OpenAI, "gpt-6.2-sol");
    var replaced = Start("replaced", Ollama, "gpt-6.2-sol");
    var early = Start("early", Ollama, "fixture-chat");
    var late = Start("late", Ollama, "fixture-chat");
    early.Append(Reported());
    early.Finish();
    release.TrySetResult();
    await catalog.RefreshAsync().WaitAsync(TestTimeout);
    pinned.Append(Reported());
    pinned.Finish();
    replaced.Append(Reported("fixture-chat"));
    replaced.Finish();
    late.Append(Reported());
    late.Finish();

    var records = store.ReadUsage(session).Recent.ToDictionary(request => request.Purpose);
    Assert.Equal(2m, records["pinned"].Rates?.Input);
    Assert.Equal(7m, catalog.FindRates(OpenAI, "gpt-6.2-sol")?.Input);
    Assert.Equal("fixture-chat", records["replaced"].Model);
    Assert.Equal(0.000016m, records["replaced"].CostUsd);
    Assert.Null(records["early"].CostUsd);
    Assert.Equal("fixture/chat-model", records["late"].Rates?.Model);
    Assert.Equal(records["replaced"].CostUsd, records["late"].CostUsd);
  }

  private static ChatResponseUpdate Reported(string? model = null) => new() {
    ModelId = model,
    Contents = [new UsageContent(new UsageDetails { InputTokenCount = 2, OutputTokenCount = 2, CachedInputTokenCount = 0 })]
  };

  private static ProviderConnection Connection(string endpoint, ProviderRoute route = ProviderRoute.Custom) =>
      new(Guid.NewGuid(), "test", route, LlmApi.OpenAIResponses, new Uri(endpoint), AuthenticationMethod.None);

  private sealed class ManualClock : TimeProvider {
    internal DateTimeOffset Now { get; set; } = ModelPriceCatalog.Bundled.Data.CapturedAt.AddDays(2);
    public override DateTimeOffset GetUtcNow() => Now;
  }

}
