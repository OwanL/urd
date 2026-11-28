using System.Text.Json;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Usage;
using Xunit;

namespace Urd.Backend.Tests.Usage;

public sealed class PromptCacheMonitorTests {
  private static readonly ContextStamp stamp = new(0, null, 0);

  [Fact]
  public void ExcludesTheColdRequestAndUsesTokenWeightedReuse() {
    var monitor = new PromptCacheMonitor();
    ProviderState provider = State();
    Measure(monitor, provider, ["a"], 4000, 0);
    Measure(monitor, provider, ["a", "b"], 5000, 2000);
    Assert.Null(monitor.ReadWarning());

    Measure(monitor, provider, ["a", "b", "c"], 7000, 4500);
    Assert.Equal(stamp, monitor.ReadWarning()?.Stamp);
    Assert.Equal(new PromptCacheTotals(12_000, 6500, 2, 2), monitor.ReadWarning()?.Totals);
  }

  [Fact]
  public void DoesNotWarnAtTheThreshold() {
    var monitor = new PromptCacheMonitor();
    ProviderState provider = State();
    Measure(monitor, provider, ["a"], 4000, 0);
    Measure(monitor, provider, ["a", "b"], 5000, 3000);
    Measure(monitor, provider, ["a", "b", "c"], 7000, 4200);
    Assert.Null(monitor.ReadWarning());
  }

  [Theory]
  [InlineData("prefix")]
  [InlineData("equal")]
  [InlineData("tools")]
  [InlineData("model")]
  [InlineData("reportedModel")]
  [InlineData("credentials")]
  [InlineData("endpoint")]
  [InlineData("compaction")]
  [InlineData("omissions")]
  [InlineData("largeAppend")]
  [InlineData("smallerInput")]
  public void StartsOverWhenTheNextRequestIsNotComparable(string change) {
    var monitor = new PromptCacheMonitor();
    ProviderState provider = State();
    Warm(monitor, provider);
    Assert.NotNull(monitor.ReadWarning());

    ProviderState nextProvider = change switch {
      "credentials" => provider with { CredentialVersion = 2 },
      "endpoint" => provider with { Connection = provider.Connection with { Endpoint = new("https://other.example.test/") } },
      _ => provider
    };
    string[] items = change switch {
      "prefix" => ["b", "a", "c", "d"],
      "equal" => ["a", "b", "c"],
      _ => ["a", "b", "c", "d"]
    };
    ContextStamp nextStamp = change switch {
      "compaction" => stamp with { CompactionId = Guid.NewGuid() },
      "omissions" => stamp with { ContinuationOmissions = 1 },
      _ => stamp
    };
    var attempt = monitor.Begin(nextProvider, change == "model" ? "other" : "fixture", nextStamp,
        Request(nextProvider, items, reversedTools: change == "tools"));
    long input = change switch { "largeAppend" => 12_000, "smallerInput" => 4999, _ => 5000 };
    monitor.Complete(attempt, input, 0, successful: true, change == "reportedModel" ? "fixture-v2" : null);
    Assert.Null(monitor.ReadWarning());
  }

  [Fact]
  public void MissingUsageFailedRequestsAndColdGapsAreNotMisses() {
    var clock = new TestClock();
    var monitor = new PromptCacheMonitor(clock);
    ProviderState provider = State();
    Warm(monitor, provider);
    Assert.NotNull(monitor.ReadWarning());
    clock.Advance(TimeSpan.FromMinutes(5));
    Assert.Null(monitor.ReadWarning());

    Measure(monitor, provider, ["a", "b", "c", "d"], 5000, 0);
    Measure(monitor, provider, ["a", "b", "c", "d", "e"], 5000, null);
    Measure(monitor, provider, ["a", "b", "c", "d", "e", "f"], 5000, 0);
    Assert.Null(monitor.ReadWarning());
    Measure(monitor, provider, ["a", "b", "c", "d", "e", "f", "g"], 5000, 0, successful: false);
    Measure(monitor, provider, ["a", "b", "c", "d", "e", "f", "g", "h"], 5000, 0);
    Assert.Null(monitor.ReadWarning());
  }

