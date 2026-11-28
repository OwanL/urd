using System.Net;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class SubagentRoutesTests {
  [Fact]
  public async Task LiveListingIsEmptyAndRequiresAnExistingPrimarySession() {
    using var database = new TemporaryDatabase();
    Guid primaryId = Guid.NewGuid();
    Guid childId = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(primaryId);
      store.Sessions.CreateSubagent(childId, primaryId, "retained-call", owningRequestId: Guid.NewGuid());
    }

    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();

    using (HttpResponseMessage live = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{primaryId:D}/subagents/live")) {
      Assert.Equal(HttpStatusCode.OK, live.StatusCode);
      AssertPrivateResponse(live);
      using JsonDocument body = JsonDocument.Parse(await live.Content.ReadAsStringAsync());
      Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
      Assert.Empty(body.RootElement.EnumerateArray());
    }

    using (HttpResponseMessage missing = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{Guid.NewGuid():D}/subagents/live"))
      Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    using (HttpResponseMessage nonprimary = await RequestAsync(server.Client, HttpMethod.Get,
        $"/api/sessions/{childId:D}/subagents/live"))
      Assert.Equal(HttpStatusCode.NotFound, nonprimary.StatusCode);
  }

  [Fact]
  public async Task ByCallFindsRetainedChildrenAtTheirOwningParentOnly() {
    using var database = new TemporaryDatabase();
    Guid primaryId = Guid.NewGuid();
    Guid childId = Guid.NewGuid();
    Guid nestedId = Guid.NewGuid();
    Guid childRequestId = Guid.NewGuid();
    Guid nestedRequestId = Guid.NewGuid();
    const string childCallId = "spawn/Ω";
    const string nestedCallId = "nested, %2F & Ω";
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(primaryId);
      store.Sessions.CreateSubagent(childId, primaryId, childCallId, owningRequestId: childRequestId);
      store.Sessions.CreateSubagent(nestedId, childId, nestedCallId, owningRequestId: nestedRequestId);
    }

    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();

    await AssertSubagentAsync(primaryId, childRequestId, childCallId, childId, primaryId);
    await AssertSubagentAsync(childId, nestedRequestId, nestedCallId, nestedId, childId);

    using (HttpResponseMessage wrongRequest = await RequestAsync(server.Client, HttpMethod.Get,
        PathFor(primaryId, Guid.NewGuid(), childCallId)))
      Assert.Equal(HttpStatusCode.NotFound, wrongRequest.StatusCode);
    using (HttpResponseMessage ancestor = await RequestAsync(server.Client, HttpMethod.Get,
        PathFor(primaryId, nestedRequestId, nestedCallId)))
      Assert.Equal(HttpStatusCode.NotFound, ancestor.StatusCode);

    async Task AssertSubagentAsync(Guid parentId, Guid requestId, string callId, Guid expectedId, Guid expectedParentId) {
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get, PathFor(parentId, requestId, callId));
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      AssertPrivateResponse(response);
      using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
      JsonElement item = body.RootElement;
      Assert.Equal(JsonValueKind.Object, item.ValueKind);
      Assert.Equal(new[] { "cost", "durationIncomplete", "elapsedMilliseconds", "id", "live", "modelId", "parentId",
          "startedAt", "title", "titlePending" },
          item.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
      Assert.Equal(expectedId, item.GetProperty("id").GetGuid());
      Assert.Equal(expectedParentId, item.GetProperty("parentId").GetGuid());
      Assert.Equal("Subagent", item.GetProperty("title").GetString());
      Assert.False(item.GetProperty("titlePending").GetBoolean());
      Assert.False(item.GetProperty("live").GetBoolean());
      Assert.Null(item.GetProperty("modelId").GetString());
      Assert.Equal(JsonValueKind.Null, item.GetProperty("startedAt").ValueKind);
      Assert.Equal(JsonValueKind.Null, item.GetProperty("elapsedMilliseconds").ValueKind);
      Assert.False(item.GetProperty("durationIncomplete").GetBoolean());
      JsonElement cost = item.GetProperty("cost");
      JsonElement own = cost.GetProperty("own");
      Assert.Equal(JsonValueKind.Null, own.GetProperty("usd").ValueKind);
      Assert.False(own.GetProperty("partial").GetBoolean());
      JsonElement descendants = cost.GetProperty("descendants");
      Assert.Equal(JsonValueKind.Null, descendants.GetProperty("usd").ValueKind);
      Assert.False(descendants.GetProperty("partial").GetBoolean());
      JsonElement total = cost.GetProperty("total");
      Assert.Equal(JsonValueKind.Null, total.GetProperty("usd").ValueKind);
      Assert.False(total.GetProperty("partial").GetBoolean());
      Assert.Equal(expectedId == childId, cost.GetProperty("hasDescendants").GetBoolean());
    }
  }

  [Fact]
  public async Task ByCallCostIncludesOnlyTheChildTreeAndPreservesKnownAmountsWhenPartial() {
    using var database = new TemporaryDatabase();
    Guid primaryId = Guid.NewGuid();
    Guid childId = Guid.NewGuid();
    Guid nestedId = Guid.NewGuid();
    Guid leafId = Guid.NewGuid();
    Guid siblingId = Guid.NewGuid();
    Guid childRequestId = Guid.NewGuid();
    Guid leafRequestId = Guid.NewGuid();
    const string childCallId = "costed-child";
    DateTimeOffset started = new(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(primaryId);
      store.Sessions.CreateSubagent(childId, primaryId, childCallId, owningRequestId: childRequestId);
      store.Sessions.CreateSubagent(nestedId, childId, "nested");
      store.Sessions.CreateSubagent(leafId, nestedId, "leaf", owningRequestId: leafRequestId);
      store.Sessions.CreateSubagent(siblingId, primaryId, "sibling");
      RecordUsage(store, childId, started, 0m, "child-model");
      RecordUsage(store, nestedId, started.AddMinutes(1), 0.3m, "nested-model");
      RecordUsage(store, leafId, started.AddMinutes(2), null, "unpriced-model");
      RecordUsage(store, siblingId, started.AddMinutes(3), 5m, "sibling-model");
      RecordUsage(store, primaryId, started.AddMinutes(4), 7m, "primary-model");
    }

    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();

    using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Get,
        PathFor(primaryId, childRequestId, childCallId));
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    JsonElement cost = body.RootElement.GetProperty("cost");
    Assert.Equal(0m, cost.GetProperty("own").GetProperty("usd").GetDecimal());
    Assert.False(cost.GetProperty("own").GetProperty("partial").GetBoolean());
    Assert.Equal(0.3m, cost.GetProperty("descendants").GetProperty("usd").GetDecimal());
    Assert.True(cost.GetProperty("descendants").GetProperty("partial").GetBoolean());
    Assert.Equal(0.3m, cost.GetProperty("total").GetProperty("usd").GetDecimal());
    Assert.True(cost.GetProperty("total").GetProperty("partial").GetBoolean());
    Assert.True(cost.GetProperty("hasDescendants").GetBoolean());

    using HttpResponseMessage unpriced = await RequestAsync(server.Client, HttpMethod.Get,
        PathFor(nestedId, leafRequestId, "leaf"));
    Assert.Equal(HttpStatusCode.OK, unpriced.StatusCode);
    using JsonDocument unpricedBody = JsonDocument.Parse(await unpriced.Content.ReadAsStringAsync());
    JsonElement unpricedTotal = unpricedBody.RootElement.GetProperty("cost").GetProperty("total");
    Assert.Equal(JsonValueKind.Null, unpricedTotal.GetProperty("usd").ValueKind);
    Assert.True(unpricedTotal.GetProperty("partial").GetBoolean());
  }

  private static string PathFor(Guid parentId, Guid requestId, string callId) =>
      $"/api/sessions/{parentId:D}/subagents/by-call/{requestId:D}/{Uri.EscapeDataString(callId)}";

  private static void RecordUsage(UrdStore store, Guid sessionId, DateTimeOffset started, decimal? cost,
      string model) => store.Usage.Begin(new(Guid.NewGuid(), sessionId, "fixture-provider", model, "chat", started,
          started.AddSeconds(1), cost, false, null, null, null, Guid.NewGuid()));
}
