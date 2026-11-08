using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

// Keeps the real HTTP status mapping for unavailable and absent Stop targets.
public sealed class SessionStopRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task OrphanStopIsUnavailableAndAnAbsentExecutionIsNotFound() {
    using var database = new TemporaryDatabase();
    Guid sessionId = Guid.NewGuid();
    Guid executionId = Guid.NewGuid();
    using (UrdStore store = UrdStore.Open(database.Path)) {
      store.Sessions.CreatePrimary(sessionId);
      store.UserMessages.Admit(sessionId, 0,
          new(Guid.NewGuid(), 0, "orphan", TestLlmSelection.Default), true, executionId);
      Assert.NotNull(store.Executions.ClaimNextRequest(sessionId, executionId, 0));
      store.UserMessages.Admit(sessionId, 0,
          new(Guid.NewGuid(), 0, "pending", TestLlmSelection.Default), true, Guid.NewGuid(), executionId);
    }
    List<StoredExecution> executions = StoredExecutions.Read(database);
    List<StoredRequest> requests = StoredExecutions.ReadRequests(database);
    List<StoredDelivery> deliveries = StoredDeliveries.Read(database);
    var llm = new ScriptedChatClient(_ => throw new InvalidOperationException("Orphan work must not replay."));
    await using var server = new RunningServer(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0),
        createClient: () => llm);
    await server.WaitForReadyAsync();

    for (int retry = 0; retry < 2; retry++) {
      using HttpResponseMessage unavailable = await RequestAsync(server.Client, HttpMethod.Post,
          StopPath(sessionId, executionId), JsonBody("{}"));
      Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
      AssertPrivateResponse(unavailable);
      Assert.Equal("application/json", unavailable.Content.Headers.ContentType?.MediaType);
      using JsonDocument body = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
      Assert.Equal(new[] { "executionId", "outcome", "status" },
          body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
      StopReceipt receipt = Assert.IsType<StopReceipt>(body.RootElement.Deserialize<StopReceipt>(json));
      Assert.Equal(new StopReceipt(executionId, StopStatus.Unavailable), receipt);
    }

    using (HttpResponseMessage absent = await RequestAsync(server.Client, HttpMethod.Post,
        StopPath(sessionId, Guid.NewGuid()), JsonBody("{}"))) {
      Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
      AssertPrivateResponse(absent);
    }
    Assert.Equal(executions, StoredExecutions.Read(database));
    Assert.Equal(requests, StoredExecutions.ReadRequests(database));
    Assert.Equal(deliveries, StoredDeliveries.Read(database));
    Assert.Equal(0, llm.Calls);
  }

  private static string StopPath(Guid sessionId, Guid executionId) =>
      $"/api/sessions/{sessionId}/executions/{executionId}/stop";
}
