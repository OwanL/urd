using Urd.Backend.Tests.Support;
using Urd.Core.Storage;
using Urd.Core.Usage;
using Xunit;

namespace Urd.Backend.Tests.Usage;

public sealed class UsageStorageTests {
  [Fact]
  public void BeginAndUpdateAreIdempotentAndCompletedRequestsAreImmutable() {
    using var fixture = new Fixture();
    Guid session = Guid.NewGuid();
    DateTimeOffset started = At(1);
    UsageRequest begun = Request(session, started);
    fixture.Usage.Begin(begun);
    fixture.Usage.Begin(begun);

    Assert.Throws<InvalidOperationException>(() => fixture.Usage.Begin(begun with { Provider = "other" }));
    Assert.Throws<InvalidOperationException>(() => fixture.Usage.Update(begun with { Purpose = "changed" }));

    UsageRequest completed = begun with {
      Model = "resolved-model",
      FinishedAt = started.AddMinutes(1),
      CostUsd = 0.000000007m,
      Tokens = new UsageTokens(10, 5, 2),
      Rates = new UsageRates("resolved-model", "test", started, 1m, 2m, .5m)
    };
    fixture.Usage.Update(completed);
    fixture.Usage.Update(completed);

    UsageSummary summary = fixture.Usage.Read(session, null, null);
    Assert.Equal(new UsageTotals(0.000000007m, 1, 0, 0, 0), summary.Totals);
    Assert.Equal(completed, Assert.Single(summary.Recent));
    Assert.Throws<InvalidOperationException>(() => fixture.Usage.Update(completed with { CostUsd = 0.000000008m }));
    Assert.Equal(1, Assert.Single(summary.Breakdown).Totals.Requests);
  }

  [Fact]
  public void RecoveryFinishesActiveRequestsAndPreservesKnownCost() {
    using var fixture = new Fixture();
    Guid session = Guid.NewGuid();
    UsageRequest known = Request(session, At(1)) with { CostUsd = 0.000001002m };
    UsageRequest unknown = Request(session, At(2));
    fixture.Usage.Begin(known);
    fixture.Usage.Begin(unknown);

    fixture.Usage.RecoverInterrupted();
    UsageSummary recovered = fixture.Usage.Read(session, null, null);
    Assert.Equal(new UsageTotals(0.000001002m, 2, 2, 2, 0), recovered.Totals);
    Assert.All(recovered.Recent, request => {
      Assert.NotNull(request.FinishedAt);
      Assert.True(request.IsEstimate);
      Assert.Equal("Host interrupted before final provider usage.", request.Problem);
    });
    Assert.Equal(0.000001002m, recovered.Recent.Single(request => request.Id == known.Id).CostUsd);

    fixture.Usage.RecoverInterrupted();
    UsageSummary recoveredAgain = fixture.Usage.Read(session, null, null);
    Assert.Equal(recovered.Totals, recoveredAgain.Totals);
    Assert.Equal(recovered.Recent, recoveredAgain.Recent);
  }

  [Fact]
  public void ReadsEnforceRangesAndCountUnknownsWithoutLosingKnownAmounts() {
    using var fixture = new Fixture();
    Guid session = Guid.NewGuid(), otherSession = Guid.NewGuid();
    fixture.Usage.Begin(Request(session, At(1)) with { CostUsd = null, IsEstimate = true, FinishedAt = At(1).AddMinutes(1) });
    fixture.Usage.Begin(Request(session, At(2)) with {
      CostUsd = 0.000000001m,
      Problem = "partial provider usage",
      FinishedAt = At(2).AddMinutes(1)
    });
    fixture.Usage.Begin(Request(session, At(3)) with { CostUsd = 3m, FinishedAt = At(3).AddMinutes(1) });
    fixture.Usage.Begin(Request(otherSession, At(2)) with { CostUsd = 100m, FinishedAt = At(2).AddMinutes(1) });

    UsageSummary sessionAll = fixture.Usage.Read(session, null, null);
    Assert.Equal(new UsageTotals(3.000000001m, 3, 1, 2, 0), sessionAll.Totals);
    UsageSummary sessionRange = fixture.Usage.Read(session, At(2), At(2));
    Assert.Equal(new UsageTotals(0.000000001m, 1, 0, 1, 0), sessionRange.Totals);
    UsageSummary globalRange = fixture.Usage.Read(null, At(1), At(3));
    Assert.Equal(new UsageTotals(103.000000001m, 4, 1, 2, 0), globalRange.Totals);

    UsageSummary totalsOnly = fixture.Usage.Read(session, null, null, detail: false);
    Assert.Equal(sessionAll.Totals, totalsOnly.Totals);
    Assert.Empty(totalsOnly.Breakdown);
    Assert.Empty(totalsOnly.History);
    Assert.Empty(totalsOnly.Recent);
    Assert.Throws<ArgumentException>(() => fixture.Usage.Read(null, null, null));
    Assert.Throws<ArgumentException>(() => fixture.Usage.Read(Guid.Empty, null, null));
    Assert.Throws<ArgumentException>(() => fixture.Usage.Read(session, At(1), null));
    Assert.Throws<ArgumentException>(() => fixture.Usage.Read(session, At(2), At(1)));
    Assert.Throws<ArgumentException>(() => fixture.Usage.Read(null, At(1), At(1).AddDays(32)));
  }

