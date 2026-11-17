using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Compaction;
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

namespace Urd.Backend.Tests.Execution;

// End-to-end prepass lifecycle over one real session: the pruning conversations and the title
// conversation start together and stay isolated, and their provider-failure fallback, held-title
// Stop contract and compaction-boundary task inputs hold against durable per-execution evidence.
public sealed class StartingPrepassTests {
  private const string AuxiliaryModel = "aux-model";
  private const string ToolsKeepLookup = "{\"keptIds\":[\"lookup\"]}";
  private const string KeepNothing = "{\"keptIds\":[]}";
  private const string PublishedTitle = "Prepass orbit check";
  private const string CompactedSummary = "Goal: retain the compacted decision.";
  private const string FailingPruningMessage = "scripted skills pruning failure";

  [Fact]
  public async Task CategoriesPruneConcurrentlyWhileProviderFailureKeepsAllAndProtectedToolsStayOutOfModelInput() {
    var skillsStarted = Signal();
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var titlePublished = Signal();
    string? titlePrompt = null;
    ScriptedRequest? skillsPruning = null;
    ScriptedRequest? toolsPruning = null;
    ScriptedRequest? main = null;
    var client = new ScriptedChatClient(Route);
    AgentTool[] tools = [Tool("read"), Tool("bash"), Tool("lookup"), Tool("grep"), Tool("vault")];
    ProviderManager providers = null!;
    await using var fixture = new ExecutionFixture(client, tools, store => providers = CreateProviders(store));
    await ConfigureAuxiliaryModelAsync(fixture, providers);
    new PreferenceManager(fixture.Store).SetProtectedItems(ProtectedResourceCategory.Tools, ["read", "bash", "vault"]);
    using IDisposable subscription = await fixture.Session.SubscribeAsync(_ => {
      if (fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Title is not null)
        titlePublished.TrySetResult();
    }, watched: true);

    Guid execution = await fixture.StartExecutionAsync("orbit compass mission");
    await mainWaiting.Task.WaitAsync(TestTimeout);
    await skillsStarted.Task.WaitAsync(TestTimeout);
    await titlePublished.Task.WaitAsync(TestTimeout);

    // Isolation: three separate conversations, each carrying only its own candidates,
    // with the protected tools withheld from the tools pruning input.
    Assert.Equal(["pruning.skills", "pruning.tools", "title"], fixture.Database.ReadRows(
        "SELECT DISTINCT purpose FROM auxiliary_conversations ORDER BY purpose;", row => row.GetString(0)));
    Assert.Equal("orbit compass mission", titlePrompt);
    Assert.NotNull(titlePrompt);
    Assert.DoesNotContain("Test tool", titlePrompt, StringComparison.Ordinal);
    Assert.NotNull(skillsPruning);
    Assert.Contains("diagnose", skillsPruning.LastUserText, StringComparison.Ordinal);
    Assert.DoesNotContain("lookup", skillsPruning.LastUserText, StringComparison.Ordinal);
    Assert.NotNull(toolsPruning);
    Assert.Contains("orbit compass mission", toolsPruning.LastUserText, StringComparison.Ordinal);
    Assert.Contains("lookup", toolsPruning.LastUserText, StringComparison.Ordinal);
    Assert.Contains("grep", toolsPruning.LastUserText, StringComparison.Ordinal);
    Assert.DoesNotContain("vault", toolsPruning.LastUserText, StringComparison.Ordinal);
    Assert.DoesNotContain("bash", toolsPruning.LastUserText, StringComparison.Ordinal);

    // The main model request carries only pruned and protected tools, never the dropped ones.
    Assert.NotNull(main);
    Assert.Equal(new[] { "read", "bash", "lookup", "vault" },
        main.Options!.Tools!.Select(tool => tool.Name).ToArray());

    mainRelease.TrySetResult();
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);

