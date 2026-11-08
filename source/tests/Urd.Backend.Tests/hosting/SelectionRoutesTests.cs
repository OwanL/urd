using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Server;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

#pragma warning disable MEAI001

namespace Urd.Backend.Tests.Hosting;

// Real HTTP Send freezes a complete selection; steering never replaces its initiating binding.
public sealed class SelectionRoutesTests {
  private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) {
    PropertyNameCaseInsensitive = false,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
  };

  [Fact]
  public async Task FreshMissingAndInvalidSelectionsReturn400WithoutRecordingReceipts() {
    using var database = new TemporaryDatabase();
    var llm = new ScriptedChatClient(_ => throw new InvalidOperationException("Invalid selection must not execute."));
    await using var server = new RunningServer(Options(database), createClient: () => llm);
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    foreach ((string path, string? value) in new (string, string?)[] {
        ("llmSelection", null), ("llmSelection", "null"), ("llmSelection", "{}"),
        ("llmSelection.connection", null), ("llmSelection.connection", "null"),
        ("llmSelection.connection.id", "\"00000000-0000-0000-0000-000000000000\""),
        ("llmSelection.connection.id", "\"not-a-guid\""),
        ("llmSelection.connection.name", "null"),
        ("llmSelection.connection.route", "\"unknown\""),
        ("llmSelection.connection.api", "\"unknown\""),
        ("llmSelection.connection.authentication", "\"unknown\""),
        ("llmSelection.connection.endpoint", "null"),
        ("llmSelection.connection.endpoint", "\"relative/path\""),
        ("llmSelection.connection.endpoint", "\"file:///selection\""),
        ("llmSelection.modelId", null), ("llmSelection.modelId", "null"),
        ("llmSelection.modelId", "\"\""), ("llmSelection.modelId", "\"   \""),
        ("llmSelection.reasoningEffort", "\"minimal\""),
        ("llmSelection.reasoningEffort", "\"xhigh\""),
        ("llmSelection.reasoningEffort", "\"extra_high\""),
        ("llmSelection.reasoningEffort", "\"High\""),
        ("llmSelection.reasoningEffort", "2") }) {
      var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "not admitted", TestLlmSelection.Default);
      JsonObject body = JsonSerializer.SerializeToNode(submission, json)!.AsObject();
      string[] members = path.Split('.');
      JsonObject parent = body;
      foreach (string member in members[..^1])
        parent = parent[member]!.AsObject();
      if (value is null)
        parent.Remove(members[^1]);
      else
        parent[members[^1]] = JsonNode.Parse(value);
      using HttpResponseMessage response = await RequestAsync(server.Client, HttpMethod.Post, SubmissionsPath(sessionId),
          JsonBody(body.ToJsonString()));
      Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{path} = {value ?? "<missing>"}: {response.StatusCode}");
      AssertPrivateResponse(response);
      Assert.Null(await ReceiptAsync(server.Client, sessionId, submission.SubmissionId));
    }
    SessionSnapshot snapshot = await SnapshotAsync(server.Client, sessionId);
    Assert.Null(snapshot.LatestLlmSelection);
    Assert.Null(snapshot.UnsettledExecutionId);
    Assert.Empty(snapshot.Messages);
    Assert.Empty(StoredSubmissions.Read(database));
    Assert.Empty(StoredExecutions.Read(database));
    Assert.Empty(StoredExecutions.ReadRequests(database));
    Assert.Equal(0, llm.Calls);
  }

  [Theory]
  [InlineData(null, null)]
  [InlineData("none", ReasoningEffort.None)]
  [InlineData("low", ReasoningEffort.Low)]
  [InlineData("medium", ReasoningEffort.Medium)]
  [InlineData("high", ReasoningEffort.High)]
  [InlineData("extraHigh", ReasoningEffort.ExtraHigh)]
  public async Task AcceptedSelectionRoundTripsWithCanonicalEffortAndChangedOriginalConflicts(
      string? effort, ReasoningEffort? expectedEffort) {
    using var database = new TemporaryDatabase();
    var llm = new ScriptedChatClient(Complete);
    await using var server = new RunningServer(Options(database), createClient: () => llm);
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    Assert.Null((await SnapshotAsync(server.Client, sessionId)).LatestLlmSelection);
    LlmSelection selection = TestLlmSelection.Default with { ReasoningEffort = effort };
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "frozen choice\0🙂", selection);
    UserMessageReceipt accepted = await SubmitAsync(server.Client, sessionId, submission);
    Assert.Equal(MessageAcceptance.Accepted, accepted.Acceptance);
    Assert.Null(accepted.RefusalReason);
    SessionSnapshot settled = await SettledAsync(server.Client, sessionId, accepted.ExecutionId!.Value);
    Assert.Equal(ExecutionOutcome.Completed, settled.LastSettlement!.Outcome);
    Assert.Equal(selection, settled.LatestLlmSelection);
    Assert.Equal(accepted, await ReceiptAsync(server.Client, sessionId, submission.SubmissionId));
    Assert.Equal(accepted, await SubmitAsync(server.Client, sessionId, submission));
    ChatOptions options = Assert.IsType<ChatOptions>(Assert.Single(llm.Options));
    Assert.Equal(selection.ModelId, options.ModelId);
    Assert.Equal(expectedEffort, options.Reasoning?.Effort);

    foreach (LlmSelection changed in new[] {
        selection with { ModelId = "different-model" },
        selection with { ReasoningEffort = effort == "high" ? "low" : "high" },
        selection with { Connection = selection.Connection with { Endpoint = new Uri("https://changed.invalid/v1/") } },
        selection with { Connection = selection.Connection with { Name = "renamed provider" } } }) {
      using HttpResponseMessage conflict = await RequestAsync(server.Client, HttpMethod.Post, SubmissionsPath(sessionId),
          JsonContent.Create(submission with { LlmSelection = changed }, options: json));
      Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
      AssertPrivateResponse(conflict);
      Assert.Equal(accepted, await ReceiptAsync(server.Client, sessionId, submission.SubmissionId));
      Assert.Equal(selection, (await SnapshotAsync(server.Client, sessionId)).LatestLlmSelection);
    }
    Assert.Single(StoredSubmissions.Read(database));
    Assert.Single(StoredExecutions.Read(database));
    Assert.Single(StoredExecutions.ReadRequests(database));
    Assert.Equal(1, llm.Calls);

    LlmSelection nextSelection = selection with { ModelId = "next-model" };
    UserMessageReceipt next = await SubmitAsync(server.Client, sessionId,
        new(Guid.NewGuid(), settled.HistoryRevision, "new operation", nextSelection));
    Assert.Equal(MessageAcceptance.Accepted, next.Acceptance);
    Assert.NotEqual(accepted.ExecutionId, next.ExecutionId);
    Assert.Equal(nextSelection, (await SettledAsync(server.Client, sessionId, next.ExecutionId!.Value)).LatestLlmSelection);
    Assert.Equal(accepted, await ReceiptAsync(server.Client, sessionId, submission.SubmissionId));
    Assert.Equal(accepted, await SubmitAsync(server.Client, sessionId, submission));
    Assert.Equal(nextSelection, (await SnapshotAsync(server.Client, sessionId)).LatestLlmSelection);
    Assert.Equal(2, llm.Calls);
  }

  [Fact]
  public async Task BusySelectionChangesAreDurablyLockedButRenameSteeringKeepsInitiatingLatest() {
    var entered = Signal();
    var release = Signal();
    int requests = 0;
    var llm = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? Paused(entered, release, token) : Complete(token));
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(Options(database), createClient: () => llm);
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateAsync(server.Client);
    LlmSelection selection = TestLlmSelection.Default with { ReasoningEffort = "high" };
    var initiating = new UserMessageSubmission(Guid.NewGuid(), 0, "start", selection);
    UserMessageReceipt accepted = await SubmitAsync(server.Client, sessionId, initiating);
    var locked = new List<UserMessageReceipt>();
    UserMessageReceipt steering;
    try {
      await entered.Task.WaitAsync(TestTimeout);
      foreach (LlmSelection different in new[] {
          selection with { ModelId = "other-model" },
          selection with { ReasoningEffort = "low" },
          selection with { Connection = selection.Connection with { Endpoint = new Uri("https://other.invalid/v1/") } } }) {
        var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "different selection", different);
        UserMessageReceipt refused = await SubmitAsync(server.Client, sessionId, submission);
        Assert.Equal(MessageAcceptance.Refused, refused.Acceptance);
        Assert.Equal(MessageRefusalReason.SelectionLocked, refused.RefusalReason);
        Assert.Null(refused.ExecutionId);
        Assert.Equal(refused, await ReceiptAsync(server.Client, sessionId, submission.SubmissionId));
        Assert.Equal(refused, await SubmitAsync(server.Client, sessionId, submission));
        locked.Add(refused);
      }
      LlmSelection renamed = selection with { Connection = selection.Connection with { Name = "renamed while running" } };
      steering = await SubmitAsync(server.Client, sessionId, new(Guid.NewGuid(), 0, "compatible steering", renamed));
      Assert.Equal(MessageAcceptance.Accepted, steering.Acceptance);
      Assert.Equal(accepted.ExecutionId, steering.ExecutionId);
      Assert.Equal(steering, await ReceiptAsync(server.Client, sessionId, steering.Submission.SubmissionId));
      SessionSnapshot active = await SnapshotAsync(server.Client, sessionId);
      Assert.Equal(accepted.ExecutionId, active.UnsettledExecutionId);
      Assert.Equal(selection, active.LatestLlmSelection);
      Assert.Equal(new[] { initiating.SubmissionId, steering.Submission.SubmissionId }, active.Messages
          .Where(message => message.Message.Role == MessageRole.User).Select(message => message.Message.MessageId));
      Assert.Equal(1, llm.Calls);
    } finally {
      release.TrySetResult();
    }
    SessionSnapshot settled = await SettledAsync(server.Client, sessionId, accepted.ExecutionId!.Value);
    Assert.Equal(ExecutionOutcome.Completed, settled.LastSettlement!.Outcome);
    Assert.Equal(selection, settled.LatestLlmSelection);
    Assert.Equal(accepted, await ReceiptAsync(server.Client, sessionId, initiating.SubmissionId));
    Assert.Equal(steering, await SubmitAsync(server.Client, sessionId, steering.Submission));
    foreach (UserMessageReceipt refused in locked) {
      Assert.Equal(refused, await ReceiptAsync(server.Client, sessionId, refused.Submission.SubmissionId));
      Assert.Equal(refused, await SubmitAsync(server.Client, sessionId, refused.Submission));
    }
    Assert.Equal(new[] { initiating.Text, steering.Submission.Text }, llm.LastRequest
        .Where(message => message.Role == ChatRole.User).Select(message => message.Text));
    Assert.All(llm.Options, options => {
      Assert.Equal(selection.ModelId, options!.ModelId);
      Assert.Equal(ReasoningEffort.High, options.Reasoning?.Effort);
    });
    Assert.Equal(2, llm.Calls);
    Assert.Equal(5, StoredSubmissions.Read(database).Count);
    Assert.Single(StoredExecutions.Read(database));
    Assert.Equal(2, StoredExecutions.ReadRequests(database).Count);
  }

  [Fact]
  public async Task StopAndReopenRetainInitiatingSelectionAndAcceptedAndLockedReceiptsWithoutReplay() {
    using var database = new TemporaryDatabase();
    LlmSelection selection = TestLlmSelection.Default with { ReasoningEffort = "extraHigh" };
    var submission = new UserMessageSubmission(Guid.NewGuid(), 0, "stop this operation", selection);
    Guid sessionId;
    UserMessageReceipt accepted, steering, locked;
    SessionSnapshot before;
    var entered = Signal();
    var release = Signal();
    var llm = new ScriptedChatClient(token => Paused(entered, release, token));
    await using (var first = new RunningServer(Options(database), createClient: () => llm)) {
      await first.WaitForReadyAsync();
      sessionId = await CreateAsync(first.Client);
      accepted = await SubmitAsync(first.Client, sessionId, submission);
      try {
        await entered.Task.WaitAsync(TestTimeout);
        LlmSelection renamed = selection with { Connection = selection.Connection with { Name = "new display name" } };
        steering = await SubmitAsync(first.Client, sessionId, new(Guid.NewGuid(), 0, "queued steering", renamed));
        Assert.Equal(MessageAcceptance.Accepted, steering.Acceptance);
        Assert.Equal(accepted.ExecutionId, steering.ExecutionId);
        locked = await SubmitAsync(first.Client, sessionId,
            new(Guid.NewGuid(), 0, "locked choice", selection with { ModelId = "another-model" }));
        Assert.Equal(MessageRefusalReason.SelectionLocked, locked.RefusalReason);
        Assert.Equal(MessageAcceptance.Refused, locked.Acceptance);
        Assert.Null(locked.ExecutionId);
        using HttpResponseMessage stopped = await RequestAsync(first.Client, HttpMethod.Post,
            $"/api/sessions/{sessionId}/executions/{accepted.ExecutionId}/stop", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        AssertPrivateResponse(stopped);
        StopReceipt receipt = Assert.IsType<StopReceipt>(await stopped.Content.ReadFromJsonAsync<StopReceipt>(json));
        Assert.Equal(accepted.ExecutionId, receipt.ExecutionId);
        Assert.True(receipt.Status is StopStatus.Requested or StopStatus.Settled);
        before = await SettledAsync(first.Client, sessionId, accepted.ExecutionId!.Value);
        Assert.Equal(ExecutionOutcome.Cancelled, before.LastSettlement!.Outcome);
        Assert.Equal(selection, before.LatestLlmSelection);
        Assert.Equal(1, llm.Calls);
      } finally {
        release.TrySetResult();
      }
    }
    List<StoredExecution> executions = StoredExecutions.Read(database);
    List<StoredRequest> requests = StoredExecutions.ReadRequests(database);
    var noReplay = new ScriptedChatClient(_ => throw new InvalidOperationException("Reopening and retries must not replay work."));
    await using var reopened = new RunningServer(Options(database), createClient: () => noReplay);
    await reopened.WaitForReadyAsync();
    SessionSnapshot after = await SnapshotAsync(reopened.Client, sessionId);
    Assert.Equal(selection, after.LatestLlmSelection);
    Assert.Equal(before.LastSettlement, after.LastSettlement);
    Assert.Equal(before.HistoryRevision, after.HistoryRevision);
    Assert.Equal(before.Messages, after.Messages);
    Assert.Null(after.UnsettledExecutionId);
    Assert.False(after.AdmissionFenced);
    foreach (UserMessageReceipt retained in new[] { accepted, steering, locked }) {
      Assert.Equal(retained, await ReceiptAsync(reopened.Client, sessionId, retained.Submission.SubmissionId));
      Assert.Equal(retained, await SubmitAsync(reopened.Client, sessionId, retained.Submission));
    }
    Assert.Equal(selection, (await SnapshotAsync(reopened.Client, sessionId)).LatestLlmSelection);
    Assert.Equal(executions, StoredExecutions.Read(database));
    Assert.Equal(requests, StoredExecutions.ReadRequests(database));
    Assert.Equal(3, StoredSubmissions.Read(database).Count);
    Assert.Equal(0, noReplay.Calls);
  }

  private static ServerOptions Options(TemporaryDatabase database) => new(Path.GetDirectoryName(database.Path)!, 0);

  private static string SubmissionsPath(Guid sessionId) => $"/api/sessions/{sessionId}/message-submissions";

  private static Task<HttpResponseMessage> RequestAsync(HttpClient client, HttpMethod method, string path,
      HttpContent? content = null) {
    Assert.True(client.BaseAddress!.IsLoopback);
    return HttpTest.RequestAsync(client, method, path, content);
  }

  private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response) {
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    AssertPrivateResponse(response);
    Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
  }

  private static async Task<Guid> CreateAsync(HttpClient client) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, "/api/sessions", JsonBody("{}"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    AssertPrivateResponse(response);
    using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return body.RootElement.GetProperty("sessionId").GetGuid();
  }

  private static async Task<UserMessageReceipt> SubmitAsync(HttpClient client, Guid sessionId, UserMessageSubmission submission) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, SubmissionsPath(sessionId),
        JsonContent.Create(submission, options: json));
    using JsonDocument body = await ReadAsync(response);
    UserMessageReceipt receipt = Assert.IsType<UserMessageReceipt>(body.RootElement.Deserialize<UserMessageReceipt>(json));
    Assert.Equal(sessionId, receipt.SessionId);
    Assert.Equal(submission, receipt.Submission);
    AssertSelectionJson(body.RootElement.GetProperty("submission").GetProperty("llmSelection"), submission.LlmSelection);
    return receipt;
  }

  private static async Task<UserMessageReceipt?> ReceiptAsync(HttpClient client, Guid sessionId, Guid submissionId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"{SubmissionsPath(sessionId)}/{submissionId}");
    using JsonDocument body = await ReadAsync(response);
    UserMessageReceipt? receipt = body.RootElement.Deserialize<UserMessageReceipt>(json);
    if (receipt is not null)
      AssertSelectionJson(body.RootElement.GetProperty("submission").GetProperty("llmSelection"), receipt.Submission.LlmSelection);
    return receipt;
  }

  private static async Task<SessionSnapshot> SnapshotAsync(HttpClient client, Guid sessionId) {
    using HttpResponseMessage response = await RequestAsync(client, HttpMethod.Get, $"/api/sessions/{sessionId}/snapshot");
    using JsonDocument body = await ReadAsync(response);
    SessionSnapshot snapshot = Assert.IsType<SessionSnapshot>(body.RootElement.Deserialize<SessionSnapshot>(json));
    Assert.Equal(sessionId, snapshot.SessionId);
    AssertSelectionJson(body.RootElement.GetProperty("latestLlmSelection"), snapshot.LatestLlmSelection);
    return snapshot;
  }

  private static void AssertSelectionJson(JsonElement value, LlmSelection? expected) {
    if (expected is null) {
      Assert.Equal(JsonValueKind.Null, value.ValueKind);
      return;
    }
    Assert.Equal(new[] { "connection", "modelId", "reasoningEffort" },
        value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    Assert.Equal(new[] { "api", "authentication", "endpoint", "id", "name", "route" },
        value.GetProperty("connection").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    Assert.Equal(expected, value.Deserialize<LlmSelection>(json));
    Assert.Equal(expected.ReasoningEffort, value.GetProperty("reasoningEffort").GetString());
  }

  private static Task<SessionSnapshot> SettledAsync(HttpClient client, Guid sessionId, Guid executionId) =>
      HttpTest.SettledAsync(executionId, () => SnapshotAsync(client, sessionId));
}
