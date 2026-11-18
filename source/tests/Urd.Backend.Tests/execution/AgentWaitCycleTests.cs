using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
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

public sealed class AgentWaitCycleTests {
  private const string Alpha = "Alpha agent";
  private const string Beta = "Beta agent";
  private const string AlphaTask = "alpha cycle task";
  private const string BetaTask = "beta cycle task";
  private const string AlphaQuestion = "Where is the compass?";
  private const string BetaQuestion = "Can you check the map?";

  [Theory]
  [InlineData("park closes cycle", "alpha-ask")]
  [InlineData("ask closes cycle", "beta-ask")]
  [InlineData("active background", null)]
  [InlineData("woken background", null)]
  [InlineData("foreground", "beta-ask")]
  public async Task ReciprocalAsksFailOnlyTheWaitThatClosesTheCycle(string scenario, string? failedAsk) {
    var alphaEntered = Signal();
    var releaseAlpha = Signal();
    var alphaParked = Signal();
    bool foreground = scenario == "foreground";
    bool parkFirst = scenario == "ask closes cycle";
    bool wakeBeforeAsk = scenario == "woken background";
    const string steer = "resume alpha";
    await using var fixture = new WaitFixture(Route);
    PrimarySession alpha = await fixture.BootAsync(Alpha);
    PrimarySession beta = await fixture.BootAsync(Beta);
    Guid betaBootRun = (await beta.ReadSnapshotAsync()).LastSettlement!.ExecutionId;
    using IDisposable subscription = await alpha.SubscribeAsync(change => {
      if (change.Status == "waiting")
        alphaParked.TrySetResult();
    }, watched: true);
    Guid alphaRun = await StartAsync(alpha, AlphaTask);
    try {
      if (!foreground)
        await WaitUntilAsync(() => fixture.Manager.Questions.Queued(beta.Id).Any(q => q.CallId == "alpha-ask"));
      if (parkFirst || wakeBeforeAsk)
        await alphaParked.Task.WaitAsync(TestTimeout);
      if (wakeBeforeAsk)
        Assert.Equal(alphaRun, await StartAsync(alpha, steer));
      if (!parkFirst && !foreground)
        await alphaEntered.Task.WaitAsync(TestTimeout);
      Guid betaRun = foreground
          ? await WaitForRunAsync(beta, betaBootRun) // Woken by Alpha's foreground ask.
          : await StartAsync(beta, BetaTask);
      if (!parkFirst && !foreground) {
        // Beta's wait must really exist before Alpha either answers it or attempts to park.
        await WaitUntilAsync(() => fixture.Manager.Questions.Queued(alpha.Id).Any(q => q.CallId == "beta-ask"));
        releaseAlpha.TrySetResult();
      }
      await AssertCompletedAsync(alpha, alphaRun);
      await AssertCompletedAsync(beta, betaRun);
      foreach (string callId in new[] { "alpha-ask", "beta-ask" }) {
        StoredTool result = fixture.Call(callId);
        if (callId == failedAsk) {
          Assert.Equal(ExecutionOutcome.Failed, result.Outcome);
          Assert.Contains("wait cycle", result.Error!, StringComparison.Ordinal);
        } else {
          Assert.Equal(ExecutionOutcome.Completed, result.Outcome);
          Assert.Contains("verified answer", result.ResultJson!, StringComparison.Ordinal);
        }
      }
      Assert.Empty(fixture.Manager.Questions.Queued(alpha.Id));
      Assert.Empty(fixture.Manager.Questions.Queued(beta.Id));
    } finally {
      releaseAlpha.TrySetResult();
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      if (HasText(request, AlphaTask)) {
        if (!HasResult(request, "alpha-ask") && !HasBackgroundResult(request, "alpha-ask"))
          return ToolBatch(token, Ask("alpha-ask", Beta, AlphaQuestion, wake: foreground));
        if (HasText(request, BetaQuestion) && !HasResult(request, "alpha-answer")) {
          if (scenario == "park closes cycle" && !HasBackgroundResult(request, "alpha-ask"))
            return Complete(token);
          return ToolBatch(token, Answer("alpha-answer"));
        }
        if (!foreground && !parkFirst && (!wakeBeforeAsk || HasText(request, steer))
            && !HasBackgroundResult(request, "alpha-ask"))
          return Paused(alphaEntered, releaseAlpha, token);
      } else if (HasText(request, BetaTask) || HasText(request, AlphaQuestion)) {
        if (!HasResult(request, "beta-ask"))
          return ToolBatch(token, Ask("beta-ask", Alpha, BetaQuestion));
        if (!HasResult(request, "beta-answer") && HasText(request, AlphaQuestion))
          return ToolBatch(token, Answer("beta-answer"));
      }
      return Complete(token);
    }
  }