  [Fact]
  public void DetailedReadsSplitProviderModelPurposeAndPreserveUnknownCountsInHistory() {
    using var fixture = new Fixture();
    Guid session = Guid.NewGuid();
    DateTimeOffset first = At(1), second = first.AddHours(1), third = first.AddHours(2);
    fixture.Usage.Begin(Request(session, first, "model-a", "provider-a", "chat") with {
      CostUsd = null,
      IsEstimate = true,
      FinishedAt = first.AddMinutes(1)
    });
    fixture.Usage.Begin(Request(session, second, "model-b", "provider-a", "embed") with {
      CostUsd = 0.000000002m,
      Problem = "partial provider usage",
      FinishedAt = second.AddMinutes(1)
    });
    fixture.Usage.Begin(Request(session, third, "model-a", "provider-b", "chat") with {
      CostUsd = 0.000000005m
    });

    UsageSummary summary = fixture.Usage.Read(session, null, null);
    Assert.Equal(new UsageTotals(0.000000007m, 3, 1, 2, 1), summary.Totals);
    Assert.Equal(3, summary.Breakdown.Count);
    Assert.Equal(3, summary.History.Count);
    Assert.Equal(summary.Totals, SumBreakdown(summary.Breakdown));
    Assert.Equal(new[] { first, second, third }, summary.History.Select(point => point.At));
    Assert.All(summary.History, point => {
      Assert.Equal(summary.Breakdown.Select(group => (group.Provider, group.Model, group.Purpose, group.IsOther)),
          point.Breakdown.Select(group => (group.Provider, group.Model, group.Purpose, group.IsOther)));
      Assert.Equal(point.Totals, SumBreakdown(point.Breakdown));
    });
    Assert.Equal(new UsageTotals(0m, 1, 1, 1, 0), summary.History[0].Totals);
    Assert.Equal(new UsageTotals(0.000000002m, 1, 0, 1, 0), summary.History[1].Totals);
    Assert.Equal(new UsageTotals(0.000000005m, 1, 0, 0, 1), summary.History[2].Totals);
    Assert.Equal(summary.History[0].Totals, summary.History[0].Breakdown.Single(group =>
        group.Provider == "provider-a" && group.Model == "model-a" && group.Purpose == "chat").Totals);
    Assert.Equal(summary.History[1].Totals, summary.History[1].Breakdown.Single(group =>
        group.Provider == "provider-a" && group.Model == "model-b" && group.Purpose == "embed").Totals);
    Assert.Equal(summary.History[2].Totals, summary.History[2].Breakdown.Single(group =>
        group.Provider == "provider-b" && group.Model == "model-a" && group.Purpose == "chat").Totals);
  }

