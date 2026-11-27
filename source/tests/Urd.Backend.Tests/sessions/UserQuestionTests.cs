using System.Collections.Concurrent;
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

public sealed class UserQuestionTests {
  [Fact]
  public async Task PrimaryHasPendingQuestionsUntilEachSettles() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("No model request."));
    PrimarySession primary = await manager.CreatePrimaryAsync();
    var tabChanges = new ConcurrentQueue<SessionChange>();
    using IDisposable subscription = await primary.SubscribeAsync(tabChanges.Enqueue, watched: false);
    Assert.False(Assert.Single(tabChanges).HasPendingUserQuestion);
    using var firstCancellation = new CancellationTokenSource();
    using var secondCancellation = new CancellationTokenSource();
    Task<string> firstAsk = primary.AskUserAsync("First?", null, [], false, firstCancellation.Token);
    Task<string> secondAsk = primary.AskUserAsync("Second?", null, [], false, secondCancellation.Token);
    try {
      PendingUserQuestion[] published = await WaitForQuestionsAsync(primary, 2);
      PendingUserQuestion first = Assert.Single(published, item => item.Question == "First?");
      PendingUserQuestion second = Assert.Single(published, item => item.Question == "Second?");
      Assert.All(published, item => Assert.Equal(primary.Id, item.AskingSessionId));
      Assert.Equal(new[] { false, true }, tabChanges.Select(change => change.HasPendingUserQuestion));
      SessionChange? initial = null;
      using IDisposable reopened = await primary.SubscribeAsync(change => initial = change, watched: false);
      Assert.True(initial?.HasPendingUserQuestion);
      Assert.True(await primary.HasPendingUserQuestionAsync(CancellationToken.None));

      Assert.False(await primary.AnswerUserQuestionAsync(Guid.NewGuid(), "not an answer"));
      Assert.True(await primary.AnswerUserQuestionAsync(first.Id, "first answer"));
      Assert.Equal("first answer", await firstAsk.WaitAsync(TestTimeout));
      Assert.False(await primary.AnswerUserQuestionAsync(first.Id, "too late"));
      Assert.Equal(new[] { false, true }, tabChanges.Select(change => change.HasPendingUserQuestion));
      Assert.True(await primary.HasPendingUserQuestionAsync(CancellationToken.None));

      await secondCancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondAsk.WaitAsync(TestTimeout));
      Assert.False(await primary.HasPendingUserQuestionAsync(CancellationToken.None));
      Assert.False(await primary.AnswerUserQuestionAsync(second.Id, "too late"));
      Assert.Equal(new[] { false, true, false }, tabChanges.Select(change => change.HasPendingUserQuestion));
      Assert.All(tabChanges, change => Assert.Equal("idle", change.Status));
      Assert.Empty(await primary.ReadUserQuestionsAsync());
    } finally {
      await firstCancellation.CancelAsync();
      await secondCancellation.CancelAsync();
      await DrainAsync(firstAsk, secondAsk);
    }
  }

  [Fact]
  public async Task CancelledBeforePublicationDoesNotCreateAPendingQuestion() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("No model request."));
    PrimarySession primary = await manager.CreatePrimaryAsync();
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        primary.AskUserAsync("Question?", null, [], false, cancellation.Token));
    Assert.False(await primary.HasPendingUserQuestionAsync(CancellationToken.None));
    Assert.Empty(await primary.ReadUserQuestionsAsync());
  }

  [Fact]
  public async Task ManagerChecksOnlyTheExactOpenAskingSession() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("No model request."));
    PrimarySession primary = await manager.CreatePrimaryAsync();
    PrimarySession other = await manager.CreatePrimaryAsync();
    Guid closedId = (await manager.CreatePrimaryAsync()).Id;
    Assert.Equal(SessionCloseStatus.Closed, await manager.ClosePrimaryAsync(closedId));
    Guid unopenedId = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid finishedChildId = store.Sessions.CreateSubagent(Guid.NewGuid(), primary.Id, "finished").Id;
    SessionRecord liveChild = store.Sessions.CreateSubagent(Guid.NewGuid(), primary.Id, "live");
    await manager.Registry.RunOpenAsync(() => {
      manager.Registry.TrackChild(new SubagentSession(liveChild, store, _ => throw new InvalidOperationException("No model request.")));
      return true;
    });
    Guid unknownId = Guid.NewGuid();
    using var cancellation = new CancellationTokenSource(TestTimeout);
    HarnessToolContext caller = Caller(store, manager, primary.Id, "ask") with { Background = true };
    Task<string> ask = manager.AskUserAsync(caller, "Choose?", "Context", ["one", "two"], cancellation.Token);
    try {
      PendingUserQuestion question = Assert.Single(await WaitForQuestionsAsync(primary, 1));
      Assert.Equal(primary.Id, question.AskingSessionId);
      Assert.Equal("Choose?", question.Question);
      Assert.Equal("Context", question.Context);
      Assert.Equal(new[] { "one", "two" }, question.Answers);
      Assert.Null(question.SourceTitle);
      Assert.True(question.Background);
      HarnessToolContext childCaller = Caller(store, manager, liveChild.Id, "child-ask");
      var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
          manager.AskUserAsync(childCaller, "Child question?", null, [], cancellation.Token));
      Assert.Contains("Subagents must use ask_agent", rejection.Message);
      Assert.Equal(question, Assert.Single(await primary.ReadUserQuestionsAsync()));
      Assert.True(await manager.HasPendingUserQuestionAsync(primary.Id));
      Assert.False(await manager.HasPendingUserQuestionAsync(liveChild.Id));
      Assert.False(await manager.HasPendingUserQuestionAsync(other.Id));
      Assert.False(await manager.HasPendingUserQuestionAsync(finishedChildId));
      Assert.False(await manager.HasPendingUserQuestionAsync(unopenedId));
      Assert.False(await manager.HasPendingUserQuestionAsync(closedId));
      Assert.False(await manager.HasPendingUserQuestionAsync(question.Id));
      Assert.False(await manager.HasPendingUserQuestionAsync(unknownId));
      Assert.False(await manager.HasPendingUserQuestionAsync(Guid.Empty));

      Assert.True(await primary.AnswerUserQuestionAsync(question.Id, "one"));
      Assert.Equal("one", await ask.WaitAsync(TestTimeout));
      Assert.False(await manager.HasPendingUserQuestionAsync(primary.Id));
    } finally {
      await cancellation.CancelAsync();
      await DrainAsync(ask);
    }
  }

  [Fact]
  public async Task SubagentCannotSeeOrInvokeAskUser() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    ScriptedRequest? childRequest = null;
    using var client = new ScriptedChatClient(Route);
    using var providers = new ProviderManager(store, () => new Uri("http://localhost:54321/api/providers/callback"),
        new ScriptedOpenAIHandler(Catalog, "application/json"));
    ProviderConnection connection = await providers.AddCustomAsync(new("user-question-fixture", LlmApi.OpenAIResponses,
        new Uri("https://fixture.invalid/v1"), AuthenticationMethod.None)).WaitAsync(TestTimeout);
    await providers.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
    new PreferenceManager(store).SetBucketModels(ModelBucket.Small, [new(connection.Id, "user-question-fixture")]);
    await using var manager = new SessionManager(store, _ => client, modelSelector: new ModelSelector(providers));
    PrimarySession primary = await manager.CreatePrimaryAsync();
    using var cancellation = new CancellationTokenSource();
    ISubagentRunner runner = manager;
    Task<SubagentRunResult> run = runner.RunSubagentAsync(Caller(store, manager, primary.Id, "spawn"),
        new("small", false, "Ask the user"), cancellation.Token);
    try {
      SubagentRunResult result = await run.WaitAsync(TestTimeout);
      Assert.Null(result.Error);
      Assert.Equal("finished", result.Report);
      Assert.NotNull(childRequest);
      Assert.DoesNotContain(childRequest.Options!.Tools!, tool => tool.Name == "ask_user");
      Assert.Contains(childRequest.Options.Tools!, tool => tool.Name == "ask_agent");
      FunctionResultContent rejected = Assert.Single(client.Requests.SelectMany(messages => messages)
          .SelectMany(message => message.Contents).OfType<FunctionResultContent>(), item => item.CallId == "ask");
      Assert.Equal("Unknown tool 'ask_user'.", rejected.Result);
      Assert.Empty(await primary.ReadUserQuestionsAsync());
    } finally {
      await cancellation.CancelAsync();
      try {
        await run.WaitAsync(TestTimeout);
      } finally {
        try {
          await manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
        } finally {
          await providers.ShutdownAsync().WaitAsync(TestTimeout);
        }
      }
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      return request.Purpose switch {
        "title" => TextUpdates(["child title"], token),
        "conversation" when childRequest is null => AttemptAskUser(request, token),
        "conversation" => TextUpdates(["finished"], token),
        { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal) =>
            TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token),
        _ => throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.")
      };
    }

    IAsyncEnumerable<ChatResponseUpdate> AttemptAskUser(ScriptedRequest request, CancellationToken token) {
      childRequest = request;
      return ToolBatch(token, new FunctionCallContent("ask", "ask_user", new Dictionary<string, object?> {
        ["question"] = "Which option?", ["choices"] = new[] { "option A", "option B" }
      }));
    }
  }

  private static HarnessToolContext Caller(UrdStore store, SessionManager manager, Guid sessionId, string callId) =>
      new(sessionId, Guid.NewGuid(), callId, new ToolSessionServices(store.Sessions, manager, manager)) {
        RequestId = Guid.NewGuid()
      };

  private static async Task<PendingUserQuestion[]> WaitForQuestionsAsync(PrimarySession primary, int count) {
    using var timeout = new CancellationTokenSource(TestTimeout);
    while (true) {
      PendingUserQuestion[] questions = await primary.ReadUserQuestionsAsync(timeout.Token);
      if (questions.Length == count)
        return questions;
      await Task.Delay(10, timeout.Token);
    }
  }

  private static async Task DrainAsync(params Task[] tasks) {
    try {
      await Task.WhenAll(tasks).WaitAsync(TestTimeout);
    } catch (OperationCanceledException) {
      // Cancelled asks are expected during cleanup.
    }
  }

  private static string Catalog => JsonSerializer.Serialize(new {
    @object = "list",
    data = new[] {
      new { id = "user-question-fixture", @object = "model", created = 1_700_000_000, owned_by = "fixture",
        context_window = 32_000 }
    }
  });
}
