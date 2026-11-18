using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
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

public sealed class AgentQuestionExecutionTests {
  [Fact]
  public async Task BusyRecipientPresentsFifoAlongsideToolResultsAndRejectsLateAnswers() {
    await using var fixture = await QuestionFixture.OpenAsync();
    fixture.Respond(Probe("busy"), Probe("first"), Probe("second"), Complete);
    Guid execution = await fixture.StartAsync("work");
    CapturedCall busy = await fixture.NextCallAsync();
    Assert.True((await new AnswerAgentTool().RunAsync(AnswerArguments("nothing"), busy.Context, CancellationToken.None)).IsError);

    Task<AgentQuestionResult> first = fixture.Ask("first question", wake: false);
    await fixture.WaitForQueueAsync(1);
    Task<AgentQuestionResult> second = fixture.Ask("second question", wake: false);
    await fixture.WaitForQueueAsync(2);
    Assert.Empty(fixture.QuestionRows());
    Assert.False(first.IsCompleted);
    Assert.False(second.IsCompleted);

    busy.Release.TrySetResult();
    CapturedCall firstCall = await fixture.NextCallAsync();
    IReadOnlyList<ChatMessage> firstRequest = fixture.Client.LastRequest;
    ChatMessage question = Assert.Single(firstRequest, message => message.Text.Contains("first question", StringComparison.Ordinal));
    Assert.Contains("agent-authored", question.Text, StringComparison.Ordinal);
    Assert.Contains(fixture.SenderTitle, question.Text, StringComparison.Ordinal);
    Assert.DoesNotContain(firstRequest, message => message.Text.Contains("second question", StringComparison.Ordinal));
    Assert.Contains(firstRequest.SelectMany(message => message.Contents).OfType<FunctionResultContent>(), result => result.CallId == "busy");
    var firstRow = Assert.Single(fixture.QuestionRows());
    Assert.Equal(fixture.Sender.Id, firstRow.Sender);
    Assert.Equal(firstCall.Context.RequestId, firstRow.Request);
    Assert.Equal((int)InputProvenance.AgentQuestion, firstRow.Provenance);

    Assert.True(fixture.Questions.AnswerAgent(firstCall.Context, "first answer"));
    Assert.False(fixture.Questions.AnswerAgent(firstCall.Context, "duplicate"));
    Assert.Equal("first answer", (await first.WaitAsync(TestTimeout)).Answer);
    Assert.False(second.IsCompleted);
    firstCall.Release.TrySetResult();

    CapturedCall secondCall = await fixture.NextCallAsync();
    Assert.Contains(fixture.Client.LastRequest, message => message.Text.Contains("second question", StringComparison.Ordinal));
    Assert.False(fixture.Questions.AnswerAgent(firstCall.Context, "late"));
    Assert.False(second.IsCompleted);
    Assert.True(fixture.Questions.AnswerAgent(secondCall.Context, "second answer"));
    Assert.Equal("second answer", (await second.WaitAsync(TestTimeout)).Answer);
    Assert.Equal(2, fixture.QuestionRows().Count);
    secondCall.Release.TrySetResult();
    Assert.Equal(ExecutionOutcome.Completed, (await fixture.FinishAsync(execution)).Outcome);
  }

  [Fact]
  public async Task WakeFalseDoesNotStartAnIdleRecipientButItsNextRunPresentsTheQuestion() {
    await using var fixture = await QuestionFixture.OpenAsync();
    Task<AgentQuestionResult> ask = fixture.Ask("opportunistic question", wake: false);
    await fixture.WaitForQueueAsync(1);
    Assert.Null((await fixture.Recipient.ReadSnapshotAsync()).UnsettledExecutionId);
    Assert.Empty(fixture.QuestionRows());
    Assert.False(ask.IsCompleted);

    fixture.Respond(Probe("answer"), Complete);
    Guid execution = await fixture.StartAsync("resume");
    CapturedCall answer = await fixture.NextCallAsync();
    Assert.Contains(fixture.Client.LastRequest, message => message.Text.Contains("opportunistic question", StringComparison.Ordinal));
    Assert.True(fixture.Questions.AnswerAgent(answer.Context, "yes"));
    Assert.Equal("yes", (await ask.WaitAsync(TestTimeout)).Answer);
    answer.Release.TrySetResult();
    Assert.Equal(ExecutionOutcome.Completed, (await fixture.FinishAsync(execution)).Outcome);
  }

