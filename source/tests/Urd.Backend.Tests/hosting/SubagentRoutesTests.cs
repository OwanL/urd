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
      Assert.Equal(new[] { "id", "parentId", "title" }, item.EnumerateObject().Select(property => property.Name)
          .OrderBy(name => name, StringComparer.Ordinal));
      Assert.Equal(expectedId, item.GetProperty("id").GetGuid());
      Assert.Equal(expectedParentId, item.GetProperty("parentId").GetGuid());
      Assert.Equal("Subagent", item.GetProperty("title").GetString());
    }

    static string PathFor(Guid parentId, Guid requestId, string callId) =>
        $"/api/sessions/{parentId:D}/subagents/by-call/{requestId:D}/{Uri.EscapeDataString(callId)}";
  }
}