    // The provider failure keeps all of its candidates; the successful category pruned,
    // and both outcomes were persisted for the execution's boundary.
    Assert.Equal(PublishedTitle, fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Title);
    Assert.Equal(new[] {
      new StartingResourcePruningStatus("tools", "completed", 5, 4, null),
      new StartingResourcePruningStatus("skills", "fallback", 1, 1, FailingPruningMessage),
      new StartingResourcePruningStatus("instructions", "completed", 0, 0, null)
    }, fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Pruning);
    string[] toolCandidates = ReadCandidateIds("tools");
    Assert.Equal(["lookup", "grep"], toolCandidates);
    Assert.DoesNotContain("read", toolCandidates);
    Assert.DoesNotContain("bash", toolCandidates);
    Assert.DoesNotContain("vault", toolCandidates);
    Assert.Equal(["lookup"], ReadResultIds("tools"));
    Assert.Equal("completed", ReadDetailState("tools"));
    string skillIdentity = Assert.Single(ReadCandidateIds("skills"));
    Assert.EndsWith(Path.Combine("diagnose", "SKILL.md"), skillIdentity, StringComparison.Ordinal);
    Assert.Equal([skillIdentity], ReadResultIds("skills"));
    Assert.Equal("fallback", ReadDetailState("skills"));
    Assert.Equal([], ReadCandidateIds("instructions"));
    Assert.Equal([], ReadResultIds("instructions"));
    Assert.Equal("completed", ReadDetailState("instructions"));
    Assert.Equal("completed", ReadDetailState("title"));
    StartingContext context = Assert.IsType<StartingContext>(fixture.Store.StartingContexts.Read(fixture.Session.Id));
    Assert.Equal(new[] { "read", "bash", "lookup", "vault" }, context.Tools.Select(tool => tool.Name));
    Assert.Contains("diagnose", string.Join('\n', context.Instructions.Select(message => message.Text)), StringComparison.Ordinal);

    string[] ReadCandidateIds(string category) => ReadJsonArray(category, "candidates_json");
    string[] ReadResultIds(string category) => ReadJsonArray(category, "results_json");
    string ReadDetailState(string category) => fixture.Database.ReadRows(
        $"SELECT state FROM session_prepass_details WHERE run_id = '{execution:D}' AND category = '{category}';",
        row => row.GetString(0)).Single();
    string[] ReadJsonArray(string category, string column) {
      string json = fixture.Database.ReadRows(
          $"SELECT {column} FROM session_prepass_details WHERE run_id = '{execution:D}' AND category = '{category}';",
          row => row.GetString(0)).Single();
      using JsonDocument document = JsonDocument.Parse(json);
      return document.RootElement.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
          ? value.GetString()! : value.GetProperty("Id").GetString()!).ToArray();
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "conversation":
          main = request;
          return Paused(mainWaiting, mainRelease, token);
        case "title":
          titlePrompt = request.Messages[^1].Text;
          return TextUpdates([PublishedTitle], token);
        case "pruning.skills":
          skillsPruning = request;
          skillsStarted.TrySetResult();
          throw new InvalidOperationException(FailingPruningMessage);
        case "pruning.tools":
          toolsPruning = request;
          return AfterSkills(token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }

