using Urd.Core.Sessions;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

// One recipient's FIFO mailbox with a single current head, exactly-once settlement against
// cancellation and failure, and closed mailboxes that reject new asks.
public sealed class AgentQuestionsTests {
  private static readonly Guid Recipient = Guid.NewGuid();
  private static readonly Guid Sender = Guid.NewGuid();
  private static readonly Guid RequestId = Guid.NewGuid();

  [Fact]
  public async Task EnqueuePreservesFifoOrderWithOneCurrentHead() {
    var questions = new AgentQuestions();
    using var firstToken = new CancellationTokenSource();
    using var secondToken = new CancellationTokenSource();
    Task<AgentQuestionResult> first = questions.Enqueue(Recipient, Sender, RequestId, "call-1", "why?", true, firstToken.Token);
    Task<AgentQuestionResult> second = questions.Enqueue(Recipient, Sender, RequestId, "call-2", "why not?", false, secondToken.Token);

    QueuedQuestion[] queued = questions.Queued(Recipient).ToArray();
    Assert.Equal(["call-1", "call-2"], queued.Select(question => question.CallId));
    Assert.Equal(Recipient, queued[0].RecipientId);
    Assert.Equal(Sender, queued[0].SenderId);
    Assert.Equal(RequestId, queued[0].RequestId);
    Assert.Equal("why?", queued[0].Prompt);
    Assert.True(queued[0].Wake);
    Assert.False(queued[1].Wake);

    Guid firstRecipientRequestId = Guid.NewGuid();
    QuestionPresentation firstPresentation = Assert.IsType<QuestionPresentation>(
        questions.Present(Recipient, firstRecipientRequestId));
    Assert.True(firstPresentation.First);
    Assert.True(questions.AnswerPresented(Recipient, firstRecipientRequestId, "head answer"));
    Assert.Equal("head answer", (await first.WaitAsync(TestTimeout)).Answer);
    queued = questions.Queued(Recipient).ToArray();
    Assert.Equal(["call-2"], queued.Select(question => question.CallId));

    Guid secondRecipientRequestId = Guid.NewGuid();
    QuestionPresentation secondPresentation = Assert.IsType<QuestionPresentation>(
        questions.Present(Recipient, secondRecipientRequestId));
    Assert.True(secondPresentation.First);
    Assert.False(questions.AnswerPresented(Recipient, firstRecipientRequestId, "late first answer"));
    Assert.True(questions.AnswerPresented(Recipient, secondRecipientRequestId, "second answer"));
    Assert.Equal("second answer", (await second.WaitAsync(TestTimeout)).Answer);
    Assert.Empty(questions.Queued(Recipient));
  }

  [Fact]
  public async Task AnswerSettlesARecordExactlyOnce() {
    var questions = new AgentQuestions();
    using var token = new CancellationTokenSource();
    Task<AgentQuestionResult> ask = questions.Enqueue(Recipient, Sender, RequestId, "call-1", "why?", true, token.Token);
    Guid recipientRequestId = Guid.NewGuid();
    Assert.NotNull(questions.Present(Recipient, recipientRequestId));

    Assert.True(questions.AnswerPresented(Recipient, recipientRequestId, "the answer"));
    Assert.False(questions.AnswerPresented(Recipient, recipientRequestId, "the repeat"));
    AgentQuestionResult result = await ask.WaitAsync(TestTimeout);
    Assert.Equal("the answer", result.Answer);
    Assert.Null(result.Error);
    Assert.Empty(questions.Queued(Recipient));
  }

  [Fact]
  public async Task AnswerPresentedRequiresAQuestionShownToThatRequest() {
    var questions = new AgentQuestions();
    using var token = new CancellationTokenSource();
    Guid recipientRequestId = Guid.NewGuid();
    Assert.False(questions.AnswerPresented(Recipient, recipientRequestId, "nobody answered"));
    Task<AgentQuestionResult> ask = questions.Enqueue(Recipient, Sender, RequestId, "call-1", "why?", true, token.Token);
    Assert.False(questions.AnswerPresented(Recipient, recipientRequestId, "not presented"));
    Assert.NotNull(questions.Present(Recipient, recipientRequestId));
    Assert.False(questions.AnswerPresented(Recipient, Guid.NewGuid(), "not this request"));
    Assert.True(questions.AnswerPresented(Recipient, recipientRequestId, "the answer"));
    Assert.Equal("the answer", (await ask.WaitAsync(TestTimeout)).Answer);
    Assert.False(questions.AnswerPresented(Recipient, recipientRequestId, "answered twice"));
  }

