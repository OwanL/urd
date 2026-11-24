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
  public async Task PrimaryChecksAskingSessionRatherThanQuestionIdentityUntilEachQuestionSettles() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    await using var manager = new SessionManager(store, _ => throw new InvalidOperationException("No model request."));
    PrimarySession primary = await manager.CreatePrimaryAsync();
    Guid childId = Guid.NewGuid();
    var first = Question(primary.Id);
    var second = Question(primary.Id);
    var child = Question(childId);
    using var firstCancellation = new CancellationTokenSource();
    using var secondCancellation = new CancellationTokenSource();
    using var childCancellation = new CancellationTokenSource();
    Task<string> firstAsk = primary.AskUserAsync(first, firstCancellation.Token);
    Task<string> secondAsk = primary.AskUserAsync(second, secondCancellation.Token);
    Task<string> childAsk = primary.AskUserAsync(child, childCancellation.Token);
    try {
      PendingUserQuestion[] published = await WaitForQuestionsAsync(primary, 3);
      Assert.Contains(first, published);
      Assert.Contains(second, published);
      Assert.Contains(child, published);
      Assert.True(await primary.HasPendingUserQuestionAsync(primary.Id, CancellationToken.None));
      Assert.True(await primary.HasPendingUserQuestionAsync(childId, CancellationToken.None));
      Assert.False(await primary.HasPendingUserQuestionAsync(first.Id, CancellationToken.None));
      Assert.False(await primary.HasPendingUserQuestionAsync(Guid.NewGuid(), CancellationToken.None));
      Assert.False(await primary.HasPendingUserQuestionAsync(Guid.Empty, CancellationToken.None));

      Assert.False(await primary.AnswerUserQuestionAsync(Guid.NewGuid(), "not an answer"));
      Assert.True(await primary.AnswerUserQuestionAsync(first.Id, "first answer"));
      Assert.Equal("first answer", await firstAsk.WaitAsync(TestTimeout));
      Assert.False(await primary.AnswerUserQuestionAsync(first.Id, "too late"));
      Assert.True(await primary.HasPendingUserQuestionAsync(primary.Id, CancellationToken.None));
      Assert.True(await primary.HasPendingUserQuestionAsync(childId, CancellationToken.None));

      await secondCancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondAsk.WaitAsync(TestTimeout));
      Assert.False(await primary.HasPendingUserQuestionAsync(primary.Id, CancellationToken.None));
      Assert.True(await primary.HasPendingUserQuestionAsync(childId, CancellationToken.None));
      Assert.False(await primary.AnswerUserQuestionAsync(second.Id, "too late"));

      Assert.True(await primary.AnswerUserQuestionAsync(child.Id, "child answer"));
      Assert.Equal("child answer", await childAsk.WaitAsync(TestTimeout));
      Assert.False(await primary.HasPendingUserQuestionAsync(childId, CancellationToken.None));
      Assert.Empty(await primary.ReadUserQuestionsAsync());
    } finally {
      await firstCancellation.CancelAsync();
      await secondCancellation.CancelAsync();
      await childCancellation.CancelAsync();
      await DrainAsync(firstAsk, secondAsk, childAsk);
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
    PendingUserQuestion question = Question(primary.Id);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => primary.AskUserAsync(question, cancellation.Token));
    Assert.False(await primary.HasPendingUserQuestionAsync(primary.Id, CancellationToken.None));
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
    Guid unknownId = Guid.NewGuid();
    using var cancellation = new CancellationTokenSource();
    using var nextCancellation = new CancellationTokenSource();
    HarnessToolContext caller = Caller(store, manager, primary.Id, "ask") with { Background = true };
    Task<string> ask = manager.AskUserAsync(caller, "Choose?", "Context", ["one", "two"], cancellation.Token);
    Task<string>[] foreignAsks = [.. new[] { other.Id, finishedChildId, unopenedId, closedId, unknownId }
        .Select(id => primary.AskUserAsync(Question(id), cancellation.Token))];
    Task<string>? cancelledAsk = null;
    try {
      PendingUserQuestion[] published = await WaitForQuestionsAsync(primary, 6);
      PendingUserQuestion question = Assert.Single(published, item => item.AskingSessionId == primary.Id);
      Assert.Equal("Choose?", question.Question);
      Assert.Equal("Context", question.Context);
      Assert.Equal(new[] { "one", "two" }, question.Answers);
      Assert.Null(question.SourceTitle);
      Assert.True(question.Background);
      Assert.True(await primary.HasPendingUserQuestionAsync(finishedChildId, CancellationToken.None));
      Assert.True(await primary.HasPendingUserQuestionAsync(other.Id, CancellationToken.None));
      Assert.True(await primary.HasPendingUserQuestionAsync(unopenedId, CancellationToken.None));
      Assert.True(await primary.HasPendingUserQuestionAsync(closedId, CancellationToken.None));
      Assert.True(await primary.HasPendingUserQuestionAsync(unknownId, CancellationToken.None));
      Assert.True(await manager.HasPendingUserQuestionAsync(primary.Id));
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
      Assert.False(await manager.HasPendingUserQuestionAsync(finishedChildId));

      Task<string> nextAsk = manager.AskUserAsync(caller, "Another?", null, [], nextCancellation.Token);
      cancelledAsk = nextAsk;
      await WaitForQuestionsAsync(primary, 6);
      Assert.True(await manager.HasPendingUserQuestionAsync(primary.Id));
      await nextCancellation.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => nextAsk.WaitAsync(TestTimeout));
      Assert.False(await manager.HasPendingUserQuestionAsync(primary.Id));
    } finally {
      await cancellation.CancelAsync();
      await nextCancellation.CancelAsync();
      await DrainAsync([ask, .. foreignAsks]);
      if (cancelledAsk is not null)
        await DrainAsync(cancelledAsk);
    }
  }

  [Fact]
  public async Task LiveDescendantQuestionIsAttributedToTheChildNotItsPrimary() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    int conversations = 0;
    using var client = new ScriptedChatClient(Route);
    using var providers = new ProviderManager(store, () => new Uri("http://localhost:54321/api/providers/callback"),
        new ScriptedOpenAIHandler(Catalog, "application/json"));
    ProviderConnection connection = await providers.AddCustomAsync(new("user-question-fixture", LlmApi.OpenAIResponses,
        new Uri("https://fixture.invalid/v1"), AuthenticationMethod.None)).WaitAsync(TestTimeout);
    await providers.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
    new PreferenceManager(store).SetBucketModels(ModelBucket.Small, [new(connection.Id, "user-question-fixture")]);
    await using var manager = new SessionManager(store, _ => client, modelSelector: new ModelSelector(providers));
    PrimarySession primary = await manager.CreatePrimaryAsync();
    PrimarySession other = await manager.CreatePrimaryAsync();
    using var cancellation = new CancellationTokenSource();
    ISubagentRunner runner = manager;
    Task<SubagentRunResult> run = runner.RunSubagentAsync(Caller(store, manager, primary.Id, "spawn"),
        new("small", SubagentContextMode.None, "Ask the user"), cancellation.Token);
    try {
      PendingUserQuestion question = Assert.Single(await WaitForQuestionsAsync(primary, 1));
      Guid[] children = [.. database.ReadRows("SELECT session_id FROM sessions WHERE kind = 1;",
          row => Guid.Parse(row.GetString(0)))]; // raw-sql: independently identify the registered child.
      Guid childId = Assert.Single(children);
      Assert.Equal(childId, question.AskingSessionId);
      Assert.Equal("Which option?", question.Question);
      Assert.True(await manager.IsSubagentLiveAsync(childId));
      Assert.True(await primary.HasPendingUserQuestionAsync(childId, CancellationToken.None));
      Assert.True(await manager.HasPendingUserQuestionAsync(childId));
      Assert.False(await manager.HasPendingUserQuestionAsync(primary.Id));
      Assert.False(await manager.HasPendingUserQuestionAsync(other.Id));
      Assert.False(await manager.HasPendingUserQuestionAsync(question.Id));

      Assert.True(await primary.AnswerUserQuestionAsync(question.Id, "option A"));
      SubagentRunResult result = await run.WaitAsync(TestTimeout);
      Assert.Equal(childId, result.ChildSessionId);
      Assert.Null(result.Error);
      Assert.False(await manager.IsSubagentLiveAsync(childId));
      Assert.False(await manager.HasPendingUserQuestionAsync(childId));
      Assert.False(await manager.HasPendingUserQuestionAsync(primary.Id));
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
        "conversation" => Interlocked.Increment(ref conversations) == 1
            ? ToolBatch(token, new FunctionCallContent("ask", "ask_user", new Dictionary<string, object?> {
              ["question"] = "Which option?", ["choices"] = new[] { "option A", "option B" }
            })) : TextUpdates(["finished"], token),
        { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal) =>
            TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token),
        _ => throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.")
      };
    }
  }

  private static PendingUserQuestion Question(Guid askingSessionId) =>
      new(Guid.NewGuid(), askingSessionId, "Question?", null, [], null, false);

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