  [Theory]
  [InlineData(ProviderRoute.Custom, LlmApi.OpenAIChatCompletions)]
  [InlineData(ProviderRoute.GitHubCopilot, LlmApi.OpenAIResponses)]
  public void CompatibleRoutesNeedEvidenceOfAReportedCacheRead(ProviderRoute route, LlmApi api) {
    var monitor = new PromptCacheMonitor();
    ProviderState provider = State(route, api);
    Warm(monitor, provider);
    Assert.Null(monitor.ReadWarning());

    Measure(monitor, provider, ["a", "b", "c", "d"], 5000, 100);
    Assert.Null(monitor.ReadWarning());
    Measure(monitor, provider, ["a", "b", "c", "d", "e"], 5000, 0);
    Assert.Equal(new PromptCacheTotals(10_000, 100, 2, 2), monitor.ReadWarning()?.Totals);
  }

  [Theory]
  [InlineData(ProviderRoute.Custom, LlmApi.OpenAIResponses)]
  [InlineData(ProviderRoute.GitHubCopilot, LlmApi.AnthropicMessages)]
  public void UnsupportedTelemetryDoesNotQualifyEvenWithExplicitCounts(ProviderRoute route, LlmApi api) {
    var monitor = new PromptCacheMonitor();
    ProviderState provider = State(route, api);
    Assert.Null(monitor.Begin(provider, "fixture", stamp, Request(provider, ["a"])));
    Assert.Null(monitor.ReadWarning());
  }

  [Fact]
  public void StatefulResponsesAndOverlapDoNotContribute() {
    var monitor = new PromptCacheMonitor();
    ProviderState provider = State();
    Warm(monitor, provider);
    Assert.NotNull(monitor.ReadWarning());
    using var delta = JsonDocument.Parse("""{"previous_response_id":"resp_fixture","input":[{"role":"user","content":"d"}]}""");
    Assert.Null(monitor.Begin(provider, "fixture", stamp, delta.RootElement));
    Assert.Null(monitor.ReadWarning());

    Measure(monitor, provider, ["a"], 4500, 0);
    var abandoned = monitor.Begin(provider, "fixture", stamp, Request(provider, ["a", "b"]));
    Measure(monitor, provider, ["a", "b", "c"], 5000, 0);
    monitor.Complete(abandoned, 5000, 0, successful: true);
    Measure(monitor, provider, ["a", "b", "c", "d"], 5100, 0);
    Assert.Null(monitor.ReadWarning());
  }

  private static void Warm(PromptCacheMonitor monitor, ProviderState provider) {
    Measure(monitor, provider, ["a"], 5000, 0);
    Measure(monitor, provider, ["a", "b"], 5000, 0);
    Measure(monitor, provider, ["a", "b", "c"], 5000, 0);
  }

  private static void Measure(PromptCacheMonitor monitor, ProviderState provider, string[] items,
      long input, long? cached, bool successful = true) =>
      monitor.Complete(monitor.Begin(provider, "fixture", stamp, Request(provider, items)), input, cached, successful);

  private static JsonElement Request(ProviderState provider, string[] items, bool reversedTools = false) =>
      JsonSerializer.SerializeToElement(new Dictionary<string, object> {
        ["model"] = "fixture",
        [provider.Connection.Api == LlmApi.OpenAIResponses ? "input" : "messages"] =
          items.Select(content => new { role = "user", content }).ToArray(),
        ["tools"] = reversedTools ? new[] { "second", "first" } : new[] { "first", "second" }
      });

  private static ProviderState State(ProviderRoute route = ProviderRoute.OpenAI, LlmApi api = LlmApi.OpenAIResponses) =>
      new(new(Guid.NewGuid(), "Fixture", route, api, new("https://fixture.example.test/"), AuthenticationMethod.None),
          1, 1, null, null, false, null);

  private sealed class TestClock : TimeProvider {
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => ticks;
    internal void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
  }
}
