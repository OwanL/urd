using Microsoft.Extensions.AI;
using Urd.Core.Usage;
using Xunit;

namespace Urd.Backend.Tests.Usage;

public sealed class UsagePricingTests {
  private static readonly DateTimeOffset CapturedAt = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

  [Fact]
  public void NanoDollarsPricesExclusiveInputBucketsAndOutputOnce() {
    var rates = new UsageRates("fixture-model", "test", CapturedAt,
        Input: 1m, Output: 2m, CachedInput: .1m,
        CacheWrite30m: 1.25m, CacheWrite12h: 2.5m);
    var tokens = new UsageTokens(10, 10, 1, 2, 3);

    Assert.Equal(34_100L, UsagePricing.NanoDollars(rates, tokens));
  }

  [Fact]
  public void LongContextRateAppliesToWholeRequestOnlyAboveThreshold() {
    var rates = new UsageRates("fixture-model", "test", CapturedAt, 2.5m, 15m, .25m,
        LongContextThreshold: 272_000, LongInput: 5m, LongOutput: 22.5m, LongCachedInput: .5m);

    Assert.Equal(680_015_000L, UsagePricing.NanoDollars(rates, new UsageTokens(272_000, 1, 0)));
    Assert.Equal(1_360_027_500L, UsagePricing.NanoDollars(rates, new UsageTokens(272_001, 1, 0)));
  }

  [Fact]
  public void NanodollarRoundingUsesMidpointsAwayFromZero() {
    var rates = new UsageRates("fixture-model", "test", CapturedAt, .0005m, 0m, 0m);

    Assert.Equal(1L, UsagePricing.NanoDollars(rates, new UsageTokens(1, 0, 0)));
  }

  [Fact]
  public void MissingInvalidOrOverlappingUsageAndRatesStayUnknown() {
    var rates = new UsageRates("fixture-model", "test", CapturedAt, 1m, 1m, .5m);
    Assert.Null(UsagePricing.NanoDollars(rates, new UsageTokens(null, 1, 0)));
    Assert.Null(UsagePricing.NanoDollars(rates, new UsageTokens(1, null, 0)));
    Assert.Null(UsagePricing.NanoDollars(rates, new UsageTokens(1, 1, null)));
    Assert.Null(UsagePricing.NanoDollars(rates, new UsageTokens(1, 1, -1)));
    Assert.Null(UsagePricing.NanoDollars(rates, new UsageTokens(1, 1, 1, 1)));
    Assert.Null(UsagePricing.NanoDollars(rates with { CachedInput = null }, new UsageTokens(1, 1, 1)));
    Assert.Null(UsagePricing.NanoDollars(rates, new UsageTokens(1, 1, 0, 1)));

    UsageRates longRatesWithoutLongInput = rates with { LongContextThreshold = 272_000 };
    Assert.Null(UsagePricing.NanoDollars(longRatesWithoutLongInput, new UsageTokens(272_001, 1, 0)));
  }

  [Fact]
  public void StreamedOutputCountingDoesNotDependOnChunkBoundaries() {
    var single = new UsageTokenCounter("gpt-4o");
    single.Append(Update("Hello, tokenizer world!"));

    var partitioned = new UsageTokenCounter("gpt-4o");
    partitioned.Append(Update("Hello"));
    partitioned.Append(Update(", "));
    partitioned.Append(Update("tokenizer "));
    partitioned.Append(Update("world!"));

    Assert.Equal(single.OutputTokens, partitioned.OutputTokens);
    Assert.NotNull(single.OutputTokens);
    Assert.Null(new UsageTokenCounter("unknown-model").OutputTokens);
  }

  [Fact]
  public void ImagesAndNativeOpaqueMessagesDoNotUseCharacterHeuristics() {
    var counter = new UsageTokenCounter("gpt-4o");
    Assert.Null(counter.CountInput([new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")])], new ChatOptions()));

    var opaque = new ChatMessage(ChatRole.User, "text") { RawRepresentation = new object() };
    Assert.Null(counter.CountInput([opaque], new ChatOptions()));
  }

  private static ChatResponseUpdate Update(string text) => new() {
    Contents = [new TextContent(text)]
  };
}