  [Fact]
  public async Task WakeTrueStartsAnIdleRecipientUsingItsSavedSelectionWithoutSkippingTheHead() {
    await using var fixture = await QuestionFixture.OpenAsync();
    Task<AgentQuestionResult> head = fixture.Ask("earlier question", wake: false);
    await fixture.WaitForQueueAsync(1);
    fixture.Respond(Probe("head"), Probe("wake"), Complete);
    Task<AgentQuestionResult> ask = fixture.Ask("wake question", wake: true);
    CapturedCall first = await fixture.NextCallAsync();
    Assert.Contains(fixture.Client.LastRequest, message => message.Text.Contains("earlier question", StringComparison.Ordinal));
    Assert.DoesNotContain(fixture.Client.LastRequest, message => message.Text.Contains("wake question", StringComparison.Ordinal));
    Assert.Equal(first.Context.RequestId, Assert.Single(fixture.QuestionRows()).Request);
    Assert.True(fixture.Questions.AnswerAgent(first.Context, "first"));
    Assert.Equal("first", (await head.WaitAsync(TestTimeout)).Answer);
    first.Release.TrySetResult();

    CapturedCall answer = await fixture.NextCallAsync();
    Assert.Equal(first.Context.ExecutionId, answer.Context.ExecutionId);
    Assert.Contains(fixture.Client.LastRequest, message => message.Text.Contains("wake question", StringComparison.Ordinal));
    Assert.True(fixture.Questions.AnswerAgent(answer.Context, "awake"));
    Assert.Equal("awake", (await ask.WaitAsync(TestTimeout)).Answer);
    answer.Release.TrySetResult();
    Assert.Equal(ExecutionOutcome.Completed, (await fixture.FinishAsync(answer.Context.ExecutionId)).Outcome);
  }

  [Fact]
  public async Task WakeTrueSignalsARecipientWaitingForBackgroundWork() {
    await using var fixture = await QuestionFixture.OpenAsync();
    var waiting = Signal();
    using IDisposable subscription = await fixture.Recipient.SubscribeAsync(change => {
      if (change.Status == "waiting")
        waiting.TrySetResult();
    }, watched: true);
    fixture.Respond(token => ToolBatch(token, new FunctionCallContent("background", "question_probe",
        new Dictionary<string, object?> { ["background"] = true })), Complete, Probe("answer"), Complete, Complete);
    Guid execution = await fixture.StartAsync("background work");
    CapturedCall background = await fixture.NextCallAsync();
    await waiting.Task.WaitAsync(TestTimeout);
    Task<AgentQuestionResult> ask = fixture.Ask("waiting question", wake: true);
    CapturedCall answer = await fixture.NextCallAsync();
    Assert.Equal(execution, answer.Context.ExecutionId);
    Assert.Contains(fixture.Client.LastRequest, message => message.Text.Contains("waiting question", StringComparison.Ordinal));
    Assert.True(fixture.Questions.AnswerAgent(answer.Context, "signalled"));
    Assert.Equal("signalled", (await ask.WaitAsync(TestTimeout)).Answer);
    answer.Release.TrySetResult();
    background.Release.TrySetResult();
    Assert.Equal(ExecutionOutcome.Completed, (await fixture.FinishAsync(execution)).Outcome);
  }