  [Theory]
  [InlineData(false, true)]
  [InlineData(true, false)]
  public async Task ForegroundDelegationRejectsAsksToWaitingAncestors(bool nested, bool wake) {
    const string childTask = "child assignment";
    const string descendantTask = "descendant assignment";
    await using var fixture = new WaitFixture(Route);
    await fixture.ConfigureBucketAsync();
    PrimarySession alpha = await fixture.BootAsync(Alpha);
    await AssertCompletedAsync(alpha, await StartAsync(alpha, AlphaTask));

    StoredTool ask = fixture.Call("ask-ancestor");
    Assert.Equal(ExecutionOutcome.Failed, ask.Outcome);
    Assert.Contains("Interactive delegation requires a background child", ask.Error!, StringComparison.Ordinal);
    Assert.Equal(ExecutionOutcome.Completed, fixture.Call("outer").Outcome);
    if (nested)
      Assert.Equal(ExecutionOutcome.Completed, fixture.Call("inner").Outcome);
    Assert.Empty(fixture.Manager.Questions.Queued(alpha.Id));

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      if (HasAssignment(request, nested ? descendantTask : childTask)) {
        string ancestor = nested ? Alpha : "parent";
        return HasAnyResult(request) ? Complete(token) : ToolBatch(token, Ask("ask-ancestor", ancestor, BetaQuestion, wake));
      }
      if (HasAssignment(request, childTask))
        return HasAnyResult(request) ? Complete(token) : ToolBatch(token, Delegate("inner", descendantTask, background: true));
      if (HasText(request, AlphaTask) && !HasResult(request, "outer"))
        return ToolBatch(token, Delegate("outer", childTask, background: false));
      return Complete(token);
    }
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task ParkingOnABackgroundChildRejectsItsReciprocalAsk(bool parkFirst) {
    const string childTask = "waiting child assignment";
    var alphaParked = Signal();
    var alphaEntered = Signal();
    var releaseAlpha = Signal();
    var childEntered = Signal();
    var releaseChild = Signal();
    await using var fixture = new WaitFixture(Route);
    await fixture.ConfigureBucketAsync();
    PrimarySession alpha = await fixture.BootAsync(Alpha);
    using IDisposable subscription = await alpha.SubscribeAsync(change => {
      if (change.Status == "waiting")
        alphaParked.TrySetResult();
    }, watched: true);
    Guid alphaRun = await StartAsync(alpha, AlphaTask);
    try {
      await childEntered.Task.WaitAsync(TestTimeout);
      await (parkFirst ? alphaParked : alphaEntered).Task.WaitAsync(TestTimeout);
      releaseChild.TrySetResult();
      if (!parkFirst) {
        await WaitUntilAsync(() => fixture.Manager.Questions.Queued(alpha.Id).Any(q => q.CallId == "child-ask"));
        releaseAlpha.TrySetResult();
      }
      await AssertCompletedAsync(alpha, alphaRun);
      StoredTool ask = fixture.Call("child-ask");
      Assert.Equal(ExecutionOutcome.Failed, ask.Outcome);
      Assert.Contains("wait cycle", ask.Error!, StringComparison.Ordinal);
      Assert.Equal(ExecutionOutcome.Completed, fixture.Call("child").Outcome);
      Assert.Empty(fixture.Manager.Questions.Queued(alpha.Id));
    } finally {
      releaseAlpha.TrySetResult();
      releaseChild.TrySetResult();
    }

    IAsyncEnumerable<ChatResponseUpdate> Route(ScriptedRequest request, CancellationToken token) {
      if (HasAssignment(request, childTask)) {
        if (HasResult(request, "child-ask"))
          return Complete(token);
        return WaitThenAskAsync(token);
      }
      if (!HasText(request, AlphaTask))
        return Complete(token);
      if (!HasResult(request, "child"))
        return ToolBatch(token, Delegate("child", childTask, background: true));
      if (!parkFirst && !HasText(request, BetaQuestion) && !HasBackgroundResult(request, "child"))
        return Paused(alphaEntered, releaseAlpha, token);
      return Complete(token);
    }

    async IAsyncEnumerable<ChatResponseUpdate> WaitThenAskAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
      childEntered.TrySetResult();
      await releaseChild.Task.WaitAsync(token);
      await foreach (ChatResponseUpdate update in ToolBatch(token, Ask("child-ask", "parent", BetaQuestion)))
        yield return update;
    }
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task SelfAskFailsWithoutQueuingOrParking(bool wake) {
    await using var fixture = new WaitFixture((request, token) => HasText(request, AlphaTask) && !HasAnyResult(request)
        ? ToolBatch(token, Ask("self-ask", Alpha, AlphaQuestion, wake)) : Complete(token));
    PrimarySession alpha = await fixture.BootAsync(Alpha);
    await AssertCompletedAsync(alpha, await StartAsync(alpha, AlphaTask));
    Assert.Equal(ExecutionOutcome.Failed, fixture.Call("self-ask").Outcome);
    Assert.Contains("cannot ask itself", fixture.Call("self-ask").Error!, StringComparison.Ordinal);
    Assert.Empty(fixture.Manager.Questions.Queued(alpha.Id));
  }

