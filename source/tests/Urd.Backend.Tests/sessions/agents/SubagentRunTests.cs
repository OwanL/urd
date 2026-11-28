using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Win32.SafeHandles;
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
  public async Task ReadsSubagentViewAndTranscriptWhileLiveAndAfterCleanup() {
    var childEntered = Signal();
    var childRelease = Signal();
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    Task<SubagentRunResult> run = RunTaskAsync(fixture, AssignmentTask).WaitAsync(TestTimeout);
    try {
      await childEntered.Task.WaitAsync(TestTimeout);
      SessionRecord child = ChildForCall(fixture, "spawn");
      SubagentView liveView = Assert.IsType<SubagentView>(
          await fixture.Manager.ReadSubagentViewAsync(child.Id).WaitAsync(TestTimeout));
      Assert.True(liveView.Live);
      Assert.Equal(fixture.ParentSessionId, liveView.ParentId);
      Assert.Equal(ChildTitle, liveView.Title);
      SessionSnapshot liveStored = fixture.Store.Snapshots.ReadPersisted(child.Id);
      Assert.Equal(liveStored.LatestLlmSelection!.ModelId, liveView.ModelId);
      Assert.Equal(liveStored.LatestLlmSelection.ContextWindow, liveView.ContextWindow);
      Assert.NotNull(liveView.ReasoningEffort);
      Assert.Equal(liveStored.WorkingTime, liveView.WorkingTime);
      Assert.NotNull(liveView.WorkingTime!.StartedAt);
      Assert.False(liveView.AdmissionFenced);

      (SessionTranscript liveTranscript, string? liveWorkingDirectory) =
          await fixture.Manager.GetTranscriptAsync(child.Id).WaitAsync(TestTimeout);
      Assert.Equal(fixture.DatabaseDirectory, liveWorkingDirectory);
      await AssertAssignmentInTranscriptAsync(liveTranscript, liveView.HistoryRevision);
    } finally {
      childRelease.TrySetResult();
    }

    SubagentRunResult result = await run;
    Assert.Null(result.Error);
    Assert.Equal(ChildReport, result.Report);
    SessionRecord retainedChild = ChildForCall(fixture, "spawn");
    Assert.False(await fixture.Manager.IsSubagentLiveAsync(retainedChild.Id).WaitAsync(TestTimeout));

    SubagentView retainedView = Assert.IsType<SubagentView>(
        await fixture.Manager.ReadSubagentViewAsync(retainedChild.Id).WaitAsync(TestTimeout));
    Assert.False(retainedView.Live);
    Assert.Equal(fixture.ParentSessionId, retainedView.ParentId);
    Assert.Equal(ChildTitle, retainedView.Title);
    SessionSnapshot retainedStored = fixture.Store.Snapshots.ReadPersisted(retainedChild.Id);
    Assert.Equal(retainedStored.LatestLlmSelection!.ModelId, retainedView.ModelId);
    Assert.Equal(retainedStored.LatestLlmSelection.ContextWindow, retainedView.ContextWindow);
    Assert.Equal(new SessionContextUsage(15, SessionContextUsageSource.Measured), retainedView.ContextUsage);
    Assert.Equal(retainedStored.ContextUsage, retainedView.ContextUsage);
    Assert.Equal(retainedStored.WorkingTime, retainedView.WorkingTime);
    Assert.Null(retainedView.WorkingTime!.StartedAt);
    Assert.True(retainedView.AdmissionFenced);
    Assert.Null(await fixture.Manager.ReadSubagentViewAsync(fixture.ParentSessionId));
    (SessionTranscript retainedTranscript, string? retainedWorkingDirectory) =
        await fixture.Manager.GetTranscriptAsync(retainedChild.Id).WaitAsync(TestTimeout);
    Assert.Equal(fixture.DatabaseDirectory, retainedWorkingDirectory);
    await AssertAssignmentInTranscriptAsync(retainedTranscript, retainedView.HistoryRevision);

    async Task AssertAssignmentInTranscriptAsync(SessionTranscript transcript, long historyRevision) {
      TranscriptHistoryPage history = await transcript.ReadHistoryAsync(
          new(TranscriptHistoryMode.Latest, historyRevision)).WaitAsync(TestTimeout);
      TranscriptEntryDescriptor userEntry = Assert.Single(history.Entries,
          entry => entry.Entry.Kind == TranscriptEntryKind.User);
      MessageContentPage content = await transcript.ReadContentAsync(userEntry.Text!.From).WaitAsync(TestTimeout);
      Assert.Equal(AssignmentTask, content.Text);
      Assert.False(content.HasMore);
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          return ChildResponse(token);
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }

    async IAsyncEnumerable<ChatResponseUpdate> ChildResponse(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
      childEntered.TrySetResult();
      await childRelease.Task.WaitAsync(token);
      yield return Text(ChildReport);
      yield return new() { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 12, OutputTokenCount = 3 })] };
      yield return new() { FinishReason = ChatFinishReason.Stop };
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
        new("small", false, AssignmentTask), CancellationToken.None).WaitAsync(TestTimeout);
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
        new("small", false, AssignmentTask), cancellation.Token).WaitAsync(TestTimeout);
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
        new("small", false, AssignmentTask), CancellationToken.None).WaitAsync(TestTimeout);
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
  [SupportedOSPlatform("windows")]
  public async Task ManagerShutdownRetriesAFailedChildJobKill() {
    if (!OperatingSystem.IsWindows())
      return;
    var client = new ScriptedChatClient((request, token) => TextUpdates([request.Purpose switch {
      "title" => ChildTitle,
      "conversation" => ChildReport,
      _ => PrepassTolerantChatClient.KeepAll(request.Messages)
    }], token));
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    int killAttempts = 0;
    bool jobHadProcesses = false;
    await using var chat = new ChatProcesses(new GitBash(), KillJobAsync);
    var registry = (SessionRegistry)typeof(SessionManager)
        .GetField("registry", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Manager)!;
    AgentQuestions questions = fixture.Manager.Questions;
    var runs = new SubagentRuns(fixture.Store, new PreferenceManager(fixture.Store), fixture.Selector,
        questions, registry, CreateChild, _ => Task.CompletedTask);
    HarnessToolContext caller = Caller(fixture);

    await Assert.ThrowsAsync<ToolCleanupException>(() => runs.RunSubagentAsync(
        caller, new("small", false, AssignmentTask), CancellationToken.None).WaitAsync(TestTimeout));
    Assert.Equal(1, killAttempts);
    Assert.True(jobHadProcesses);
    SessionRecord child = ChildForCall(fixture, caller.CallId);
    Assert.False(await fixture.Manager.IsSubagentLiveAsync(child.Id).WaitAsync(TestTimeout));
    AgentQuestionResult lateQuestion = await questions.Enqueue(child.Id, caller.SessionId, caller.RequestId,
        "late-question", "Are you still there?", wake: false, CancellationToken.None).WaitAsync(TestTimeout);
    Assert.Equal(AgentQuestions.RecipientFinished, lateQuestion.Error);

    await fixture.Manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    Assert.Equal(2, killAttempts);

    SubagentSession CreateChild(SessionRecord record, int depth) {
      chat.StartBash("exec sleep 60", fixture.DatabaseDirectory, new Dictionary<string, string>(), _ => { });
      return new SubagentSession(record, fixture.Store, (_, _) => client, processes: chat, depth: depth);
    }

    async Task KillJobAsync(SafeFileHandle job) {
      if (Interlocked.Increment(ref killAttempts) == 1) {
        jobHadProcesses = WindowsShell.ActiveProcesses(job) > 0;
        throw new TimeoutException("Injected chat-job kill failure.");
      }
      WindowsShell.Terminate(job);
      await WindowsShell.WaitForEmptyAsync(job);
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
  public async Task ForegroundSubagentReturnsAReportWithCapturedParentContext() {
    const string parentTask = "parent orbit request";
    const string parentContinuation = "parent wrapped the child";
    var childTitlePrompt = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var capturedRequests = new System.Collections.Concurrent.ConcurrentQueue<ScriptedRequest>();
    var client = new ScriptedChatClient(Route);
    var unused = new TestAgentTool("unused", (_, _, _) => Task.FromResult(new ToolOutput("unused", [])));
    await using var fixture = await SubagentRunFixture.OpenAsync(client, [unused]);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    long revision = (await parent.ReadSnapshotAsync().WaitAsync(TestTimeout)).HistoryRevision;
    UserMessageReceipt receipt = await parent.SubmitUserMessageAsync(
        new UserMessageSubmission(Guid.NewGuid(), revision, parentTask, TestLlmSelection.Default));
    Guid parentExecution = Assert.IsType<Guid>(receipt.ExecutionId);
    try {
      Assert.Equal(ExecutionOutcome.Completed,
          (await parent.WaitForExecutionFinishAsync(parentExecution).WaitAsync(TestTimeout)).Outcome);

      StoredTool call = Assert.Single(StoredTools.Read(fixture.Database), call => call.Name == "subagent");
      Assert.Equal(parentExecution, call.ExecutionId);
      Assert.Equal(ExecutionOutcome.Completed, call.Outcome);
      SessionRecord child = ChildForCall(fixture, "delegate");
      await AssertOwnedChild(fixture, parent.Id, child.Id, "delegate");
      Assert.Equal(AssignmentTask, await childTitlePrompt.Task.WaitAsync(TestTimeout));
      Assert.Equal(fixture.DatabaseDirectory, child.WorkingDirectory);

      ScriptedRequest[] requests = capturedRequests.ToArray();
      ScriptedRequest childRequest = Assert.Single(requests, request => request.Purpose == "conversation" && request.Messages.Any(
          message => message.Role == ChatRole.User && message.Text.StartsWith("[PARENT CONTEXT:", StringComparison.Ordinal)));
      ScriptedRequest parentRequest = Assert.Single(requests, request => request.Purpose == "conversation"
          && HasUserText(request, parentTask) && !HasResult(request, "delegate"));
      var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(parentRequest.Options!.Tools!,
          definition => definition.Name == "subagent"));
      JsonElement properties = declaration.JsonSchema.GetProperty("properties");
      Assert.Equal("string", properties.GetProperty("bucket").GetProperty("type").GetString());
      Assert.Equal("Pick the smallest bucket sufficient: cost and latency grow with tier. small: bounded, well-defined work; medium: substantial judgment across interacting concerns; large: difficult reasoning, diagnosis or design.",
          properties.GetProperty("bucket").GetProperty("description").GetString());
      Assert.Equal("boolean", properties.GetProperty("includeUserInput").GetProperty("type").GetString());
      Assert.False(properties.TryGetProperty("context", out _));
      Assert.Contains(declaration.JsonSchema.GetProperty("required").EnumerateArray(),
          item => item.GetString() == "includeUserInput");
      Assert.Equal("string", properties.GetProperty("task").GetProperty("type").GetString());
      Assert.Equal("boolean", properties.GetProperty("background").GetProperty("type").GetString());
      Assert.Contains(childRequest.Options!.Tools!, definition => definition.Name == "subagent");
      ChatMessage[] childInputs = [.. childRequest.Messages.Where(message => message.Role == ChatRole.User)];
      Assert.StartsWith("[PARENT CONTEXT:", childInputs[0].Text, StringComparison.Ordinal);
      Assert.Contains(parentTask, childInputs[0].Text, StringComparison.Ordinal);
      Assert.Equal(AssignmentTask, childInputs[1].Text);
      string childInstructions = string.Join('\n', childRequest.Messages
          .Where(message => message.Role == ChatRole.System).Select(message => message.Text));
      Assert.Contains("# Subagent guidance", childInstructions, StringComparison.Ordinal);
      Assert.DoesNotContain("# Default working guidance", childInstructions, StringComparison.Ordinal);

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
      capturedRequests.Enqueue(request);
      switch (request.Purpose) {
        case "title":
          if (request.LastUserText == AssignmentTask)
            childTitlePrompt.TrySetResult(request.LastUserText);
          return TextUpdates([ChildTitle], token);
        case "conversation":
          if (request.Messages.Any(message => message.Role == ChatRole.User
                  && message.Text.StartsWith("[PARENT CONTEXT:", StringComparison.Ordinal)))
            return TextUpdates([ChildReport], token);
          if (request.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any())
            return TextUpdates([parentContinuation], token);
          return ToolBatch(token, new FunctionCallContent("delegate", "subagent",
              new Dictionary<string, object?> {
            ["bucket"] = "small", ["includeUserInput"] = true, ["task"] = AssignmentTask }));
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task PolicyChangeRejectsAStaleCallAndRestoresDelegationWithoutRebuildingContext(bool changeDepth) {
    var firstRequest = new TaskCompletionSource<ScriptedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = Signal();
    var deniedRequest = new TaskCompletionSource<ScriptedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
    var childRequest = new TaskCompletionSource<ScriptedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    Guid providerId = fixture.Store.Preferences.Read().ModelChoices.Small.Single().ConnectionId;
    Guid execution = await StartParentAsync(parent);
    try {
      Assert.Contains((await firstRequest.Task.WaitAsync(TestTimeout)).Options!.Tools!, tool => tool.Name == "subagent");
      if (changeDepth)
        new PreferenceManager(fixture.Store).SetMaxSubagentDepth(0);
      else
        await fixture.Manager.SetSubagentProvidersAsync(parent.Id, []);
      release.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await parent.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
      Assert.DoesNotContain((await deniedRequest.Task.WaitAsync(TestTimeout)).Options!.Tools!, tool => tool.Name == "subagent");
      Assert.Equal(ExecutionOutcome.Failed, Assert.Single(StoredTools.Read(fixture.Database), call => call.CallId == "stale").Outcome);
      Assert.Equal(0L, fixture.Database.Scalar("SELECT EXISTS(SELECT 1 FROM sessions WHERE kind = 1);")); // raw-sql: rejected delegation must not create a child

      if (changeDepth)
        new PreferenceManager(fixture.Store).SetMaxSubagentDepth(1);
      else
        await fixture.Manager.SetSubagentProvidersAsync(parent.Id, [providerId]);
      Guid secondExecution = await StartParentAsync(parent, "Try again");
      Assert.Equal(ExecutionOutcome.Completed,
          (await parent.WaitForExecutionFinishAsync(secondExecution).WaitAsync(TestTimeout)).Outcome);
      ScriptedRequest child = await childRequest.Task.WaitAsync(TestTimeout);
      Assert.Equal("gpt-5.10-terra", child.Options!.ModelId);
      Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredTools.Read(fixture.Database), call => call.CallId == "assigned").Outcome);
    } finally {
      release.TrySetResult();
      await parent.StopExecutionAsync().WaitAsync(TestTimeout);
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      if (request.Purpose == "title")
        return TextUpdates([ChildTitle], token);
      if (request.Purpose != "conversation")
        return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
      if (request.LastUserText == AssignmentTask) {
        childRequest.TrySetResult(request);
        return TextUpdates([ChildReport], token);
      }
      if (request.LastUserText == "Try again")
        return HasResult(request, "assigned") ? Complete(token) : ToolBatch(token, Delegate("assigned", AssignmentTask, bucket: "medium"));
      if (HasResult(request, "stale")) {
        deniedRequest.TrySetResult(request);
        return Complete(token);
      }
      firstRequest.TrySetResult(request);
      return StaleCall(token);
    }

    async IAsyncEnumerable<ChatResponseUpdate> StaleCall([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
      await release.Task.WaitAsync(token);
      await foreach (ChatResponseUpdate update in ToolBatch(token, Delegate("stale", AssignmentTask)))
        yield return update;
    }
  }

  [Fact]
  public async Task BackgroundSubagentDeliversItsReportAfterTheParentContinues() {
    var childEntered = Signal();
    var childRelease = Signal();
    var parentContinued = Signal();
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    Guid execution = await StartParentAsync(parent);
    Task<SessionExecutionResult> settlement = parent.WaitForExecutionFinishAsync(execution);
    try {
      await childEntered.Task.WaitAsync(TestTimeout);
      await parentContinued.Task.WaitAsync(TestTimeout);
      SessionRecord child = ChildForCall(fixture, "background-child");
      Assert.True(await fixture.Manager.IsSubagentLiveAsync(child.Id).WaitAsync(TestTimeout));
      Assert.False(settlement.IsCompleted);
      FunctionResultContent placeholder = Assert.Single(client.Requests.SelectMany(messages => messages)
          .SelectMany(message => message.Contents).OfType<FunctionResultContent>(), result => result.CallId == "background-child");
      Assert.StartsWith("Background tool started:", Assert.IsType<string>(placeholder.Result));
      childRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed, (await settlement.WaitAsync(TestTimeout)).Outcome);
      await AssertOwnedChild(fixture, parent.Id, child.Id, "background-child");
      Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredTools.Read(fixture.Database),
          call => call.CallId == "background-child").Outcome);
      Assert.Contains(client.Requests.SelectMany(messages => messages), message =>
          message.Text.StartsWith("Results from background subagent call, ID: background-child\n", StringComparison.Ordinal)
          && message.Text.Contains(ChildReport, StringComparison.Ordinal));
    } finally {
      childRelease.TrySetResult();
      await parent.StopExecutionAsync().WaitAsync(TestTimeout);
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          if (HasUserText(request, AssignmentTask))
            return ChildResponse(token);
          if (HasResult(request, "background-child")) {
            parentContinued.TrySetResult();
            return Complete(token);
          }
          if (request.Messages.Any(message => message.Text.StartsWith(
              "Results from background subagent call, ID: background-child\n", StringComparison.Ordinal)))
            return Complete(token);
          return ToolBatch(token, Delegate("background-child", AssignmentTask, background: true));
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }

    async IAsyncEnumerable<ChatResponseUpdate> ChildResponse(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
      childEntered.TrySetResult();
      await childRelease.Task.WaitAsync(token);
      yield return Text(ChildReport);
      yield return new() { FinishReason = ChatFinishReason.Stop };
    }
  }

  [Fact]
  public async Task NestedSubagentReturnsReportsThroughBothOwningCalls() {
    const string nestedTask = "nested compass mission";
    const string nestedReport = "nested report";
    var nestedEntered = Signal();
    var nestedRelease = Signal();
    ScriptedRequest? grandchildRequest = null;
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    new PreferenceManager(fixture.Store).SetMaxSubagentDepth(2);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    Guid execution = await StartParentAsync(parent);
    SessionRecord child;
    SessionRecord nested;
    try {
      await nestedEntered.Task.WaitAsync(TestTimeout);
      child = ChildForCall(fixture, "outer");
      nested = ChildForCall(fixture, "inner");
      SubagentCall live = await ReadOwnedCallAsync(nested, "inner");
      Assert.True(live.Live);
      Assert.Equal("subagent-fixture", live.ModelId);
      Assert.NotNull(live.StartedAt);
      Assert.Null(live.ElapsedMilliseconds);
      Assert.False(live.DurationIncomplete);
      Assert.Collection(await fixture.Manager.ListLiveSubagentsAsync(parent.Id).WaitAsync(TestTimeout),
          item => {
            Assert.Equal(child.Id, item.Id);
            Assert.Equal(parent.Id, item.ParentId);
          },
          item => {
            Assert.Equal(nested.Id, item.Id);
            Assert.Equal(child.Id, item.ParentId);
          });
      Assert.Empty(await fixture.Manager.ListLiveSubagentsAsync(fixture.ParentSessionId).WaitAsync(TestTimeout));
      Assert.NotNull(grandchildRequest);
      Assert.DoesNotContain(grandchildRequest.Options!.Tools!, tool => tool.Name == "subagent");
      Assert.Contains(grandchildRequest.Options.Tools!, tool => tool.Name == "ask_agent");

      var caller = new HarnessToolContext(nested.Id, Guid.NewGuid(), "too-deep",
          new ToolSessionServices(fixture.Store.Sessions, fixture.Manager)) { RequestId = Guid.NewGuid() };
      var runner = (ISubagentRunner)fixture.Manager;
      var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunSubagentAsync(caller,
          new("small", false, "must not run"), CancellationToken.None).WaitAsync(TestTimeout));
      Assert.Contains("nesting limit", error.Message, StringComparison.OrdinalIgnoreCase);
      Assert.Null(await fixture.Manager.FindSubagentByCallAsync(nested.Id, caller.RequestId, caller.CallId)
          .WaitAsync(TestTimeout));
    } finally {
      nestedRelease.TrySetResult();
    }
    Assert.Equal(ExecutionOutcome.Completed,
        (await parent.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
    Assert.Empty(await fixture.Manager.ListLiveSubagentsAsync(parent.Id).WaitAsync(TestTimeout));
    await AssertOwnedChild(fixture, parent.Id, child.Id, "outer");
    await AssertOwnedChild(fixture, child.Id, nested.Id, "inner");
    foreach (SubagentCall retained in new[] { await ReadOwnedCallAsync(child, "outer"),
        await ReadOwnedCallAsync(nested, "inner") }) {
      Assert.False(retained.Live);
      Assert.Equal("subagent-fixture", retained.ModelId);
      Assert.Null(retained.StartedAt);
      Assert.True(retained.ElapsedMilliseconds is >= 0);
      Assert.False(retained.DurationIncomplete);
    }
    Assert.All(StoredTools.Read(fixture.Database).Where(call => call.Name == "subagent"),
        call => Assert.Equal(ExecutionOutcome.Completed, call.Outcome));
    Assert.Contains(client.Requests.SelectMany(messages => messages).SelectMany(message => message.Contents)
        .OfType<FunctionResultContent>(), result => result.CallId == "inner" && Equals(result.Result, nestedReport));
    Assert.Contains(client.Requests.SelectMany(messages => messages).SelectMany(message => message.Contents)
        .OfType<FunctionResultContent>(), result => result.CallId == "outer" && Equals(result.Result, ChildReport));

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          if (HasUserText(request, nestedTask)) {
            grandchildRequest = request;
            return NestedResponse(token);
          }
          if (HasUserText(request, AssignmentTask))
            return HasResult(request, "inner") ? TextUpdates([ChildReport], token)
                : ToolBatch(token, Delegate("inner", nestedTask));
          return HasResult(request, "outer") ? Complete(token)
              : ToolBatch(token, Delegate("outer", AssignmentTask));
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }

    async Task<SubagentCall> ReadOwnedCallAsync(SessionRecord record, string callId) {
      Guid requestId = Assert.Single(StoredTools.Read(fixture.Database), call => call.CallId == callId).RequestId;
      Assert.Equal(requestId, record.OwningRequestId);
      SubagentCall? found = await fixture.Manager.FindSubagentByCallAsync(record.ParentSessionId!.Value,
          requestId, callId).WaitAsync(TestTimeout);
      Assert.NotNull(found);
      Assert.Equal(record.Id, found.Id);
      return found;
    }

    async IAsyncEnumerable<ChatResponseUpdate> NestedResponse(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
      nestedEntered.TrySetResult();
      await nestedRelease.Task.WaitAsync(token);
      await foreach (ChatResponseUpdate update in TextUpdates([nestedReport], token))
        yield return update;
    }
  }

  [Theory]
  [InlineData(null, 4)]
  [InlineData(5, 5)]
  public async Task DelegationReachesTheDefaultOrConfiguredDepth(int? configuredLimit, int expectedDepth) {
    string[] tasks = Enumerable.Range(0, expectedDepth + 1).Select(depth => $"Explore level {depth}").ToArray();
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    if (configuredLimit is int limit)
      new PreferenceManager(fixture.Store).SetMaxSubagentDepth(limit);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    Guid execution = await StartParentAsync(parent, tasks[0]);
    Assert.Equal(ExecutionOutcome.Completed,
        (await parent.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
    var calls = StoredTools.Read(fixture.Database).Where(call => call.Name == "subagent").ToArray();
    Assert.Equal(expectedDepth, calls.Length);
    Assert.All(calls, call => Assert.Equal(ExecutionOutcome.Completed, call.Outcome));

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      if (request.Purpose == "title")
        return TextUpdates([ChildTitle], token);
      if (request.Purpose != "conversation")
        return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
      int depth = Array.FindIndex(tasks, task => HasUserText(request, task));
      Assert.InRange(depth, 0, expectedDepth);
      if (depth == expectedDepth) {
        Assert.DoesNotContain(request.Options!.Tools!, tool => tool.Name == "subagent");
        return TextUpdates([ChildReport], token);
      }
      Assert.Contains(request.Options!.Tools!, tool => tool.Name == "subagent");
      string callId = $"level-{depth + 1}";
      return HasResult(request, callId) ? TextUpdates([ChildReport], token)
          : ToolBatch(token, Delegate(callId, tasks[depth + 1]));
    }
  }

  [Theory]
  [InlineData(true, true)]
  [InlineData(false, true)]
  [InlineData(true, false)]
  [InlineData(false, false)]
  public async Task NestedContextPreservesHumanProvenance(bool childIncludesUserInput, bool nestedIncludesUserInput) {
    const string firstHuman = "parent mission";
    const string secondHuman = "another human request";
    const string parentAssistant = "parent assistant observation";
    const string parentToolResult = "parent tool observation";
    const string nestedTask = "nested provenance mission";
    ScriptedRequest? childRequest = null;
    ScriptedRequest? nestedRequest = null;
    var client = new ScriptedChatClient(Route);
    var lookup = new TestAgentTool("lookup", (_, _, _) => Task.FromResult(new ToolOutput(parentToolResult, [])));
    await using var fixture = await SubagentRunFixture.OpenAsync(client, [lookup]);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    Guid firstExecution = await StartParentAsync(parent);
    Assert.Equal(ExecutionOutcome.Completed,
        (await parent.WaitForExecutionFinishAsync(firstExecution).WaitAsync(TestTimeout)).Outcome);
    long revision = (await parent.ReadSnapshotAsync().WaitAsync(TestTimeout)).HistoryRevision;
    UserMessageReceipt receipt = await parent.SubmitUserMessageAsync(
        new(Guid.NewGuid(), revision, secondHuman, TestLlmSelection.Default));
    Guid execution = Assert.IsType<Guid>(receipt.ExecutionId);
    Assert.Equal(ExecutionOutcome.Completed,
        (await parent.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
    Assert.NotNull(childRequest);
    Assert.NotNull(nestedRequest);
    Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredTools.Read(fixture.Database),
        call => call.CallId == "provenance-outer").Outcome);
    Assert.Equal(ExecutionOutcome.Completed, Assert.Single(StoredTools.Read(fixture.Database),
        call => call.CallId == "provenance-inner").Outcome);

    SubagentContextEntry[] childEntries = ContextEntries(childRequest);
    Assert.Equal(childIncludesUserInput ? new[] { firstHuman, secondHuman } : [], childEntries.Select(entry => entry.Text));
    Assert.All(childEntries, entry => Assert.Equal("human input", entry.Source));
    Assert.Equal(new[] { AssignmentTask }, childRequest.Messages.Where(message => message.Role == ChatRole.User
        && !message.Text.StartsWith("[PARENT CONTEXT:", StringComparison.Ordinal)).Select(message => message.Text));

    SubagentContextEntry[] nestedEntries = ContextEntries(nestedRequest);
    Assert.Equal(childIncludesUserInput && nestedIncludesUserInput ? new[] { firstHuman, secondHuman } : [],
        nestedEntries.Select(entry => entry.Text));
    Assert.All(nestedEntries, entry => Assert.Equal("human input", entry.Source));
    Assert.Equal(new[] { nestedTask }, nestedRequest.Messages.Where(message => message.Role == ChatRole.User
        && !message.Text.StartsWith("[PARENT CONTEXT:", StringComparison.Ordinal)).Select(message => message.Text));

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          if (HasUserText(request, nestedTask)) {
            nestedRequest = request;
            return TextUpdates(["nested report"], token);
          }
          if (HasUserText(request, AssignmentTask)) {
            if (HasResult(request, "provenance-inner"))
              return TextUpdates([ChildReport], token);
            childRequest = request;
            return ToolBatch(token, Delegate("provenance-inner", nestedTask, includeUserInput: nestedIncludesUserInput));
          }
          if (!HasUserText(request, secondHuman) || HasResult(request, "provenance-outer"))
            return Complete(token);
          return HasResult(request, "provenance-lookup")
              ? ToolBatch(token, Delegate("provenance-outer", AssignmentTask, includeUserInput: childIncludesUserInput))
              : Lookup(token);
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }

    async IAsyncEnumerable<ChatResponseUpdate> Lookup(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
      yield return Text(parentAssistant);
      await foreach (ChatResponseUpdate update in ToolBatch(token, new FunctionCallContent("provenance-lookup", "lookup")))
        yield return update;
    }
  }

  [Theory]
  [InlineData("option A", true)]
  [InlineData("a freeform answer", true)]
  [InlineData("option A", false)]
  public async Task AnsweredUserQuestionIsInheritedTogetherAndReinherited(string answer, bool nestedIncludesUserInput) {
    const string question = "Which route should we take?";
    const string context = "The northern route is shorter.";
    const string unrelated = "unrelated tool result";
    const string nestedTask = "nested question mission";
    ScriptedRequest? childRequest = null;
    ScriptedRequest? nestedRequest = null;
    var client = new ScriptedChatClient(Route);
    var lookup = new TestAgentTool("lookup", (_, _, _) => Task.FromResult(new ToolOutput(unrelated, [])));
    await using var fixture = await SubagentRunFixture.OpenAsync(client, [lookup]);
    new PreferenceManager(fixture.Store).SetMaxSubagentDepth(2);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    Guid execution = await StartParentAsync(parent);
    try {
      PendingUserQuestion pending = Assert.Single(await WaitForQuestionsAsync(parent));
      Assert.Equal(question, pending.Question);
      Assert.True(await parent.AnswerUserQuestionAsync(pending.Id, answer));
      Assert.Equal(ExecutionOutcome.Completed,
          (await parent.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
      Assert.NotNull(childRequest);
      Assert.NotNull(nestedRequest);
      SubagentContextEntry[] childEntries = ContextEntries(childRequest);
      string exchange = Assert.Single(childEntries, entry => entry.Tool == "ask_user").Text;
      using JsonDocument details = JsonDocument.Parse(exchange);
      JsonElement agent = details.RootElement.GetProperty("agent");
      Assert.Equal(question, agent.GetProperty("question").GetString());
      Assert.Equal(context, agent.GetProperty("context").GetString());
      Assert.Equal(new[] { "option A", "option B" }, agent.GetProperty("choices").EnumerateArray().Select(choice => choice.GetString()));
      Assert.Equal(answer, details.RootElement.GetProperty("human").GetProperty("answer").GetString());
      string childContext = string.Join('\n', childEntries.Select(entry => entry.Text));
      Assert.DoesNotContain(unrelated, childContext, StringComparison.Ordinal);
      SubagentContextEntry[] nestedEntries = ContextEntries(nestedRequest);
      if (nestedIncludesUserInput) {
        string nestedContext = string.Join('\n', nestedEntries.Select(entry => entry.Text));
        Assert.Equal(exchange, Assert.Single(nestedEntries, entry => entry.Tool == "ask_user").Text);
        Assert.DoesNotContain(unrelated, nestedContext, StringComparison.Ordinal);
      } else {
        Assert.Empty(nestedEntries);
      }
    } finally {
      await parent.StopExecutionAsync().WaitAsync(TestTimeout);
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      if (request.Purpose == "title")
        return TextUpdates([ChildTitle], token);
      if (request.Purpose != "conversation")
        return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
      if (HasUserText(request, nestedTask)) {
        nestedRequest = request;
        return TextUpdates([ChildReport], token);
      }
      if (HasUserText(request, AssignmentTask)) {
        if (HasResult(request, "inner-question"))
          return TextUpdates([ChildReport], token);
        childRequest = request;
        return ToolBatch(token, Delegate("inner-question", nestedTask, includeUserInput: nestedIncludesUserInput));
      }
      if (HasResult(request, "outer-question"))
        return Complete(token);
      if (HasResult(request, "lookup-question"))
        return ToolBatch(token, Delegate("outer-question", AssignmentTask, includeUserInput: true));
      if (HasResult(request, "ask-question"))
        return ToolBatch(token, new FunctionCallContent("lookup-question", "lookup"));
      return ToolBatch(token, new FunctionCallContent("ask-question", "ask_user", new Dictionary<string, object?> {
        ["question"] = question, ["context"] = context, ["choices"] = new[] { "option A", "option B" }
      }));
    }
  }

  [Fact]
  public async Task PendingBackgroundUserQuestionAndItsFutureAnswerAreNotInherited() {
    const string question = "A background question?";
    const string context = "Background question context";
    const string answer = "future answer";
    var allowDelegation = Signal();
    var childEntered = Signal();
    var releaseChild = Signal();
    ScriptedRequest? childRequest = null;
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    Guid execution = await StartParentAsync(parent);
    try {
      PendingUserQuestion pending = Assert.Single(await WaitForQuestionsAsync(parent));
      allowDelegation.TrySetResult();
      await childEntered.Task.WaitAsync(TestTimeout);
      Assert.True(await parent.AnswerUserQuestionAsync(pending.Id, answer));
      releaseChild.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed,
          (await parent.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
      Assert.NotNull(childRequest);
      SubagentContextEntry[] entries = ContextEntries(childRequest);
      Assert.DoesNotContain(entries, entry => entry.Text.Contains(question, StringComparison.Ordinal)
          || entry.Text.Contains(context, StringComparison.Ordinal) || entry.Text.Contains(answer, StringComparison.Ordinal));
    } finally {
      allowDelegation.TrySetResult();
      releaseChild.TrySetResult();
      await parent.StopExecutionAsync().WaitAsync(TestTimeout);
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      if (request.Purpose == "title")
        return TextUpdates([ChildTitle], token);
      if (request.Purpose != "conversation")
        return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
      if (HasUserText(request, AssignmentTask)) {
        childRequest = request;
        return Paused(childEntered, releaseChild, token);
      }
      if (HasResult(request, "pending-delegate"))
        return Complete(token);
      if (HasResult(request, "pending-ask"))
        return DelegateAfterPending(token);
      return ToolBatch(token, new FunctionCallContent("pending-ask", "ask_user", new Dictionary<string, object?> {
        ["question"] = question, ["context"] = context, ["choices"] = Array.Empty<string>(), ["background"] = true
      }));
    }

    async IAsyncEnumerable<ChatResponseUpdate> DelegateAfterPending(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
      await allowDelegation.Task.WaitAsync(token);
      await foreach (ChatResponseUpdate update in ToolBatch(token,
          Delegate("pending-delegate", AssignmentTask, includeUserInput: true)))
        yield return update;
    }
  }

  private static async Task<PendingUserQuestion[]> WaitForQuestionsAsync(PrimarySession parent) {
    using var timeout = new CancellationTokenSource(TestTimeout);
    while (true) {
      PendingUserQuestion[] questions = await parent.ReadUserQuestionsAsync(timeout.Token);
      if (questions.Length > 0)
        return questions;
      await Task.Delay(10, timeout.Token);
    }
  }

  [Fact]
  public async Task ListsOpenPrimariesAndNestedChildrenThroughTheToolUntilDisposal() {
    const string nestedTask = "nested list mission";
    var entered = Signal();
    var release = Signal();
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    await fixture.Manager.CreatePrimaryAsync(); // an idle, untitled live primary
    Guid execution = await StartParentAsync(parent);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      SessionRecord child = ChildForCall(fixture, "outer-list");
      SessionRecord nested = ChildForCall(fixture, "inner-list");
      string parentTitle = fixture.Store.Snapshots.ReadPersisted(parent.Id).Title!;
      string childTitle = fixture.Store.Snapshots.ReadPersisted(child.Id).Title!;
      string nestedTitle = fixture.Store.Snapshots.ReadPersisted(nested.Id).Title!;
      IReadOnlyList<LiveAgent> agents = await ((ISubagentRunner)fixture.Manager).ListLiveAgentsAsync(CancellationToken.None);
      Assert.Equal(4, agents.Count); // the fixture's stored-but-unopened primary is excluded
      Assert.Contains(agents, agent => agent.Title.Text == "new session" && agent.Title.IsPlaceholder && agent.State == "idle");
      Assert.Contains(agents, agent => agent.Title.Text == parentTitle && !agent.Title.IsPlaceholder
          && agent.State == "active" && agent.ParentTitle is null);
      Assert.Contains(agents, agent => agent.Title.Text == childTitle && agent.State == "active"
          && agent.ParentTitle?.Text == parentTitle);
      Assert.Contains(agents, agent => agent.Title.Text == nestedTitle && agent.State == "active"
          && agent.ParentTitle?.Text == childTitle);
      FunctionResultContent listed = Assert.Single(client.Requests.SelectMany(messages => messages)
          .SelectMany(message => message.Contents).OfType<FunctionResultContent>(), result => result.CallId == "list-live");
      string text = listed.Result!.ToString()!;
      Assert.Contains($"title: {nestedTitle} | state: active | parent: {childTitle}", text, StringComparison.Ordinal);
      Assert.Contains("title: new session [not addressable] | state: idle", text, StringComparison.Ordinal);
      Assert.DoesNotContain("canAsk", text, StringComparison.Ordinal);
    } finally {
      release.TrySetResult();
    }
    Assert.Equal(ExecutionOutcome.Completed,
        (await parent.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
    IReadOnlyList<LiveAgent> after = await ((ISubagentRunner)fixture.Manager).ListLiveAgentsAsync(CancellationToken.None);
    Assert.Equal(2, after.Count);
    Assert.All(after, agent => Assert.Null(agent.ParentTitle));

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          if (HasUserText(request, nestedTask))
            return HasResult(request, "list-live") ? Paused(entered, release, token)
                : ToolBatch(token, new FunctionCallContent("list-live", "list_agents"));
          if (HasUserText(request, AssignmentTask))
            return HasResult(request, "inner-list") ? TextUpdates([ChildReport], token)
                : ToolBatch(token, Delegate("inner-list", nestedTask));
          return HasResult(request, "outer-list") ? Complete(token)
              : ToolBatch(token, Delegate("outer-list", AssignmentTask));
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task ListsNumberedPlaceholdersWhileTitleGenerationRuns() {
    var bothGenerating = Signal();
    var titleEntered = Signal();
    var release = Signal();
    int titleCalls = 0;
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    PrimarySession first = await fixture.Manager.CreatePrimaryAsync();
    PrimarySession second = await fixture.Manager.CreatePrimaryAsync();
    await fixture.Manager.CreatePrimaryAsync();
    await fixture.Manager.CreatePrimaryAsync();
    Guid firstExecution = await StartParentAsync(first);
    Guid secondExecution = await StartParentAsync(second);
    try {
      await bothGenerating.Task.WaitAsync(TestTimeout);
      IReadOnlyList<LiveAgent> agents = await ((ISubagentRunner)fixture.Manager).ListLiveAgentsAsync(CancellationToken.None);
      Assert.Equal(new[] { "generating title", "generating title (2)", "new session", "new session (2)" },
          agents.Select(agent => agent.Title.Text));
      Assert.All(agents, agent => Assert.True(agent.Title.IsPlaceholder));
    } finally {
      release.TrySetResult();
    }
    await first.WaitForExecutionFinishAsync(firstExecution).WaitAsync(TestTimeout);
    await second.WaitForExecutionFinishAsync(secondExecution).WaitAsync(TestTimeout);

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          if (Interlocked.Increment(ref titleCalls) == 2)
            bothGenerating.TrySetResult();
          return Paused(titleEntered, release, token);
        case "conversation":
          return Complete(token);
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task ListsWaitingForBackgroundTask() {
    var entered = Signal();
    var release = Signal();
    var waiting = Signal();
    var sleeper = new TestAgentTool("sleeper", async (_, _, cancellation) => {
      entered.TrySetResult();
      await release.Task.WaitAsync(cancellation);
      return new ToolOutput("woke", []);
    });
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client, [sleeper]);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    using IDisposable subscription = await parent.SubscribeAsync(change => {
      if (change.Status == "waiting")
        waiting.TrySetResult();
    }, watched: false);
    Guid execution = await StartParentAsync(parent);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      await waiting.Task.WaitAsync(TestTimeout);
      LiveAgent agent = Assert.Single(await ((ISubagentRunner)fixture.Manager).ListLiveAgentsAsync(CancellationToken.None));
      Assert.Equal("waiting for background task", agent.State);
    } finally {
      release.TrySetResult();
    }
    Assert.Equal(ExecutionOutcome.Completed,
        (await parent.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          return HasResult(request, "sleep") ? Complete(token)
              : ToolBatch(token, new FunctionCallContent("sleep", "sleeper",
                  new Dictionary<string, object?> { ["background"] = true }));
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task ParentStopCascadesThroughNestedSubagentsAndJoinsCleanup() {
    const string nestedTask = "nested compass mission";
    var entered = Signal();
    var never = Signal();
    var cleanupEntered = Signal();
    var cleanupRelease = Signal();
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    Guid execution = await StartParentAsync(parent);
    Task<SessionExecutionResult> settlement = parent.WaitForExecutionFinishAsync(execution);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      SessionRecord child = ChildForCall(fixture, "outer-stop");
      SessionRecord nested = ChildForCall(fixture, "inner-stop");
      Assert.Equal(child.Id, nested.ParentSessionId);
      Assert.True(await fixture.Manager.IsSubagentLiveAsync(child.Id).WaitAsync(TestTimeout));
      Assert.True(await fixture.Manager.IsSubagentLiveAsync(nested.Id).WaitAsync(TestTimeout));
      Assert.Equal(StopStatus.Requested, (await parent.RequestStopAsync(execution)).Status);
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.False(settlement.IsCompleted);
      cleanupRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Cancelled, (await settlement.WaitAsync(TestTimeout)).Outcome);
      await AssertOwnedChild(fixture, parent.Id, child.Id, "outer-stop");
      await AssertOwnedChild(fixture, child.Id, nested.Id, "inner-stop");
      Assert.Equal(ExecutionOutcome.Cancelled, fixture.Store.Snapshots.ReadPersisted(child.Id).LastSettlement!.Outcome);
      Assert.Equal(ExecutionOutcome.Cancelled, fixture.Store.Snapshots.ReadPersisted(nested.Id).LastSettlement!.Outcome);
      Assert.All(StoredTools.Read(fixture.Database).Where(call => call.Name == "subagent"),
          call => Assert.Equal(ExecutionOutcome.Cancelled, call.Outcome));
    } finally {
      cleanupRelease.TrySetResult();
      await parent.StopExecutionAsync().WaitAsync(TestTimeout);
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          if (HasUserText(request, nestedTask))
            return Paused(entered, never, token, cleanupEntered, cleanupRelease);
          return HasUserText(request, AssignmentTask)
              ? ToolBatch(token, Delegate("inner-stop", nestedTask))
              : ToolBatch(token, Delegate("outer-stop", AssignmentTask));
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task UnknownBucketReturnsToolErrorWithoutCreatingAChild() {
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    Guid execution = await StartParentAsync(parent);
    Assert.Equal(ExecutionOutcome.Completed,
        (await parent.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
    StoredTool call = Assert.Single(StoredTools.Read(fixture.Database), call => call.CallId == "unknown-bucket");
    Assert.Equal(ExecutionOutcome.Failed, call.Outcome);
    Assert.Contains("bucket", call.Error!, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("unknown", call.Error!, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(fixture.Database.ReadRows("SELECT session_id FROM sessions WHERE kind = 1;", row => row.GetString(0)));
    Assert.Contains(client.Requests.SelectMany(messages => messages).SelectMany(message => message.Contents)
        .OfType<FunctionResultContent>(), result => result.CallId == "unknown-bucket"
          && result.Result?.ToString()?.Contains("bucket", StringComparison.OrdinalIgnoreCase) == true);

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          return HasResult(request, "unknown-bucket") ? Complete(token)
              : ToolBatch(token, Delegate("unknown-bucket", AssignmentTask, bucket: "unknown"));
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  [Fact]
  public async Task RestartDoesNotReviveStoppedChildren() {
    var entered = Signal();
    var never = Signal();
    var client = new ScriptedChatClient(Route);
    await using var fixture = await SubagentRunFixture.OpenAsync(client);
    PrimarySession parent = await fixture.Manager.CreatePrimaryAsync();
    Guid execution = await StartParentAsync(parent);
    await entered.Task.WaitAsync(TestTimeout);
    SessionRecord child = ChildForCall(fixture, "restart-child");
    Assert.True(await fixture.Manager.IsSubagentLiveAsync(child.Id).WaitAsync(TestTimeout));
    await fixture.Manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    Assert.Equal(ExecutionOutcome.Cancelled,
        (await parent.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
    int callsBeforeRestart = client.Calls;
    using UrdStore reopened = UrdStore.Open(fixture.Database.Path);
    var restarted = new SessionManager(reopened, _ => client);
    try {
      Assert.False(await restarted.IsSubagentLiveAsync(child.Id).WaitAsync(TestTimeout));
      Assert.Equal(parent.Id, (await restarted.GetPrimaryAsync(parent.Id).WaitAsync(TestTimeout)).Id);
      Assert.False(await restarted.IsSubagentLiveAsync(child.Id).WaitAsync(TestTimeout));
      Assert.Equal(callsBeforeRestart, client.Calls);
      Assert.Equal(parent.Id, reopened.Sessions.Read(child.Id)!.ParentSessionId);
      Assert.Equal(ExecutionOutcome.Cancelled, reopened.Snapshots.ReadPersisted(child.Id).LastSettlement!.Outcome);
    } finally {
      await restarted.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      switch (request.Purpose) {
        case "title":
          return TextUpdates([ChildTitle], token);
        case "conversation":
          return HasUserText(request, AssignmentTask) ? Paused(entered, never, token)
              : ToolBatch(token, Delegate("restart-child", AssignmentTask));
        case { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal):
          return TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token);
        default:
          throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.");
      }
    }
  }

  private static async Task<Guid> StartParentAsync(PrimarySession parent, string text = "parent mission") {
    long revision = (await parent.ReadSnapshotAsync().WaitAsync(TestTimeout)).HistoryRevision;
    UserMessageReceipt receipt = await parent.SubmitUserMessageAsync(
        new(Guid.NewGuid(), revision, text, TestLlmSelection.Default));
    return Assert.IsType<Guid>(receipt.ExecutionId);
  }

  private static FunctionCallContent Delegate(string callId, string task, bool background = false, string bucket = "small",
      bool includeUserInput = false) =>
      new(callId, "subagent", new Dictionary<string, object?> {
        ["bucket"] = bucket, ["includeUserInput"] = includeUserInput, ["task"] = task, ["background"] = background
      });

  private static SubagentContextEntry[] ContextEntries(ScriptedRequest request) {
    ChatMessage[] contexts = [.. request.Messages.Where(message => message.Role == ChatRole.User
        && message.Text.StartsWith("[PARENT CONTEXT:", StringComparison.Ordinal))];
    Assert.True(contexts.Length <= 1);
    if (contexts.Length == 0)
      return [];
    string text = contexts[0].Text;
    string json = text[(text.IndexOf('\n', StringComparison.Ordinal) + 1)..];
    return JsonSerializer.Deserialize<SubagentContextEntry[]>(json, JsonSerializerOptions.Web)!;
  }

  private static bool HasUserText(ScriptedRequest request, string text) =>
      request.Messages.Any(message => message.Role == ChatRole.User && message.Text == text);

  private static bool HasResult(ScriptedRequest request, string callId) =>
      request.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>()
          .Any(result => result.CallId == callId);

  private static async Task<SubagentRunResult> RunTaskAsync(SubagentRunFixture fixture, string task) {
    ISubagentRunner runner = fixture.Manager;
    return await runner.RunSubagentAsync(Caller(fixture), new("small", false, task),
        CancellationToken.None);
  }

  private static HarnessToolContext Caller(SubagentRunFixture fixture) =>
      new(fixture.ParentSessionId, Guid.NewGuid(), "spawn",
          new ToolSessionServices(fixture.Store.Sessions, fixture.Manager)) { RequestId = Guid.NewGuid() };

  private static async Task AssertOwnedChild(SubagentRunFixture fixture, Guid parentSessionId, Guid childSessionId,
      string owningCallId) {
    SessionRecord record = Assert.IsType<SessionRecord>(fixture.Store.Sessions.Read(childSessionId));
    Assert.Equal(SessionKind.Subagent, record.Kind);
    Assert.Equal(parentSessionId, record.ParentSessionId);
    Assert.Equal(owningCallId, record.OwningCallId);
    Assert.Equal(fixture.Store.Sessions.Read(parentSessionId)!.WorkingDirectory, record.WorkingDirectory);
    Assert.DoesNotContain(childSessionId, fixture.Store.Sessions.ListPrimaryIds(null).SessionIds);
    Assert.False(await fixture.Manager.IsSubagentLiveAsync(childSessionId).WaitAsync(TestTimeout));
    Exception failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
        () => fixture.Manager.GetPrimaryAsync(childSessionId)).WaitAsync(TestTimeout);
    Assert.Contains("subagent session", failure.Message, StringComparison.Ordinal);
  }

  private static SessionRecord ChildForCall(SubagentRunFixture fixture, string callId) {
    Guid[] childIds = [.. fixture.Database.ReadRows(
        "SELECT session_id FROM sessions WHERE kind = 1;", row => Guid.Parse(row.GetString(0)))];
    return Assert.Single(childIds.Select(id => fixture.Store.Sessions.Read(id)!),
        child => child.OwningCallId == callId);
  }

  private sealed class SubagentRunFixture : IAsyncDisposable {
    private readonly TemporaryDatabase database = new();
    private readonly UrdStore store;
    private readonly ProviderManager providers;
    private readonly ScriptedChatClient Client;

    internal SessionManager Manager { get; }
    internal ModelSelector Selector { get; }
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
      Selector = new ModelSelector(providers);
      Manager = tools is null
          ? new SessionManager(store, _ => client, modelSelector: Selector)
          : new SessionManager(store, _ => client, tools, DatabaseDirectory, Selector);
      ParentSessionId = store.Sessions.CreatePrimary(Guid.NewGuid(), DatabaseDirectory).Id;
    }

    internal static async Task<SubagentRunFixture> OpenAsync(ScriptedChatClient client,
        IReadOnlyList<AgentTool>? tools = null) {
      var fixture = new SubagentRunFixture(client, tools);
      ProviderConnection connection = await fixture.providers.AddCustomAsync(
          new("subagent-fixture", LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/v1"),
              AuthenticationMethod.None)).WaitAsync(TestTimeout);
      await fixture.providers.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      var preferences = new PreferenceManager(fixture.store);
      preferences.SetBucketModels(ModelBucket.Small, [new(connection.Id, "subagent-fixture")]);
      preferences.SetBucketModels(ModelBucket.Medium, [new(connection.Id, "gpt-5.10-terra")]);
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
      data = new[] { "subagent-fixture", "gpt-5.9-terra", "gpt-5.10-terra" }.Select(id =>
          new { id, @object = "model", created = 1_700_000_000, owned_by = "fixture", context_window = 32_000 })
    });
  }
}
