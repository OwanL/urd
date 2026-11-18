using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Preferences;
using Urd.Core.Providers;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

// Runs bounded children through the manager: continuations, failures, background stay-alive,
// cancellation, shutdown and the harness-tool service chain all end in the child's final report
// or a stated error, with the child disposed and unregistered afterwards.
public sealed class SubagentRunTests {
  private const string ChildTitle = "bounded child";
  private const string CancelledMessage = "The subagent task was cancelled.";
  private const string AssignmentTask = "orbit compass mission";
  private const string ChildReport = "bounded child report";

  [Fact]
  public async Task ReportsOnlyTheLastRequestTextAfterToolContinuations() {
    int conversations = 0;
    var client = new ScriptedChatClient(Route);
    var echo = new TestAgentTool("echo", (_, _, _) => Task.FromResult(new ToolOutput("echoed", [])));
    await using var fixture = await SubagentRunFixture.OpenAsync(client, [echo]);
    SubagentRunResult result = await RunTaskAsync(fixture, AssignmentTask).WaitAsync(TestTimeout);

    Assert.Null(result.Error);
    Assert.Equal("final report", result.Report);
    Assert.Equal(2, conversations);
    await AssertOwnedChild(fixture, fixture.ParentSessionId, result.ChildSessionId, "spawn");

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          return Interlocked.Increment(ref conversations) == 1
              ? ToolBatch(token, new FunctionCallContent("run", "echo"))
              : TextUpdates(["final", " report"], token);
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task FailedChildResponseReturnsAnErrorResult() {
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    SubagentRunResult result = await RunTaskAsync(fixture, AssignmentTask).WaitAsync(TestTimeout);

    Assert.Null(result.Report);
    Assert.Contains("child model failure", result.Error!, StringComparison.Ordinal);
    Assert.Equal(ExecutionOutcome.Failed, fixture.Store.Snapshots.ReadPersisted(result.ChildSessionId)
        .LastSettlement!.Outcome);
    await AssertOwnedChild(fixture, fixture.ParentSessionId, result.ChildSessionId, "spawn");

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          throw new InvalidOperationException("child model failure");
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task CompletedChildWithoutOutputReturnsAReportError() {
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    SubagentRunResult result = await RunTaskAsync(fixture, AssignmentTask).WaitAsync(TestTimeout);

    Assert.Null(result.Report);
    Assert.Equal("The subagent task produced no report.", result.Error);
    await AssertOwnedChild(fixture, fixture.ParentSessionId, result.ChildSessionId, "spawn");

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          return TextUpdates([], token);
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task PendingBackgroundCallKeepsTheChildAliveAndDelaysTheReport() {
    int conversations = 0;
    var toolEntered = Signal();
    var toolRelease = Signal();
    var sleeper = new TestAgentTool("sleeper", async (_, _, cancellation) => {
      toolEntered.TrySetResult();
      await toolRelease.Task.WaitAsync(cancellation);
      return new ToolOutput("woke", []);
    });
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client, [sleeper]);

    ISubagentRunner runner = fixture.Manager;
    Task<SubagentRunResult> run = runner.RunSubagentAsync(Caller(fixture),
        new("small", SubagentContextMode.None, AssignmentTask), CancellationToken.None).WaitAsync(TestTimeout);
    try {
      await toolEntered.Task.WaitAsync(TestTimeout);
      Assert.False(run.IsCompleted);
    } finally {
      toolRelease.TrySetResult();
    }

    SubagentRunResult result = await run;
    Assert.Equal("final report", result.Report);
    await AssertOwnedChild(fixture, fixture.ParentSessionId, result.ChildSessionId, "spawn");
    StoredTool background = Assert.Single(StoredTools.Read(fixture.Database),
        call => call.Name == "sleeper");
    Assert.True(background.Started);
    Assert.Equal(ExecutionOutcome.Completed, background.Outcome);

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          return Interlocked.Increment(ref conversations) == 1
              ? ToolBatch(token, new FunctionCallContent("sleep", "sleeper",
                  new Dictionary<string, object?> { ["background"] = true }))
              : TextUpdates(["final report"], token);
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task CancellationStopsTheChildAndJoinsItsWork() {
    var requestWaiting = Signal();
    var release = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    using var cancellation = new CancellationTokenSource();

    ISubagentRunner runner = fixture.Manager;
    Task<SubagentRunResult> run = runner.RunSubagentAsync(Caller(fixture),
        new("small", SubagentContextMode.None, AssignmentTask), cancellation.Token).WaitAsync(TestTimeout);
    try {
      await requestWaiting.Task.WaitAsync(TestTimeout);
      await cancellation.CancelAsync();
      // The child's held request unwinds first; the run settles only after its cleanup joins.
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.False(run.IsCompleted);
    } finally {
      cleanupRelease.TrySetResult();
    }

    SubagentRunResult result = await run;
    Assert.Null(result.Report);
    Assert.Equal(CancelledMessage, result.Error);
    SessionSnapshot snapshot = fixture.Store.Snapshots.ReadPersisted(result.ChildSessionId);
    Assert.Equal(ExecutionOutcome.Cancelled, snapshot.LastSettlement!.Outcome);
    Assert.False(await fixture.Manager.IsSubagentLiveAsync(result.ChildSessionId).WaitAsync(TestTimeout));

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          return Paused(requestWaiting, release, token, cleanupEntered, cleanupRelease);
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task ManagerShutdownStopsALiveChild() {
    var requestWaiting = Signal();
    var release = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);

    ISubagentRunner runner = fixture.Manager;
    Task<SubagentRunResult> run = runner.RunSubagentAsync(Caller(fixture),
        new("small", SubagentContextMode.None, AssignmentTask), CancellationToken.None).WaitAsync(TestTimeout);
    try {
      await requestWaiting.Task.WaitAsync(TestTimeout);
      Task managerShutdown = fixture.Manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      // The child's held request unwinds first; the manager finishes after its cleanup joins.
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.False(managerShutdown.IsCompleted);
      cleanupRelease.TrySetResult();
      await managerShutdown;
    } finally {
      cleanupRelease.TrySetResult();
    }

    SubagentRunResult result = await run;
    Assert.Null(result.Report);
    Assert.Equal(CancelledMessage, result.Error);
    SessionSnapshot snapshot = fixture.Store.Snapshots.ReadPersisted(result.ChildSessionId);
    Assert.Equal(ExecutionOutcome.Cancelled, snapshot.LastSettlement!.Outcome);
    Assert.False(await fixture.Manager.IsSubagentLiveAsync(result.ChildSessionId).WaitAsync(TestTimeout));

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          return Paused(requestWaiting, release, token, cleanupEntered, cleanupRelease);
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task PrimaryLookupRejectsChildRecords() {
    var client = new ScriptedChatClient(_ => throw new InvalidOperationException("unused"));
    var database = new TemporaryDatabase();
    var store = UrdStore.Open(database.Path);
    SessionRecord parent = store.Sessions.CreatePrimary(Guid.NewGuid());
    SessionRecord child = store.Sessions.CreateSubagent(Guid.NewGuid(), parent.Id, "spawn");
    var manager = new SessionManager(store, _ => client);
    try {
      Exception failure = await Assert.ThrowsAsync<InvalidOperationException>(
          () => manager.GetPrimaryAsync(child.Id)).WaitAsync(TestTimeout);
      Assert.Contains("subagent session", failure.Message, StringComparison.Ordinal);
    } finally {
      await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      store.Dispose();
      client.Dispose();
      database.Dispose();
    }
  }

  [Fact]
  public async Task HarnessToolServicesRunAChildWithCapturedParentContext() {
    const string parentTask = "parent orbit request";
    const string parentContinuation = "parent wrapped the child";
    string? lastTitlePrompt = null;
    var client = new ScriptedChatClient(Route);
    var tool = new SubagentTool();
    await using var fixture = await SubagentRunFixture.OpenAsync(client, [tool]);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    long revision = (await parent.ReadSnapshotAsync().WaitAsync(TestTimeout)).HistoryRevision;
    UserMessageReceipt receipt = await parent.SubmitUserMessageAsync(
        new UserMessageSubmission(Guid.NewGuid(), revision, parentTask, TestLlmSelection.Default));
    Guid parentExecution = Assert.IsType<Guid>(receipt.ExecutionId);
    try {
      Assert.Equal(ExecutionOutcome.Completed,
          (await parent.WaitForExecutionFinishAsync(parentExecution).WaitAsync(TestTimeout)).Outcome);

      SubagentRunResult result = Assert.IsType<SubagentRunResult>(tool.Result);
      Assert.Null(result.Error);
      Assert.Equal(ChildReport, result.Report);
      Assert.Equal(parentExecution, tool.Context!.ExecutionId);
      Assert.Equal(Assert.Single(StoredTools.Read(fixture.Database)).RequestId, tool.Context.RequestId);
      await AssertOwnedChild(fixture, tool.Context.SessionId, result.ChildSessionId, "delegate");
      Assert.Equal(AssignmentTask, lastTitlePrompt);
      Assert.Equal(fixture.DatabaseDirectory,
          Assert.IsType<SessionRecord>(fixture.Store.Sessions.Read(result.ChildSessionId)).WorkingDirectory);

      ScriptedRequest[] requests = [.. client.Requests.Zip(client.Options,
          (messages, options) => new ScriptedRequest(messages, options))];
      ScriptedRequest childRequest = Assert.Single(requests, request => request.Messages.Any(
          message => message.Role == ChatRole.User && message.Text.StartsWith("[PARENT CONTEXT:", StringComparison.Ordinal)));
      ChatMessage[] childInputs = [.. childRequest.Messages.Where(message => message.Role == ChatRole.User)];
      Assert.StartsWith("[PARENT CONTEXT:", childInputs[0].Text, StringComparison.Ordinal);
      Assert.Contains(parentTask, childInputs[0].Text, StringComparison.Ordinal);
      Assert.Equal(AssignmentTask, childInputs[1].Text);
      string childInstructions = string.Join('\n', childRequest.Messages
          .Where(message => message.Role == ChatRole.System).Select(message => message.Text));
      Assert.Contains("# Shared child guidance", childInstructions, StringComparison.Ordinal);
      Assert.DoesNotContain("Help the user directly", childInstructions, StringComparison.Ordinal);

      ScriptedRequest continuation = Assert.Single(requests, request =>
          request.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any());
      Assert.Equal(ChildReport, Assert.Single(continuation.Messages
          .SelectMany(message => message.Contents).OfType<FunctionResultContent>()).Result);
      Assert.Equal(parentTask, continuation.Messages.Last(message => message.Role == ChatRole.User).Text);
    } finally {
      await parent.StopAcceptingUserMessagesAsync();
      await parent.StopExecutionAsync().WaitAsync(TestTimeout);
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          lastTitlePrompt = request.LastUserText;
          return TextUpdates([ChildTitle], token);
        case "conversation":
          if (request.Messages.Any(message => message.Role == ChatRole.User
                  && message.Text.StartsWith("[PARENT CONTEXT:", StringComparison.Ordinal)))
            return TextUpdates([ChildReport], token);
          if (request.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any())
            return TextUpdates([parentContinuation], token);
          return ToolBatch(token, new FunctionCallContent("delegate", "subagent",
              new Dictionary<string, object?> {
            ["bucket"] = "small", ["context"] = "recent", ["task"] = AssignmentTask }));
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  private static async Task<SubagentRunResult> RunTaskAsync(SubagentRunFixture fixture, string task) {
    ISubagentRunner runner = fixture.Manager;
    return await runner.RunSubagentAsync(Caller(fixture), new("small", SubagentContextMode.None, task),
        CancellationToken.None);
  }

  private static HarnessToolContext Caller(SubagentRunFixture fixture) =>
      new(fixture.ParentSessionId, Guid.NewGuid(), "spawn",
          new ToolSessionServices(fixture.Store.Sessions, fixture.Manager));

  private static async Task AssertOwnedChild(SubagentRunFixture fixture, Guid parentSessionId, Guid childSessionId,
      string owningCallId) {
    SessionRecord record = Assert.IsType<SessionRecord>(fixture.Store.Sessions.Read(childSessionId));
    Assert.Equal(SessionKind.Subagent, record.Kind);
    Assert.Equal(parentSessionId, record.ParentSessionId);
    Assert.Equal(owningCallId, record.OwningCallId);
    Assert.Equal(fixture.DatabaseDirectory, record.WorkingDirectory);
    Assert.DoesNotContain(childSessionId, fixture.Store.Sessions.ListPrimaryIds(null).SessionIds);
    Assert.False(await fixture.Manager.IsSubagentLiveAsync(childSessionId).WaitAsync(TestTimeout));
    Exception failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
        () => fixture.Manager.GetPrimaryAsync(childSessionId)).WaitAsync(TestTimeout);
    Assert.Contains("subagent session", failure.Message, StringComparison.Ordinal);
  }

  private sealed class SubagentTool : HarnessTool {
    public override string Name => "subagent";
    public override string Description => "Runs a child to its final report.";
    public override ToolRelevance RelevantWhen => ToolRelevance.Always;
    public override JsonElement ParametersSchema { get; } = JsonSerializer.SerializeToElement(new {
      type = "object",
      properties = new {
        bucket = new { type = "string" },
        context = new { type = "string" },
        task = new { type = "string" }
      }
    });

    internal SubagentRunResult? Result { get; private set; }
    internal HarnessToolContext? Context { get; private set; }

    public override async Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context,
        CancellationToken cancellation) {
      Context = context;
      var request = new SubagentRunRequest(
          arguments.GetProperty("bucket").GetString()!,
          arguments.GetProperty("context").GetString()! == "recent"
              ? SubagentContextMode.Recent : SubagentContextMode.None,
          arguments.GetProperty("task").GetString()!);
      Result = await context.Sessions.RunSubagentAsync(context, request, cancellation);
      return new(Result.Report ?? Result.Error!, [], IsError: Result.Error is not null);
    }
  }

  private sealed class SubagentRunFixture : IAsyncDisposable {
    private readonly TemporaryDatabase database = new();
    private readonly UrdStore store;
    private readonly ProviderManager providers;
    private readonly ScriptedChatClient Client;

    internal SessionManager Manager { get; }
    internal Guid ParentSessionId { get; }
    internal UrdStore Store => store;
    internal TemporaryDatabase Database => database;
    internal string DatabaseDirectory { get; }

    private SubagentRunFixture(ScriptedChatClient client, IReadOnlyList<AgentTool>? tools) {
      store = UrdStore.Open(database.Path);
      DatabaseDirectory = Path.GetDirectoryName(database.Path)!;
      providers = new ProviderManager(store, () => new Uri("http://localhost:54321/api/providers/callback"),
          new ScriptedOpenAIHandler(BucketCatalog, "application/json"));
      Client = client;
      Manager = tools is null
          ? new SessionManager(store, _ => client, modelSelector: new ModelSelector(providers))
          : new SessionManager(store, _ => client, tools, DatabaseDirectory, new ModelSelector(providers));
      ParentSessionId = store.Sessions.CreatePrimary(Guid.NewGuid(), DatabaseDirectory).Id;
    }

    internal static async Task<SubagentRunFixture> OpenAsync(ScriptedChatClient client,
        IReadOnlyList<AgentTool>? tools = null) {
      var fixture = new SubagentRunFixture(client, tools);
      ProviderConnection connection = await fixture.providers.AddCustomAsync(
          new("subagent-fixture", LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/v1"),
              AuthenticationMethod.None)).WaitAsync(TestTimeout);
      await fixture.providers.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      new PreferenceManager(fixture.store).SetBucketModel(ModelBucket.Small,
          new(connection.Id, "subagent-fixture"));
      return fixture;
    }

    public async ValueTask DisposeAsync() {
      try {
        await Manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      } finally {
        await providers.ShutdownAsync().WaitAsync(TestTimeout);
        providers.Dispose();
        store.Dispose();
        Client.Dispose();
        database.Dispose();
      }
    }

    private static string BucketCatalog => JsonSerializer.Serialize(new {
      @object = "list",
      data = new[] {
        new { id = "subagent-fixture", @object = "model", created = 1_700_000_000, owned_by = "fixture",
          context_window = 32_000 }
      }
    });
  }
}