  [Fact]
  public async Task StopFailsThePresentedAskBeforeJoiningOwnedWork() {
    await using var fixture = await QuestionFixture.OpenAsync();
    var waiting = Signal();
    var entered = Signal();
    var providerRelease = Signal();
    (TaskCompletionSource cleanupEntered, TaskCompletionSource cleanupRelease) = fixture.HoldProbeCleanup();
    using IDisposable subscription = await fixture.Recipient.SubscribeAsync(change => {
      if (change.Status == "waiting")
        waiting.TrySetResult();
    }, watched: true);
    fixture.Respond(token => ToolBatch(token, new FunctionCallContent("background", "question_probe",
        new Dictionary<string, object?> { ["background"] = true })), Complete,
        token => Paused(entered, providerRelease, token));
    Guid execution = await fixture.StartAsync("background work");
    await fixture.NextCallAsync();
    await waiting.Task.WaitAsync(TestTimeout);
    Task<AgentQuestionResult> ask = fixture.Ask("stop question", wake: true);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      Task<SessionExecutionResult> settlement = fixture.FinishAsync(execution);
      Assert.Equal(StopStatus.Requested, (await fixture.Recipient.RequestStopAsync(execution)).Status);
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.Equal(AgentQuestions.RecipientFinished, (await ask.WaitAsync(TestTimeout)).Error);
      Assert.False(settlement.IsCompleted);
      cleanupRelease.TrySetResult();
      Assert.Equal(ExecutionOutcome.Cancelled, (await settlement).Outcome);
    } finally {
      cleanupRelease.TrySetResult();
      providerRelease.TrySetResult();
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task SettlingOrStoppingFailsOnlyThePresentedQuestion(bool stop) {
    await using var fixture = await QuestionFixture.OpenAsync();
    var entered = Signal();
    var release = Signal();
    fixture.Respond(token => Paused(entered, release, token));
    Task<AgentQuestionResult> first = fixture.Ask("unanswered question", wake: true);
    try {
      await entered.Task.WaitAsync(TestTimeout);
      Guid execution = Assert.IsType<Guid>((await fixture.Recipient.ReadSnapshotAsync()).UnsettledExecutionId);
      Task<AgentQuestionResult> second = fixture.Ask("still queued", wake: stop);
      await fixture.WaitForQueueAsync(2);
      if (stop)
        Assert.Equal(StopStatus.Requested, (await fixture.Recipient.RequestStopAsync(execution)).Status);
      else
        release.TrySetResult();
      Assert.Equal(stop ? ExecutionOutcome.Cancelled : ExecutionOutcome.Completed,
          (await fixture.FinishAsync(execution)).Outcome);
      AgentQuestionResult failed = await first.WaitAsync(TestTimeout);
      Assert.Null(failed.Answer);
      Assert.Equal(AgentQuestions.RecipientFinished, failed.Error);
      Assert.False(second.IsCompleted);
      Assert.Single(fixture.Manager.Questions.Queued(fixture.Recipient.Id));
      Assert.Null((await fixture.Recipient.ReadSnapshotAsync()).UnsettledExecutionId);

      fixture.Respond(Probe("successor"), Complete);
      Guid next = await fixture.StartAsync("next run");
      CapturedCall successor = await fixture.NextCallAsync();
      Assert.True(fixture.Questions.AnswerAgent(successor.Context, "later"));
      Assert.Equal("later", (await second.WaitAsync(TestTimeout)).Answer);
      successor.Release.TrySetResult();
      Assert.Equal(ExecutionOutcome.Completed, (await fixture.FinishAsync(next)).Outcome);
    } finally {
      release.TrySetResult();
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task RestartNeverRepresentsAnInterruptedQuestion(bool started) {
    await using var fixture = await QuestionFixture.OpenAsync();
    await fixture.Manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
    var run = new ExecutionIdentity(fixture.Recipient.Id, Guid.NewGuid(), 0);
    var question = new QuestionPresentation(Guid.NewGuid(), fixture.Sender.Id, "interrupted question", First: true, Wakes: true);
    Guid request = Guid.NewGuid();
    fixture.Store.Executions.StartQuestionRun(run, request, question, TestLlmSelection.Default);
    if (started)
      Assert.True(fixture.Store.Executions.StartRequest(run, request));
    fixture.Store.RecoverInterruptedExecutions();

    await using SessionManager restarted = fixture.CreateManager();
    PrimarySession recipient = await restarted.GetPrimaryAsync(fixture.Recipient.Id);
    Assert.Empty((await recipient.ReadNotSentInputsAsync()).Inputs);
    fixture.Respond(Probe("after restart"), Complete);
    Guid execution = await QuestionFixture.StartAsync(recipient, "new run");
    CapturedCall call = await fixture.NextCallAsync();
    int historical = fixture.Client.LastRequest.Count(message => message.Text.Contains("interrupted question", StringComparison.Ordinal));
    Assert.Equal(started ? 1 : 0, historical);
    Assert.False(((IAgentQuestions)restarted).AnswerAgent(call.Context, "stale answer"));
    call.Release.TrySetResult();
    Assert.Equal(ExecutionOutcome.Completed,
        (await recipient.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout)).Outcome);
  }

  private static JsonElement AnswerArguments(string answer) => JsonSerializer.SerializeToElement(new { answer });

  private static Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> Probe(string callId) =>
      token => ToolBatch(token, new FunctionCallContent(callId, "question_probe"));

  private sealed record CapturedCall(HarnessToolContext Context, TaskCompletionSource Release);

  // Captures real request identities without registering ask_agent or answer_agent in the catalog.
  private sealed class QuestionProbe : HarnessTool {
    internal Channel<CapturedCall> Calls { get; } = Channel.CreateUnbounded<CapturedCall>();
    internal TaskCompletionSource? CleanupEntered { get; set; }
    internal TaskCompletionSource? CleanupRelease { get; set; }
    public override string Name => "question_probe";
    public override string Description => "Capture a request context.";
    public override ToolRelevance RelevantWhen => ToolRelevance.Always;
    public override JsonElement ParametersSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object" });

    public override async Task<ToolOutput> RunAsync(JsonElement arguments, HarnessToolContext context, CancellationToken cancellation) {
      var release = Signal();
      if (context.CallId == "sender")
        release.TrySetResult();
      await Calls.Writer.WriteAsync(new(context, release), cancellation);
      try {
        await release.Task.WaitAsync(cancellation);
      } finally {
        if (context.CallId == "background" && cancellation.IsCancellationRequested && CleanupEntered is { } entered) {
          entered.TrySetResult();
          await CleanupRelease!.Task;
        }
      }
      return new ToolOutput("probe complete", []);
    }
  }

  private sealed class QuestionFixture : IAsyncDisposable {
    private readonly TemporaryDatabase database = new();
    private readonly UrdStore store;
    private readonly QuestionProbe probe = new();
    private readonly ConcurrentQueue<Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>> responses = new();
    private HarnessToolContext senderContext = null!;

    internal ScriptedChatClient Client { get; }
    internal SessionManager Manager { get; }
    internal UrdStore Store => store;
    internal PrimarySession Sender { get; private set; } = null!;
    internal PrimarySession Recipient { get; private set; } = null!;
    internal string SenderTitle { get; private set; } = null!;
    private string recipientTitle = null!;
    internal IAgentQuestions Questions => Manager;

    private QuestionFixture() {
      store = UrdStore.Open(database.Path);
      Client = new ScriptedChatClient(token => responses.TryDequeue(out var response)
          ? response(token) : throw new InvalidOperationException("No scripted response remains."));
      IChatClient client = PrepassTolerantChatClient.WrapIfNeeded(Client, disposeInner: false);
      Manager = new SessionManager(store, _ => client, [probe], Path.GetDirectoryName(database.Path)!);
    }

    internal static async Task<QuestionFixture> OpenAsync() {
      var fixture = new QuestionFixture();
      try {
        fixture.Sender = await fixture.Manager.CreatePrimaryAsync();
        fixture.Respond(Probe("sender"), Complete);
        Guid senderRun = await StartAsync(fixture.Sender, "sender boot");
        fixture.senderContext = (await fixture.NextCallAsync()).Context;
        Assert.Equal(ExecutionOutcome.Completed, (await fixture.Sender.WaitForExecutionFinishAsync(senderRun).WaitAsync(TestTimeout)).Outcome);
        fixture.SenderTitle = await fixture.TitleAsync(fixture.Sender.Id).WaitAsync(TestTimeout);
        fixture.Recipient = await fixture.Manager.CreatePrimaryAsync();
        fixture.Respond(Complete);
        Guid recipientRun = await fixture.StartAsync("recipient boot");
        Assert.Equal(ExecutionOutcome.Completed, (await fixture.FinishAsync(recipientRun)).Outcome);
        fixture.recipientTitle = await fixture.TitleAsync(fixture.Recipient.Id).WaitAsync(TestTimeout);
        return fixture;
      } catch {
        await fixture.DisposeAsync();
        throw;
      }
    }

    internal (TaskCompletionSource Entered, TaskCompletionSource Release) HoldProbeCleanup() {
      var entered = Signal();
      var release = Signal();
      probe.CleanupEntered = entered;
      probe.CleanupRelease = release;
      return (entered, release);
    }

    internal void Respond(params Func<CancellationToken, IAsyncEnumerable<ChatResponseUpdate>>[] scripts) {
      foreach (var script in scripts)
        responses.Enqueue(script);
    }

    internal Task<CapturedCall> NextCallAsync() => probe.Calls.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
    internal Task<Guid> StartAsync(string text) => StartAsync(Recipient, text);
    internal static async Task<Guid> StartAsync(PrimarySession session, string text) {
      long revision = (await session.ReadSnapshotAsync()).HistoryRevision;
      UserMessageReceipt receipt = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), revision, text, TestLlmSelection.Default));
      Assert.Equal(MessageAcceptance.Accepted, receipt.Acceptance);
      return Assert.IsType<Guid>(receipt.ExecutionId);
    }