  private static FunctionCallContent Ask(string callId, string recipient, string prompt, bool wake = true) =>
      new(callId, "ask_agent", new Dictionary<string, object?> { ["recipient"] = recipient, ["prompt"] = prompt, ["wake"] = wake });

  private static FunctionCallContent Answer(string callId) =>
      new(callId, "answer_agent", new Dictionary<string, object?> { ["answer"] = "verified answer" });

  private static FunctionCallContent Delegate(string callId, string task, bool background) =>
      new(callId, "subagent", new Dictionary<string, object?> {
        ["bucket"] = "small", ["context"] = "none", ["task"] = task, ["background"] = background
      });

  private static bool HasText(ScriptedRequest request, string text) => request.Messages.Any(message =>
      message.Role == ChatRole.User && message.Text.Contains(text, StringComparison.Ordinal));

  // A delegated task reaches its child as a whole user assignment. Recipients only quote the task
  // back incidentally inside larger envelopes (a presented question cites the asker's title), so
  // matching the complete input text is what separates child requests from primary requests.
  private static bool HasAssignment(ScriptedRequest request, string task) => request.Messages.Any(
      message => message.Role == ChatRole.User && message.Text == task);

  private static IEnumerable<FunctionResultContent> Results(ScriptedRequest request) =>
      request.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>();

  private static bool HasResult(ScriptedRequest request, string callId) => Results(request).Any(result => result.CallId == callId);
  private static bool HasAnyResult(ScriptedRequest request) => Results(request).Any();
  private static bool HasBackgroundResult(ScriptedRequest request, string callId) => request.Messages.Any(message =>
      message.Text.Contains("[TOOL-ORIGIN: background tool completion", StringComparison.Ordinal)
      && message.Text.Contains(callId, StringComparison.Ordinal));