    // The tools answer is held back until the skills conversation has started, so both
    // categories are provably pruned concurrently rather than sequentially.
    async IAsyncEnumerable<ChatResponseUpdate> AfterSkills([EnumeratorCancellation] CancellationToken token) {
      await skillsStarted.Task.WaitAsync(TestTimeout, token);
      await foreach (ChatResponseUpdate update in TextUpdates([ToolsKeepLookup], token)) yield return update;
    }
  }

  [Fact]
  public async Task FailedTitleIsRecordedWithoutChangingTheExecutionOrRetrying() {
    const string titleFailure = "Title model unavailable.";
    var client = new ScriptedChatClient((request, token) => request.Purpose switch {
      "conversation" => TextUpdates(["done"], token),
      "pruning.skills" => TextUpdates([KeepNothing], token),
      "title" => throw new InvalidOperationException(titleFailure),
      _ => throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.")
    });
    ProviderManager providers = null!;
    await using var fixture = new ExecutionFixture(client, Array.Empty<AgentTool>(), store => providers = CreateProviders(store));
    await ConfigureAuxiliaryModelAsync(fixture, providers);

    Guid execution = await SubmitAndSettleAsync(fixture, "orbit compass title failure");

    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
    Assert.Null(fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Title);
    Assert.Equal("failed", fixture.Database.ReadRows(
        $"SELECT state FROM session_prepass_details WHERE run_id = '{execution:D}' AND category = 'title';",
        row => row.GetString(0)).Single());
    Assert.NotNull(fixture.Database.ReadRows(
        $"SELECT error FROM session_prepass_details WHERE run_id = '{execution:D}' AND category = 'title';",
        row => row.GetString(0)).Single());
    Assert.Equal(1L, fixture.Database.Scalar("SELECT COUNT(*) FROM session_prepass_title;")); // raw-sql: the one-shot title claim survives failure
    Assert.Equal(1L, fixture.Database.Scalar("SELECT COUNT(*) FROM auxiliary_conversations WHERE purpose = 'title';")); // raw-sql: failed title work is not retried
  }

  [Fact]
  public async Task HeldTitlePublishesFirstMessageOnlyWithoutBlockingMainOutputSteeringOrLaterExecutions() {
    var titleHeld = Signal();
    var titleRelease = Signal();
    var mainWaiting = Signal();
    var mainRelease = Signal();
    var steeringStarted = Signal();
    var mainOutput = Signal();
    var titlePublished = Signal();
    const string launch = "orbit compass launch";
    const string steering = "steering follow-up course";
    const string later = "later message notes";
    string? titlePrompt = null;
    var client = new ScriptedChatClient(Route);
    ProviderManager providers = null!;
    await using var fixture = new ExecutionFixture(client, Array.Empty<AgentTool>(), store => providers = CreateProviders(store));
    await ConfigureAuxiliaryModelAsync(fixture, providers);
    using IDisposable subscription = await fixture.Session.SubscribeAsync(_ => {
      if (fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Title is not null)
        titlePublished.TrySetResult();
      if (StoredLlmResponses.ReadText(fixture.Database) == "visible")
        mainOutput.TrySetResult();

    }, watched: true);

    try {
      Guid execution = await fixture.StartExecutionAsync(launch);
      await Task.WhenAll(titleHeld.Task, mainWaiting.Task, mainOutput.Task).WaitAsync(TestTimeout);

      // Main output is durable while the title conversation is still held.
      Assert.Equal("visible", StoredLlmResponses.ReadText(fixture.Database));
      Assert.Equal(execution, fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).UnsettledExecutionId);

      // Steering joins the owned execution without waiting for it or for the held title.
      long revision = (await fixture.Session.ReadSnapshotAsync()).HistoryRevision;
      UserMessageReceipt steer = await fixture.Session.SubmitUserMessageAsync(
          new UserMessageSubmission(Guid.NewGuid(), revision, steering, TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Accepted, steer.Acceptance);
      Assert.Equal(execution, steer.ExecutionId);

      // Steering starts and the main request completes before the title is released.
      mainRelease.TrySetResult();
      await steeringStarted.Task.WaitAsync(TestTimeout);
      // Request completion has no public metadata notification while the title owns
      // settlement, so observe the durable boundary rather than a spurious UI notice.
      using (var wait = new CancellationTokenSource(TestTimeout)) {
        while (StoredExecutions.ReadRequests(fixture.Database) is not { Count: 2 } requests
            || requests.Any(request => request.Outcome != ExecutionOutcome.Completed))
          await Task.Delay(10, wait.Token);
      }
      Assert.Null(fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Title);

      titleRelease.TrySetResult();
      await titlePublished.Task.WaitAsync(TestTimeout);
      Assert.Equal(PublishedTitle, fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Title);

      Assert.Equal(ExecutionOutcome.Completed,
          (await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
      _ = await SubmitAndSettleAsync(fixture, later);

      // Exactly one title conversation, generated from the first message only, and pruning
      // stays a one-per-execution-start effort across steering and later executions.
      Assert.Equal(launch, titlePrompt);
      Assert.Equal(2L, fixture.Database.Scalar("SELECT COUNT(*) FROM auxiliary_conversations;")); // raw-sql: later requests reuse the saved selection instead of starting new auxiliary scopes.
      Assert.Equal(1L, fixture.Database.Scalar("SELECT COUNT(*) FROM auxiliary_conversations WHERE purpose = 'title';")); // raw-sql: the first-message title scope is never regenerated.
      Assert.Equal(execution, fixture.Database.ReadRows(
          "SELECT run_id FROM session_prepass_title;", row => Guid.Parse(row.GetString(0))).Single());
    } finally {
      mainRelease.TrySetResult();
      titleRelease.TrySetResult();
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "conversation":
          if (request.Messages.Any(message => message.Text?.Contains(later, StringComparison.Ordinal) == true))
            return TextUpdates(["after later"], token);
          if (request.Messages.Any(message => message.Text?.Contains(steering, StringComparison.Ordinal) == true)) {
            steeringStarted.TrySetResult();
            return TextUpdates(["after steering"], token);
          }
          return Paused(mainWaiting, mainRelease, token);
        case "title":
          titlePrompt = request.Messages[^1].Text;
          return HeldTitle(token);
        case "pruning.skills":
          return TextUpdates([KeepNothing], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }

    async IAsyncEnumerable<ChatResponseUpdate> HeldTitle([EnumeratorCancellation] CancellationToken token) {
      titleHeld.TrySetResult();
      await titleRelease.Task.WaitAsync(token);
      await foreach (ChatResponseUpdate update in TextUpdates([PublishedTitle], token)) yield return update;
    }
  }

  [Fact]
  public async Task StopDuringPruningJoinsSelectorBeforeSettlingWithoutStartingMain() {
    var selectorStarted = Signal();
    var selectorCancelled = Signal();
    var cleanupRelease = Signal();
    var selectorRetired = Signal();
    int mainContacts = 0;
    var client = new ScriptedChatClient(Route);
    ProviderManager providers = null!;
    await using var fixture = new ExecutionFixture(client, Array.Empty<AgentTool>(), store => providers = CreateProviders(store));
    await ConfigureAuxiliaryModelAsync(fixture, providers);

    try {
      Guid execution = await fixture.StartExecutionAsync("orbit compass stop during pruning");
      await selectorStarted.Task.WaitAsync(TestTimeout);
      Task<SessionExecutionResult> settlement = fixture.Session.WaitForExecutionFinishAsync(execution);
      await fixture.Session.RequestStopAsync(execution).WaitAsync(TestTimeout);
      await selectorCancelled.Task.WaitAsync(TestTimeout);
      Assert.False(settlement.IsCompleted);

      cleanupRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Cancelled, (await settlement.WaitAsync(TestTimeout)).Outcome);
      Assert.True(selectorRetired.Task.IsCompletedSuccessfully);
      Assert.Equal(0, Volatile.Read(ref mainContacts));
      Assert.Single(StoredExecutions.ReadRequests(fixture.Database));
      Assert.Equal(1L, fixture.Database.Scalar("SELECT attempt FROM execution_requests;")); // raw-sql: 1 is known not-attempted; only main request start changes it to 2.
      Assert.Single((await fixture.Session.ReadNotSentInputsAsync()).Inputs);
      Assert.Equal(0L, fixture.Database.Scalar("SELECT COUNT(*) FROM execution_messages WHERE role = 0;")); // raw-sql: auxiliary work must not place the unattempted main input.
      Assert.Contains(fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Pruning!,
          status => status.Category == "skills" && status.State == "cancelled");
    } finally {
      cleanupRelease.TrySetResult();
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "conversation":
          Interlocked.Increment(ref mainContacts);
          return TextUpdates(["unexpected"], token);
        case "title":
          return TextUpdates([PublishedTitle], token);
        case "pruning.skills":
          return HeldSelector(token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }

    async IAsyncEnumerable<ChatResponseUpdate> HeldSelector([EnumeratorCancellation] CancellationToken token) {
      selectorStarted.TrySetResult();
      try {
        await Task.Delay(Timeout.Infinite, token);
      } catch (OperationCanceledException) when (token.IsCancellationRequested) {
        selectorCancelled.TrySetResult();
        throw;
      } finally {
        await cleanupRelease.Task;
        selectorRetired.TrySetResult();
      }
      yield return Text(KeepNothing);
    }
  }

  [Fact]
  public async Task StopJoinsHeldTitleCleanupAndPreventsItsLatePublication() {
    var titleHeld = Signal();
    var titleRelease = Signal();
    var mainCompleted = Signal();
    var client = new ScriptedChatClient(Route);
    ProviderManager providers = null!;
    await using var fixture = new ExecutionFixture(client, Array.Empty<AgentTool>(), store => providers = CreateProviders(store));
    await ConfigureAuxiliaryModelAsync(fixture, providers);
    using IDisposable subscription = await fixture.Session.SubscribeAsync(_ => {
      if (StoredExecutions.ReadRequests(fixture.Database).FirstOrDefault()?.Outcome == ExecutionOutcome.Completed)
        mainCompleted.TrySetResult();
    }, watched: true);

    try {
      Guid execution = await fixture.StartExecutionAsync("orbit compass stop");
      await titleHeld.Task.WaitAsync(TestTimeout);
      await mainCompleted.Task.WaitAsync(TestTimeout);

      // The completed request settles only after the held title conversation joins Stop.
      Task<SessionExecutionResult> settlement = fixture.Session.WaitForExecutionFinishAsync(execution);
      await fixture.Session.RequestStopAsync(execution).WaitAsync(TestTimeout);
      Assert.False(settlement.IsCompleted);
      Assert.Null(fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Title);

      // A late answer delivered after Stop is neither recorded nor published.
      titleRelease.TrySetResult();
      SessionExecutionResult result = await settlement.WaitAsync(TestTimeout);
      Assert.Equal(ExecutionOutcome.Cancelled, result.Outcome);
      SessionSnapshot snapshot = fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id);
      Assert.Null(snapshot.Title);
      Assert.Equal("cancelled", fixture.Database.ReadRows(
          $"SELECT state FROM session_prepass_details WHERE run_id = '{execution:D}' AND category = 'title';",
          row => row.GetString(0)).Single());
      Assert.Equal([1L], fixture.Database.ReadRows("""
          SELECT r.outcome FROM auxiliary_requests r JOIN auxiliary_conversations c ON c.conversation_id = r.conversation_id
          WHERE c.purpose = 'title' ORDER BY r.request_index;
          """, row => row.GetInt64(0)));
      const string titleOutput = """
          SELECT COUNT(*) FROM auxiliary_output o JOIN auxiliary_conversations c ON c.conversation_id = o.conversation_id
          WHERE c.purpose = 'title';
          """;
      Assert.Equal(1L, fixture.Database.Scalar(titleOutput)); // raw-sql: only the pre-Stop chunk exists; the late answer was rejected.
    } finally {
      titleRelease.TrySetResult();
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) => request.Purpose switch {
      "conversation" => TextUpdates(["done"], token),
      "pruning.skills" => TextUpdates([KeepNothing], token),
      "title" => HeldTitle(),
      _ => throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.")
    };

    async IAsyncEnumerable<ChatResponseUpdate> HeldTitle() {
      yield return Text("pending title work");
      titleHeld.TrySetResult();
      // Deliberately token-blind: Stop cannot interrupt the parked stream, so its late
      // answer still has to be rejected by the closing session.
      await titleRelease.Task;
      yield return Text(PublishedTitle);
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }
  }

  [Fact]
  public async Task ManualCompactionAlonePrunesNothingUntilTheNextMessageThenPrunesSummaryAndNewMessage() {
    const string latestTurn = "latest-turn keep decision";
    const string nextTurn = "next message orbit";
    var pruningTasks = new ConcurrentQueue<string>();
    ScriptedRequest? main = null;
    var client = new ScriptedChatClient(Route, RouteResponse);
    ProviderManager providers = null!;
    await using var fixture = new ExecutionFixture(client, Array.Empty<AgentTool>(), store => providers = CreateProviders(store));
    await ConfigureAuxiliaryModelAsync(fixture, providers);

    await SubmitAndSettleAsync(fixture, "ancient-turn: " + new string('x', 40_000));
    _ = await SubmitAndSettleAsync(fixture, latestTurn);
    long revision = (await fixture.Session.ReadSnapshotAsync()).HistoryRevision;
    CompactionReceipt manual = Assert.IsType<CompactionReceipt>(await fixture.Session.CompactAsync(
        new ManualCompactionRequest(Guid.NewGuid(), revision, TestLlmSelection.Default with { ContextWindow = 8_192 })));
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(manual.ExecutionId).WaitAsync(TestTimeout)).Outcome);

    // Compaction alone performs no pruning: no new conversations and no boundary statuses.
    Assert.Single(pruningTasks);
    Assert.Null(fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Pruning);
    Assert.Equal(1L, fixture.Database.Scalar("SELECT COUNT(*) FROM auxiliary_conversations WHERE purpose = 'title';")); // raw-sql: compaction must not start another title conversation.

    await SubmitAndSettleAsync(fixture, nextTurn);

    // The next message prunes from the saved summary plus its own new message.
    Assert.Equal(2, pruningTasks.Count);
    string prunedTask = pruningTasks.ToArray()[^1];
    Assert.Contains(CompactedSummary, prunedTask, StringComparison.Ordinal);
    Assert.Contains(nextTurn, prunedTask, StringComparison.Ordinal);
    Assert.Contains("[HISTORY SUMMARY:", string.Join('\n', main!.Messages.Select(message => message.Text)), StringComparison.Ordinal);
    Assert.Contains(fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Pruning!,
        status => status.Category == "skills" && status.State == "completed" && status.Total == 1 && status.Retained == 0);
    Assert.Equal(1L, fixture.Database.Scalar("SELECT COUNT(*) FROM auxiliary_conversations WHERE purpose = 'title';")); // raw-sql: post-compaction preparation prunes without repeating the title attempt.

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "conversation":
          main = request;
          return TextUpdates(["done"], token);
        case "title":
          return TextUpdates([PublishedTitle], token);
        case "pruning.skills":
          pruningTasks.Enqueue(request.LastUserText);
          return TextUpdates([KeepNothing], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }

    Task<ChatResponse> RouteResponse(ScriptedRequest request, CancellationToken token) {
      Assert.Equal("compaction", request.Purpose);
      return Task.FromResult(CompactedSummaryResponse());
    }
  }

  [Fact]
  public async Task AutomaticCompactionPrunesFromTheSummaryAloneOmittingRetainedAndNewContext() {
    const string oldestMark = "ancient-turn:";
    const string currentTurn = "compass turn";
    var pruningTasks = new ConcurrentQueue<string>();
    var client = new ScriptedChatClient(Route, RouteResponse);
    ProviderManager providers = null!;
    await using var fixture = new ExecutionFixture(client, Array.Empty<AgentTool>(), store => providers = CreateProviders(store));
    await ConfigureAuxiliaryModelAsync(fixture, providers);

    await SubmitAndSettleAsync(fixture, oldestMark + " " + new string('x', 40_000));
    long revision = (await fixture.Session.ReadSnapshotAsync()).HistoryRevision;
    var context = fixture.Store.Compactions.ReadContext(fixture.Session.Id, revision);
    Assert.Null(context.PreviousCompaction);
    _ = await SubmitAndSettleAsync(fixture, currentTurn, TestLlmSelection.Default with { ContextWindow = 8_192 });

    context = fixture.Store.Compactions.ReadContext(fixture.Session.Id, revision);
    Assert.NotNull(context.PreviousCompaction);

    // The automatic compaction boundary prunes from the summary alone: neither the new
    // message nor the retained history is fed back into the pruning task input.
    Assert.Equal(2, pruningTasks.Count);
    string prunedTask = pruningTasks.ToArray()[^1];
    Assert.Contains(CompactedSummary, prunedTask, StringComparison.Ordinal);
    Assert.DoesNotContain(currentTurn, prunedTask, StringComparison.Ordinal);
    Assert.DoesNotContain(oldestMark, prunedTask, StringComparison.Ordinal);
    Assert.Contains(fixture.Store.Snapshots.ReadPersisted(fixture.Session.Id).Pruning!,
        status => status.Category == "skills" && status.State == "completed" && status.Total == 1 && status.Retained == 0);

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "conversation":
          return TextUpdates(["done"], token);
        case "title":
          return TextUpdates([PublishedTitle], token);
        case "pruning.skills":
          pruningTasks.Enqueue(request.LastUserText);
          return TextUpdates([KeepNothing], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }

    Task<ChatResponse> RouteResponse(ScriptedRequest request, CancellationToken token) {
      Assert.Equal("compaction", request.Purpose);
      return Task.FromResult(CompactedSummaryResponse());
    }
  }

  private static async Task<Guid> SubmitAndSettleAsync(ExecutionFixture fixture, string text, LlmSelection? selection = null) {
    long revision = (await fixture.Session.ReadSnapshotAsync()).HistoryRevision;
    UserMessageReceipt receipt = await fixture.Session.SubmitUserMessageAsync(
        new UserMessageSubmission(Guid.NewGuid(), revision, text, selection ?? TestLlmSelection.Default));
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    Guid execution = Assert.IsType<Guid>(receipt.ExecutionId);
    Assert.Equal(ExecutionOutcome.Completed,
        (await fixture.Session.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
    return execution;
  }

  private static ChatResponse CompactedSummaryResponse() =>
      new(new ChatMessage(ChatRole.Assistant, CompactedSummary)) { FinishReason = ChatFinishReason.Stop };

  private static TestAgentTool Tool(string name) =>
      new TestAgentTool(name, (_, _, _) => Task.FromResult(new ToolOutput("unused", [])));

  private static ProviderManager CreateProviders(UrdStore store) => new(store,
      () => new Uri("http://localhost:54321/api/providers/callback"),
      new ScriptedOpenAIHandler(Catalog(AuxiliaryModel), "application/json"));

  private static async Task ConfigureAuxiliaryModelAsync(ExecutionFixture fixture, ProviderManager providers) {
    ProviderConnection connection = await providers.AddCustomAsync(new("prepass-auxiliary", LlmApi.OpenAIResponses,
        new Uri("https://fixture.invalid/v1"), AuthenticationMethod.None)).WaitAsync(TestTimeout);
    await providers.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
    new PreferenceManager(fixture.Store).SetBucketModel(ModelBucket.Small, new(connection.Id, AuxiliaryModel));
  }

  private static string Catalog(string modelId) => JsonSerializer.Serialize(new {
    @object = "list",
    data = new[] { new { id = modelId, @object = "model", created = 1_700_000_000, owned_by = "fixture", context_window = 32_000 } }
  });
}