  [Fact]
  public async Task AskCancellationRemovesItsOwnRecord() {
    var questions = new AgentQuestions();
    using var cancellation = new CancellationTokenSource();
    using var otherToken = new CancellationTokenSource();
    Task<AgentQuestionResult> ask = questions.Enqueue(Recipient, Sender, RequestId, "call-1", "why?", true, cancellation.Token);
    Task<AgentQuestionResult> other = questions.Enqueue(Recipient, Sender, RequestId, "call-2", "why not?", true, otherToken.Token);

    await cancellation.CancelAsync().WaitAsync(TestTimeout);
    Assert.Equal(["call-2"], questions.Queued(Recipient).Select(question => question.CallId));
    await Assert.ThrowsAsync<TaskCanceledException>(() => ask.WaitAsync(TestTimeout));

    Guid recipientRequestId = Guid.NewGuid();
    Assert.NotNull(questions.Present(Recipient, recipientRequestId));
    Assert.True(questions.AnswerPresented(Recipient, recipientRequestId, "still answerable"));
    Assert.Equal("still answerable", (await other.WaitAsync(TestTimeout)).Answer);
  }

  [Fact]
  public async Task AnAskCancelledBeforeEnqueueSettlesCancelled() {
    var questions = new AgentQuestions();
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync().WaitAsync(TestTimeout);

    Task<AgentQuestionResult>? ask = questions.Enqueue(Recipient, Sender, RequestId, "call-1", "why?", true, cancellation.Token);
    Assert.NotNull(ask);
    Assert.Empty(questions.Queued(Recipient));
    await Assert.ThrowsAsync<TaskCanceledException>(() => ask.WaitAsync(TestTimeout));
  }

  [Fact]
  public async Task AnswerAndCancellationRaceForExactlyOneTerminalTransition() {
    for (int round = 0; round < 25; round++) {
      var questions = new AgentQuestions();
      var cancellation = new CancellationTokenSource();
      try {
        Task<AgentQuestionResult> ask = questions.Enqueue(Recipient, Sender, RequestId, "call-1", "why?", true, cancellation.Token);
        Guid recipientRequestId = Guid.NewGuid();
        Assert.NotNull(questions.Present(Recipient, recipientRequestId));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> answered = Task.Run(async () => {
          await gate.Task;
          return questions.AnswerPresented(Recipient, recipientRequestId, "the answer");
        });
        Task cancelled = Task.Run(async () => {
          await gate.Task;
          await cancellation.CancelAsync();
        });
        gate.TrySetResult();
        await Task.WhenAll(answered, cancelled).WaitAsync(TestTimeout);
        bool answerWon = await answered.WaitAsync(TestTimeout);

        try {
          AgentQuestionResult result = await ask.WaitAsync(TestTimeout);
          Assert.True(answerWon);
          Assert.Equal("the answer", result.Answer);
        } catch (OperationCanceledException) {
          Assert.False(answerWon);
        }

        Assert.Empty(questions.Queued(Recipient));
      } finally {
        cancellation.Dispose();
      }
    }
  }

  [Fact]
  public async Task ClosingTheRecipientFailsRemainingQuestionsAndRejectsNewOnes() {
    var questions = new AgentQuestions();
    using var firstToken = new CancellationTokenSource();
    using var secondToken = new CancellationTokenSource();
    Task<AgentQuestionResult> queued = questions.Enqueue(Recipient, Sender, RequestId, "call-1", "why?", true, firstToken.Token);
    Task<AgentQuestionResult> head = questions.Enqueue(Recipient, Sender, RequestId, "call-2", "why not?", true, secondToken.Token);
    questions.CloseRecipient(Recipient);

    AgentQuestionResult result = await queued.WaitAsync(TestTimeout);
    Assert.Null(result.Answer);
    Assert.Equal(AgentQuestions.RecipientFinished, result.Error);
    Assert.Equal(AgentQuestions.RecipientFinished, (await head.WaitAsync(TestTimeout)).Error);
    Assert.Empty(questions.Queued(Recipient));
    AgentQuestionResult rejected = await questions.Enqueue(Recipient, Sender, RequestId, "call-3", "why?", true, firstToken.Token);
    Assert.Equal(AgentQuestions.RecipientFinished, rejected.Error);
  }

  [Fact]
  public async Task CloseAllFailsEveryMailboxAndClosesIt() {
    var questions = new AgentQuestions();
    using var firstToken = new CancellationTokenSource();
    using var secondToken = new CancellationTokenSource();
    Guid other = Guid.NewGuid();
    Task<AgentQuestionResult> first = questions.Enqueue(Recipient, Sender, RequestId, "call-1", "why?", true, firstToken.Token);
    Task<AgentQuestionResult> second = questions.Enqueue(other, Sender, RequestId, "call-1", "why?", true, secondToken.Token);
    questions.CloseAll();

    Assert.Equal(AgentQuestions.RecipientFinished, (await first.WaitAsync(TestTimeout)).Error);
    Assert.Equal(AgentQuestions.RecipientFinished, (await second.WaitAsync(TestTimeout)).Error);
    Assert.Empty(questions.Queued(Recipient));
    Assert.Empty(questions.Queued(other));
    AgentQuestionResult rejected = await questions.Enqueue(Recipient, Sender, RequestId, "call-2", "why?", true, firstToken.Token);
    AgentQuestionResult otherRejected = await questions.Enqueue(other, Sender, RequestId, "call-2", "why?", true, secondToken.Token);
    Assert.Equal(AgentQuestions.RecipientFinished, rejected.Error);
    Assert.Equal(AgentQuestions.RecipientFinished, otherRejected.Error);
  }
}