  private static async Task WaitUntilAsync(Func<bool> ready) {
    using var timeout = new CancellationTokenSource(TestTimeout);
    while (!ready())
      await Task.Delay(10, timeout.Token);
  }

  private static async Task<Guid> WaitForRunAsync(PrimarySession session, Guid afterRun) {
    using var timeout = new CancellationTokenSource(TestTimeout);
    while (true) {
      SessionSnapshot snapshot = await session.ReadSnapshotAsync(timeout.Token);
      if ((snapshot.UnsettledExecutionId ?? snapshot.LastSettlement?.ExecutionId) is { } id && id != afterRun)
        return id;
      await Task.Delay(10, timeout.Token);
    }
  }

  private static async Task<Guid> StartAsync(PrimarySession session, string task) {
    long revision = (await session.ReadSnapshotAsync().WaitAsync(TestTimeout)).HistoryRevision;
    UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), revision, task, TestLlmSelection.Default))
        .WaitAsync(TestTimeout);
    Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
    return Assert.IsType<Guid>(receipt.ExecutionId);
  }

  private static async Task AssertCompletedAsync(PrimarySession session, Guid run) => Assert.Equal(ExecutionOutcome.Completed,
      (await session.WaitForExecutionFinishAsync(run).WaitAsync(TestTimeout)).Outcome);

  private sealed class WaitFixture : IAsyncDisposable {
    private readonly TemporaryDatabase database = new();
    private readonly UrdStore store;
    private readonly ScriptedChatClient client;
    private readonly ProviderManager providers;
    internal SessionManager Manager { get; }

    internal WaitFixture(Func<ScriptedRequest, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> conversation) {
      store = UrdStore.Open(database.Path);
      client = new((request, token) => request.Purpose switch {
        "title" => TextUpdates([request.LastUserText], token),
        "conversation" => conversation(request, token),
        { } pruning when pruning.StartsWith("pruning.", StringComparison.Ordinal) =>
            TextUpdates([PrepassTolerantChatClient.KeepAll(request.Messages)], token),
        _ => throw new InvalidOperationException($"Unexpected purpose '{request.Purpose}'.")
      });
      providers = new(store, () => new Uri("http://localhost:54321/api/providers/callback"),
          new ScriptedOpenAIHandler("""
            {"object":"list","data":[{"id":"wait-test","object":"model","created":1700000000,"owned_by":"fixture","context_window":32000}]}
            """, "application/json"));
      // Opt in only here; production does not expose either communication tool yet.
      Manager = new SessionManager(store, _ => client, [new AskAgentTool(), new AnswerAgentTool()],
          Path.GetDirectoryName(database.Path)!, new ModelSelector(providers));
    }

    internal async Task ConfigureBucketAsync() {
      ProviderConnection connection = await providers.AddCustomAsync(new("wait-test", LlmApi.OpenAIResponses,
          new Uri("https://fixture.invalid/v1"), AuthenticationMethod.None)).WaitAsync(TestTimeout);
      await providers.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      new PreferenceManager(store).SetBucketModel(ModelBucket.Small, new(connection.Id, "wait-test"));
    }

    internal async Task<PrimarySession> BootAsync(string title) {
      PrimarySession session = await Manager.CreatePrimaryAsync().WaitAsync(TestTimeout);
      await AssertCompletedAsync(session, await StartAsync(session, title));
      Assert.Equal(title, store.Sessions.ReadAgentTitle(session.Id).Title);
      return session;
    }

    internal StoredTool Call(string callId) => Assert.Single(StoredTools.Read(database), result => result.CallId == callId);

    public async ValueTask DisposeAsync() {
      try {
        await Manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      } finally {
        await providers.ShutdownAsync().WaitAsync(TestTimeout);
        providers.Dispose();
        store.Dispose();
        client.Dispose();
        database.Dispose();
      }
    }
  }
}
