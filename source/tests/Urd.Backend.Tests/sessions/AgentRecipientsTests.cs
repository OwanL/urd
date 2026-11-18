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

// Asks resolve through the same live handles and listing snapshot as list_agents: exact final
// titles, the parent alias, placeholders, finished children, unknown names and enqueue-time
// liveness all end in the pinned recipient, a stated error or a failed waiting ask.
public sealed class AgentRecipientsTests {
  private const string ChildTask = "orbit compass mission";
  private const string ChildTitle = "child title";
  private const string PrimaryTask = "primary task";

  [Fact]
  public async Task ResolvesAnExactFinalTitleAndSettlesWithTheRecipientAnswer() {
    const string answerText = "blue answer";
    var childEntered = Signal();
    var releaseChildTool = Signal();
    int conversations = 0;
    var client = new ScriptedChatClient(Route);
    var echo = new TestAgentTool("echo", async (_, _, cancellation) => {
      childEntered.TrySetResult();
      await releaseChildTool.Task.WaitAsync(cancellation);
      return new ToolOutput("echoed", []);
    });
    await using var fixture = await AgentFixture.OpenAsync(client, [echo]);
    ISubagentRunner runner = fixture.Manager;
    Task<SubagentRunResult> run = runner.RunSubagentAsync(fixture.Caller(fixture.ParentSessionId, "spawn"),
        new("small", SubagentContextMode.None, ChildTask), CancellationToken.None).WaitAsync(TestTimeout);

    Guid childId = ChildId(fixture, "spawn");
    await childEntered.Task.WaitAsync(TestTimeout);
    await fixture.WaitForFinalTitleAsync(childId);
    IAgentQuestions questions = fixture.Manager;
    HarnessToolContext caller = fixture.Caller(fixture.ParentSessionId, "ask-1");
    Task<AgentQuestionResult> ask = questions.AskAgentAsync(caller,
        ChildTitle, "why is the sky blue?", wake: true, CancellationToken.None).WaitAsync(TestTimeout);

    QueuedQuestion question = await fixture.WaitForQueuedAsync(childId).WaitAsync(TestTimeout);
    Assert.Equal("why is the sky blue?", question.Prompt);
    Assert.True(question.Wake);
    Assert.Equal(fixture.ParentSessionId, question.SenderId);
    Assert.Equal(childId, question.RecipientId);
    Assert.Equal(caller.RequestId, question.RequestId);
    Assert.Equal("ask-1", question.CallId);

    releaseChildTool.TrySetResult();
    SubagentRunResult childResult = await run;
    Assert.Null(childResult.Error);
    AgentQuestionResult result = await ask;
    Assert.Null(result.Error);
    Assert.Equal(answerText, result.Answer);
    Assert.Empty(fixture.Manager.Questions.Queued(childId));
    Assert.False(await fixture.Manager.IsSubagentLiveAsync(childId).WaitAsync(TestTimeout));

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          return Interlocked.Increment(ref conversations) switch {
            1 => ToolBatch(token, new FunctionCallContent("run", "echo")),
            2 => ToolBatch(token, new FunctionCallContent("answer", "answer_agent",
                new Dictionary<string, object?> { ["answer"] = answerText })),
            _ => TextUpdates(["final report"], token)
          };
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task DisposalFailsQueuedRecordsAndFinishedChildrenReportFinished() {
    var childEntered = Signal();
    var releaseChildTool = Signal();
    int conversations = 0;
    var client = new ScriptedChatClient(Route);
    var echo = new TestAgentTool("echo", async (_, _, cancellation) => {
      childEntered.TrySetResult();
      await releaseChildTool.Task.WaitAsync(cancellation);
      return new ToolOutput("echoed", []);
    });
    await using var fixture = await AgentFixture.OpenAsync(client, [echo]);
    ISubagentRunner runner = fixture.Manager;
    Task<SubagentRunResult> run = runner.RunSubagentAsync(fixture.Caller(fixture.ParentSessionId, "spawn"),
        new("small", SubagentContextMode.None, ChildTask), CancellationToken.None).WaitAsync(TestTimeout);

    Guid childId = ChildId(fixture, "spawn");
    await childEntered.Task.WaitAsync(TestTimeout);
    await fixture.WaitForFinalTitleAsync(childId);
    IAgentQuestions questions = fixture.Manager;
    Task<AgentQuestionResult> ask = questions.AskAgentAsync(fixture.Caller(fixture.ParentSessionId, "ask-1"),
        ChildTitle, "why?", wake: true, CancellationToken.None).WaitAsync(TestTimeout);
    await fixture.WaitForQueuedAsync(childId).WaitAsync(TestTimeout);

    releaseChildTool.TrySetResult();
    SubagentRunResult childResult = await run;
    Assert.Null(childResult.Error);
    AgentQuestionResult result = await ask.WaitAsync(TestTimeout);
    Assert.Null(result.Answer);
    Assert.Equal(AgentQuestions.RecipientFinished, result.Error);
    Assert.False(await fixture.Manager.IsSubagentLiveAsync(childId).WaitAsync(TestTimeout));

    // The finished child keeps its final title in storage; later asks fail immediately.
    AgentQuestionResult finished = await questions.AskAgentAsync(fixture.Caller(fixture.ParentSessionId, "ask-2"),
        ChildTitle, "why?", wake: true, CancellationToken.None).WaitAsync(TestTimeout);
    Assert.Contains("has finished and cannot answer questions", finished.Error!, StringComparison.Ordinal);

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          return Interlocked.Increment(ref conversations) == 1
              ? ToolBatch(token, new FunctionCallContent("run", "echo"))
              : TextUpdates(["final report"], token);
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task ParentAliasResolvesTheCallerParent() {
    var client = new ScriptedChatClient(Route);
    await using var fixture = await AgentFixture.OpenAsync(client);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync().WaitAsync(TestTimeout);
    long revision = (await parent.ReadSnapshotAsync().WaitAsync(TestTimeout)).HistoryRevision;
    UserMessageReceipt receipt = await parent.SubmitUserMessageAsync(
        new UserMessageSubmission(Guid.NewGuid(), revision, PrimaryTask, TestLlmSelection.Default));
    Guid executionId = Assert.IsType<Guid>(receipt.ExecutionId);
    try {
      Assert.Equal(ExecutionOutcome.Completed,
          (await parent.WaitForExecutionFinishAsync(executionId).WaitAsync(TestTimeout)).Outcome);
      SessionRecord child = fixture.Store.Sessions.CreateSubagent(Guid.NewGuid(), parent.Id, "spawn");

      IAgentQuestions questions = fixture.Manager;
      using var cancellation = new CancellationTokenSource();
      Task<AgentQuestionResult> ask = questions.AskAgentAsync(fixture.Caller(child.Id, "ask-1"),
          "parent", "what now?", wake: false, cancellation.Token).WaitAsync(TestTimeout);
      QueuedQuestion question = await fixture.WaitForQueuedAsync(parent.Id).WaitAsync(TestTimeout);
      Assert.Equal(parent.Id, question.RecipientId);
      Assert.Equal(child.Id, question.SenderId);
      Assert.Equal("what now?", question.Prompt);

      await cancellation.CancelAsync().WaitAsync(TestTimeout);
      await Assert.ThrowsAsync<TaskCanceledException>(() => ask);
      Assert.Empty(fixture.Manager.Questions.Queued(parent.Id));
    } finally {
      await parent.StopAcceptingUserMessagesAsync();
      await parent.StopExecutionAsync().WaitAsync(TestTimeout);
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates(["titled parent"], token);
        case "conversation":
          return TextUpdates(["done"], token);
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task PlaceholdersWaitAndParentAliasesCheckTitleState() {
    var client = new ScriptedChatClient(_ => TextUpdates(["done"], CancellationToken.None));
    await using var fixture = await AgentFixture.OpenAsync(client);
    IAgentQuestions questions = fixture.Manager;

    // A name that matches a live placeholder is not addressable yet.
    PrimarySession untitled = await fixture.Manager.CreatePrimaryAsync().WaitAsync(TestTimeout);
    AgentQuestionResult named = await questions.AskAgentAsync(fixture.Caller(untitled.Id, "ask-1"),
        "new session", "hello?", wake: true, CancellationToken.None).WaitAsync(TestTimeout);
    Assert.Null(named.Answer);
    Assert.Contains("wait a few seconds", named.Error!, StringComparison.Ordinal);

    // The parent alias resolves only after the parent carries a final title.
    SessionRecord child = fixture.Store.Sessions.CreateSubagent(Guid.NewGuid(), untitled.Id, "spawn");
    AgentQuestionResult alias = await questions.AskAgentAsync(fixture.Caller(child.Id, "ask-2"),
        "parent", "hello?", wake: true, CancellationToken.None).WaitAsync(TestTimeout);
    Assert.Contains("wait a few seconds", alias.Error!, StringComparison.Ordinal);

    // A primary caller has no parent to alias.
    AgentQuestionResult noParent = await questions.AskAgentAsync(fixture.Caller(untitled.Id, "ask-3"),
        "parent", "hello?", wake: true, CancellationToken.None).WaitAsync(TestTimeout);
    Assert.Contains("has no parent", noParent.Error!, StringComparison.Ordinal);

    // A name no live or finished agent carries is rejected outright.
    AgentQuestionResult unknown = await questions.AskAgentAsync(fixture.Caller(untitled.Id, "ask-4"),
        "nobody at all", "hello?", wake: true, CancellationToken.None).WaitAsync(TestTimeout);
    Assert.Contains("No live agent has the title", unknown.Error!, StringComparison.Ordinal);
  }

  // One manager over a temporary database with bucket resolution; tools are session tools the
  // routed child receives in its catalog.
  private sealed class AgentFixture : IAsyncDisposable {
    private readonly TemporaryDatabase database = new();
    private readonly UrdStore store;
    private readonly ProviderManager providers;

    internal SessionManager Manager { get; }
    internal Guid ParentSessionId { get; }
    internal UrdStore Store => store;
    internal TemporaryDatabase Database => database;
    internal string DatabaseDirectory { get; }

    private AgentFixture(ScriptedChatClient client, IReadOnlyList<AgentTool>? tools) {
      store = UrdStore.Open(database.Path);
      DatabaseDirectory = Path.GetDirectoryName(database.Path)!;
      providers = new ProviderManager(store, () => new Uri("http://localhost:54321/api/providers/callback"),
          new ScriptedOpenAIHandler(BucketCatalog, "application/json"));
      Manager = tools is null
          ? new SessionManager(store, _ => client, modelSelector: new ModelSelector(providers))
          : new SessionManager(store, _ => client, tools, DatabaseDirectory, new ModelSelector(providers));
      ParentSessionId = store.Sessions.CreatePrimary(Guid.NewGuid(), DatabaseDirectory).Id;
    }

    internal static async Task<AgentFixture> OpenAsync(ScriptedChatClient client,
        IReadOnlyList<AgentTool>? tools = null) {
      var fixture = new AgentFixture(client, tools);
      ProviderConnection connection = await fixture.providers.AddCustomAsync(
          new("recipients-fixture", LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/v1"),
              AuthenticationMethod.None)).WaitAsync(TestTimeout);
      await fixture.providers.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      new PreferenceManager(fixture.store).SetBucketModel(ModelBucket.Small,
          new(connection.Id, "recipients-fixture"));
      return fixture;
    }

    internal HarnessToolContext Caller(Guid sessionId, string callId) =>
        new(sessionId, Guid.NewGuid(), callId, new ToolSessionServices(store.Sessions, Manager, Manager)) {
          RequestId = Guid.NewGuid()
        };

    // Titles settle concurrently with the child's execution; asks wait for the final title first.
    internal async Task WaitForFinalTitleAsync(Guid sessionId) {
      while (store.Sessions.ReadAgentTitle(sessionId).Title is null)
        await Task.Delay(20).WaitAsync(TestTimeout);
    }

    internal async Task<QueuedQuestion> WaitForQueuedAsync(Guid recipientId) {
      while (Manager.Questions.Queued(recipientId).Count != 1)
        await Task.Delay(20).WaitAsync(TestTimeout);
      return Manager.Questions.Queued(recipientId).Single();
    }

    public async ValueTask DisposeAsync() {
      try {
        await Manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      } finally {
        await providers.ShutdownAsync().WaitAsync(TestTimeout);
        providers.Dispose();
        store.Dispose();
        database.Dispose();
      }
    }

    private static string BucketCatalog => JsonSerializer.Serialize(new {
      @object = "list",
      data = new[] {
        new { id = "recipients-fixture", @object = "model", created = 1_700_000_000, owned_by = "fixture",
          context_window = 32_000 }
      }
    });
  }

  private static Guid ChildId(AgentFixture fixture, string owningCallId) {
    Guid[] childIds = [.. fixture.Database.ReadRows(
        "SELECT session_id FROM sessions WHERE kind = 1;", row => Guid.Parse(row.GetString(0)))];
    return Assert.Single(childIds.Select(id => fixture.Store.Sessions.Read(id)!),
        child => child.OwningCallId == owningCallId).Id;
  }
}
