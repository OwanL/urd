using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Execution;

public sealed class BackgroundToolExecutionTests {
  [Fact]
  public async Task WaitingSteeringAndCompletionShareOneDriverAndRespectRequestBoundaries() {
    var releaseTool = Signal();
    var waiting = Signal();
    var saved = Signal();
    var providerEntered = Signal();
    var releaseProvider = Signal();
    var tool = new TestAgentTool("work", async (arguments, context, cancellation) => {
      Assert.Equal("background", context.CallId);
      Assert.False(arguments.TryGetProperty("background", out _));
      await releaseTool.Task.WaitAsync(cancellation);
      return new ToolOutput("background result", []);
    });
    int requests = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) switch {
      1 => ToolBatch(token, Background("background")),
      3 => Paused(providerEntered, releaseProvider, token),
      _ => Complete(token)
    });
    await using var fixture = new ExecutionFixture(client, tools: [tool]);
    using IDisposable subscription = await fixture.Session.SubscribeAsync(change => {
      if (change.Status == "waiting")
        waiting.TrySetResult();
      if (StoredTools.Read(fixture.Database).Any(call => call.ResultJson is not null))
        saved.TrySetResult();
    }, watched: true);
    Guid execution = await fixture.StartExecutionAsync("start");
    try {
      await waiting.Task.WaitAsync(TestTimeout);
      Assert.Equal(2, client.Calls);
      SessionSnapshot snapshot = await fixture.Session.ReadSnapshotAsync();
      Assert.Equal(execution, snapshot.UnsettledExecutionId);
      Assert.False(snapshot.AdmissionFenced);
      Assert.False(snapshot.StopRequested);
      UserMessageReceipt steering = await fixture.Session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "steer", TestLlmSelection.Default));
      Assert.Equal(execution, steering.ExecutionId);
      await providerEntered.Task.WaitAsync(TestTimeout);
      Assert.Single(client.LastRequest, message => message.Role == ChatRole.User && message.Text == "steer");
      IEnumerable<ChatMessage> inFlight = client.LastRequestSource;
      string[] frozen = inFlight.Select(message => message.Text).ToArray();
      releaseTool.TrySetResult();
      await saved.Task.WaitAsync(TestTimeout);
      Assert.Equal(3, client.Calls);
      Assert.Equal(frozen, inFlight.Select(message => message.Text));
      Assert.DoesNotContain((await fixture.Session.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0))).Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.ToolCompletion);
      releaseProvider.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await fixture.Session.WaitForSettlementAsync(execution).WaitAsync(TestTimeout)).Outcome);
      Assert.Equal(4, client.Calls);
      Assert.Equal(frozen, inFlight.Select(message => message.Text));
      string placeholder = Assert.IsType<string>(Assert.Single(client.Requests[1].SelectMany(message => message.Contents)
          .OfType<FunctionResultContent>()).Result);
      Assert.StartsWith("Background tool started:", placeholder);
      Assert.Equal(placeholder, Assert.IsType<string>(Assert.Single(client.LastRequest.SelectMany(message => message.Contents)
          .OfType<FunctionResultContent>()).Result));
      ChatMessage completion = Assert.Single(client.LastRequest,
          message => message.Text.Contains("[TOOL-ORIGIN: background tool completion", StringComparison.Ordinal));
      Assert.Equal(ChatRole.User, completion.Role);
      Assert.Contains("background result", completion.Text, StringComparison.Ordinal);
      Assert.Single((await fixture.Session.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0))).Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.ToolCompletion);
      Assert.All(client.Options, options => {
        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(options!.Tools!));
        Assert.Equal("boolean", declaration.JsonSchema.GetProperty("properties").GetProperty("background").GetProperty("type").GetString());
        Assert.False(declaration.JsonSchema.TryGetProperty("required", out JsonElement required)
            && required.EnumerateArray().Any(value => value.GetString() == "background"));
      });
    } finally {
      releaseTool.TrySetResult();
      releaseProvider.TrySetResult();
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task StopOrTerminalProviderFailureWaitsForToolCleanupBeforeSettlement(bool providerFails) {
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    var providerEntered = Signal();
    var providerRelease = Signal();
    var never = Signal();
    var tool = new TestAgentTool("work", async (_, _, cancellation) => {
      try {
        await never.Task.WaitAsync(cancellation);
      } finally {
        cleanupEntered.TrySetResult();
        await cleanupRelease.Task;
      }
      return new ToolOutput("finished", []);
    });
    int requests = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) switch {
      1 => ToolBatch(token, Background("background")),
      2 => Later(token),
      _ => Complete(token)
    });
    await using var fixture = new ExecutionFixture(client, tools: [tool]);
    Guid execution = await fixture.StartExecutionAsync("start");
    Task<SessionExecutionResult> settlement = fixture.Session.WaitForSettlementAsync(execution);
    try {
      await providerEntered.Task.WaitAsync(TestTimeout);
      if (!providerFails)
        Assert.Equal(StopStatus.Requested, (await fixture.Session.RequestStopAsync(execution)).Status);
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.False(settlement.IsCompleted);
      Assert.Equal(execution, (await fixture.Session.ReadSnapshotAsync()).UnsettledExecutionId);
      UserMessageReceipt late = await fixture.Session.SubmitUserMessageAsync(
          new(Guid.NewGuid(), 0, "too early", TestLlmSelection.Default));
      if (!providerFails)
        Assert.Equal(MessageAcceptance.Refused, late.Acceptance);
      if (late.Acceptance == MessageAcceptance.Accepted)
        Assert.Equal(execution, late.ExecutionId);
      Assert.Single(StoredExecutions.Read(fixture.Database));
      Assert.Equal(2, client.Calls);
    } finally {
      cleanupRelease.TrySetResult();
      providerRelease.TrySetResult();
      await settlement.WaitAsync(TestTimeout);
    }
    Assert.Equal(providerFails ? ExecutionOutcome.Failed : ExecutionOutcome.Cancelled, (await settlement).Outcome);
    Assert.Equal(ExecutionOutcome.Cancelled, Assert.Single(StoredTools.Read(fixture.Database)).Outcome);
    Assert.NotNull(Assert.Single(StoredTools.Read(fixture.Database)).ResultJson);
    Assert.Equal(2, client.Calls);
    Guid following = await fixture.StartExecutionAsync("deliberate follow-up");
    Assert.NotEqual(execution, following);
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForSettlementAsync(following).WaitAsync(TestTimeout)).Outcome);
    Assert.Equal(3, client.Calls);
    Assert.DoesNotContain(client.LastRequest, message => message.Text == "too early");

    async IAsyncEnumerable<ChatResponseUpdate> Later([EnumeratorCancellation] CancellationToken token) {
      yield return Text("later request");
      providerEntered.TrySetResult();
      if (providerFails)
        throw new IOException("terminal provider failure");
      await providerRelease.Task.WaitAsync(token);
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }
  }

  [Fact]
  public async Task BackgroundSaveFaultCancelsBlockedProviderAndDrainsSiblingBeforeFencing() {
    var database = new TemporaryDatabase();
    UrdStore store = UrdStore.Open(database.Path);
    var failRelease = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    var providerEntered = Signal();
    var providerCancelled = Signal();
    var providerRelease = Signal();
    var never = Signal();
    var tool = new TestAgentTool("work", async (_, context, cancellation) => {
      if (context.CallId == "bad") {
        await failRelease.Task;
        return new ToolOutput("unsaved", []);
      }
      try {
        await never.Task.WaitAsync(cancellation);
      } finally {
        cleanupEntered.TrySetResult();
        await cleanupRelease.Task;
      }
      return new ToolOutput("sibling", []);
    });
    database.Execute("""
      CREATE TRIGGER reject_background_result BEFORE UPDATE OF result_json ON execution_tools
      WHEN NEW.call_id = 'bad'
      BEGIN SELECT RAISE(ABORT, 'Injected background save failure'); END;
      """);
    int requests = 0;
    var client = new ScriptedChatClient(token => Interlocked.Increment(ref requests) == 1
        ? ToolBatch(token, Background("bad"), Background("held")) : Blocked(token));
    var session = new PrimarySession(store.Sessions.CreatePrimary(Guid.NewGuid(), Path.GetDirectoryName(database.Path)),
        store, _ => client, new ToolCatalog([tool]));
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "go", TestLlmSelection.Default));
    Guid execution = receipt.ExecutionId!.Value;
    Task<SessionExecutionResult> settlement = session.WaitForSettlementAsync(execution);
    try {
      try {
        await providerEntered.Task.WaitAsync(TestTimeout);
        failRelease.TrySetResult();
        await providerCancelled.Task.WaitAsync(TestTimeout);
        await cleanupEntered.Task.WaitAsync(TestTimeout);
        Assert.False(settlement.IsCompleted);
        Assert.True((await session.ReadSnapshotAsync()).AdmissionFenced);
      } finally {
        failRelease.TrySetResult();
        cleanupRelease.TrySetResult();
        providerRelease.TrySetResult();
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => settlement.WaitAsync(TestTimeout));
        Assert.IsNotType<TimeoutException>(failure);
        Assert.Contains("Injected background save failure", failure.ToString(), StringComparison.Ordinal);
      }
      SessionSnapshot snapshot = await session.ReadSnapshotAsync();
      Assert.True(snapshot.AdmissionFenced);
      Assert.Equal(execution, snapshot.UnsettledExecutionId);
      Assert.Null(snapshot.LastSettlement);
      Assert.Null(Assert.Single(StoredTools.Read(database), call => call.CallId == "bad").ResultJson);
      Assert.NotNull(Assert.Single(StoredTools.Read(database), call => call.CallId == "held").ResultJson);
      Assert.Equal(2, client.Calls);
    } finally {
      if (settlement.IsCompleted) {
        store.Dispose();
        client.Dispose();
        database.Dispose();
      }
    }

    async IAsyncEnumerable<ChatResponseUpdate> Blocked([EnumeratorCancellation] CancellationToken token) {
      yield return Text("blocked provider");
      providerEntered.TrySetResult();
      try {
        await providerRelease.Task.WaitAsync(token);
      } finally {
        if (token.IsCancellationRequested)
          providerCancelled.TrySetResult();
      }
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }
  }

  private static FunctionCallContent Background(string callId) =>
      new(callId, "work", new Dictionary<string, object?> { ["background"] = true });
}