  [Fact]
  public void HistoryUsesOccupiedRequestBoundariesAndOmitsEmptyBuckets() {
    using var fixture = new Fixture();
    Guid session = Guid.NewGuid();
    DateTimeOffset first = At(1).AddHours(2), last = At(3).AddHours(5);
    for (int index = 0; index < 2; index++)
      fixture.Usage.Begin(Request(session, first) with { CostUsd = 0.000000001m });
    fixture.Usage.Begin(Request(session, last) with { CostUsd = 0.000000002m });

    UsageSummary summary = fixture.Usage.Read(session, At(1), At(4));
    Assert.Equal(2, summary.History.Count);
    Assert.Equal(new[] { first, last }, summary.History.Select(point => point.At));
    Assert.Equal(new UsageTotals(0.000000002m, 2, 0, 0, 2), summary.History[0].Totals);
    Assert.Equal(new UsageTotals(0.000000002m, 1, 0, 0, 1), summary.History[1].Totals);
    Assert.All(summary.History, point => Assert.Equal(point.Totals, SumBreakdown(point.Breakdown)));
  }

  [Fact]
  public void DetailedReadsBoundBreakdownHistoryAndRecentRecords() {
    using var fixture = new Fixture();
    Guid session = Guid.NewGuid();
    for (int index = 0; index < 70; index++) {
      DateTimeOffset started = At(1).AddSeconds(index);
      fixture.Usage.Begin(Request(session, started, model: $"model-{index:D2}") with {
        CostUsd = 0.000000001m,
        FinishedAt = started.AddSeconds(1)
      });
    }

    UsageSummary summary = fixture.Usage.Read(session, null, null);
    Assert.Equal(new UsageTotals(0.000000070m, 70, 0, 0, 0), summary.Totals);
    Assert.Equal(64, summary.Breakdown.Count);
    UsageBreakdown other = Assert.Single(summary.Breakdown, group => group.IsOther);
    Assert.Equal(("", "", ""), (other.Provider, other.Model, other.Purpose));
    Assert.Equal(new UsageTotals(0.000000007m, 7, 0, 0, 0), other.Totals);
    Assert.Equal(summary.Totals, SumBreakdown(summary.Breakdown));
    Assert.All(summary.History, point => {
      Assert.Equal(64, point.Breakdown.Count);
      Assert.Equal(summary.Breakdown.Select(group => (group.Provider, group.Model, group.Purpose, group.IsOther)),
          point.Breakdown.Select(group => (group.Provider, group.Model, group.Purpose, group.IsOther)));
      Assert.Equal(point.Totals, SumBreakdown(point.Breakdown));
    });
    Assert.Equal(64, summary.History.Count);
    Assert.Equal(At(1).AddSeconds(1), summary.History[0].At);
    Assert.Equal(At(1).AddSeconds(69), summary.History[^1].At);
    Assert.Equal(20, summary.Recent.Count);
    Assert.Equal(70, summary.History.Sum(point => point.Totals.Requests));
    Assert.Equal(0.000000070m, summary.History.Sum(point => point.Totals.CostUsd));
    Assert.Equal(At(1).AddSeconds(69), summary.Recent[0].StartedAt);
    Assert.True(summary.History.Zip(summary.History.Skip(1), (left, right) => left.At < right.At).All(value => value));
  }

  private static UsageTotals SumBreakdown(IEnumerable<UsageBreakdown> groups) {
    UsageTotals[] totals = groups.Select(group => group.Totals).ToArray();
    return new UsageTotals(totals.Sum(value => value.CostUsd), totals.Sum(value => value.Requests),
        totals.Sum(value => value.EstimatedRequests), totals.Sum(value => value.UnknownRequests),
        totals.Sum(value => value.ActiveRequests));
  }

  private static UsageRequest Request(Guid? session, DateTimeOffset started, string model = "model",
      string provider = "provider", string purpose = "chat") =>
      new(Guid.NewGuid(), session, provider, model, purpose, started, null, null, false, null,
          null, null, Guid.NewGuid());

  private static DateTimeOffset At(int day) =>
      new(2026, 10, day, 0, 0, 0, TimeSpan.Zero);

  private sealed class Fixture : IDisposable {
    private readonly TemporaryDatabase file = new("urd-usage-tests");
    private readonly UrdStore store;

    internal Fixture() {
      store = UrdStore.Open(file.Path);
    }

    internal UsageStorage Usage => store.Usage;

    public void Dispose() {
      store.Dispose();
      file.Dispose();
    }
  }
}
