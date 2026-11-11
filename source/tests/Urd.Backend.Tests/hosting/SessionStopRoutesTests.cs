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

// Startup recovery settles orphaned work without replay; Stop reads retained outcomes.
public sealed class SessionStopRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task RecoveredOrphanStopIsSettledAndAnAbsentExecutionIsNotFound() {
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
    var llm = new ScriptedChatClient(_ => throw new InvalidOperationException("Orphan work must not replay."));
    await using var server = new RunningServer(new ServerOptions(Path.GetDirectoryName(database.Path)!, 0),
        createClient: () => llm);
    await server.WaitForReadyAsync();
    List<StoredExecution> executions = StoredExecutions.Read(database);
    List<StoredRequest> requests = StoredExecutions.ReadRequests(database);
    List<StoredDelivery> deliveries = StoredDeliveries.Read(database);
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(executions).Outcome);
    Assert.Equal(ExecutionOutcome.Failed, Assert.Single(requests).Outcome);
    Assert.All(deliveries, input => Assert.Equal(ExecutionOutcome.Failed, input.Outcome));

    for (int retry = 0; retry < 2; retry++) {
      using HttpResponseMessage settled = await RequestAsync(server.Client, HttpMethod.Post,
          StopPath(sessionId, executionId), JsonBody("{}"));
      Assert.Equal(HttpStatusCode.OK, settled.StatusCode);
      AssertPrivateResponse(settled);
      Assert.Equal("application/json", settled.Content.Headers.ContentType?.MediaType);
      using JsonDocument body = JsonDocument.Parse(await settled.Content.ReadAsStringAsync());
      Assert.Equal(new[] { "executionId", "outcome", "status" },
          body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
      StopReceipt receipt = Assert.IsType<StopReceipt>(body.RootElement.Deserialize<StopReceipt>(json));
      Assert.Equal(new StopReceipt(executionId, StopStatus.Settled, ExecutionOutcome.Failed), receipt);
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