    internal Task<SessionExecutionResult> FinishAsync(Guid execution) =>
        Recipient.WaitForExecutionFinishAsync(execution).WaitAsync(TestTimeout);

    internal Task<AgentQuestionResult> Ask(string prompt, bool wake) =>
        Questions.AskAgentAsync(senderContext with { CallId = Guid.NewGuid().ToString("N") },
            recipientTitle, prompt, wake, CancellationToken.None);

    internal async Task WaitForQueueAsync(int count) {
      using var timeout = new CancellationTokenSource(TestTimeout);
      while (Manager.Questions.Queued(Recipient.Id).Count != count)
        await Task.Delay(10, timeout.Token);
    }

    internal List<(Guid Sender, Guid Request, int Provenance)> QuestionRows() => database.ReadRows(
        $"SELECT sender_session_id, request_id, provenance FROM execution_inputs WHERE session_id = '{Recipient.Id:D}' AND provenance = {(int)InputProvenance.AgentQuestion} ORDER BY ordinal;",
        row => (Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)), row.GetInt32(2)));

    internal SessionManager CreateManager() => new(store,
        _ => PrepassTolerantChatClient.WrapIfNeeded(Client, disposeInner: false), [probe], Path.GetDirectoryName(database.Path)!);

    private async Task<string> TitleAsync(Guid id) {
      using var timeout = new CancellationTokenSource(TestTimeout);
      while (true) {
        if (store.Sessions.ReadAgentTitle(id).Title is { } title)
          return title;
        await Task.Delay(10, timeout.Token);
      }
    }

    public async ValueTask DisposeAsync() {
      await Manager.ShutdownSessionsAsync().WaitAsync(TestTimeout);
      store.Dispose();
      Client.Dispose();
      database.Dispose();
    }
  }
}
