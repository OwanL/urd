using Microsoft.Extensions.AI;
using Urd.Core.Concurrency;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Owns delivery of questions to this session through the agent question broker.
internal sealed class QuestionDelivery(SessionBase session, UrdStore store, AsyncLock sessionLock, AgentQuestions? agentQuestions) {
  internal async Task<AgentQuestionResult> QueueQuestionAsync(HarnessToolContext caller, string prompt,
      bool wake, CancellationToken cancellation) {
    TaskCompletionSource? executionStart = null;
    Task<AgentQuestionResult> waiting;
    using (await sessionLock.AcquireAsync(cancellation)) {
      if (!session.CanStartWork)
        return new(null, "The recipient agent is not accepting work.");

      SessionExecutionDriver? active = session.CurrentExecution;
      if (wake && active is { IsStopping: true })
        return new(null, "The recipient agent is stopping; try again after it stops.");

      AgentQuestions broker = agentQuestions!;
      var source = new AgentWaitSource(caller.SessionId, caller.ExecutionId, caller.Background || !wake);
      waiting = broker.Enqueue(session.Id, source, caller.RequestId, caller.CallId, prompt, wake, cancellation, out Guid questionId);
      if (wake && !waiting.IsCompleted) {
        if (active is not null)
          active.Wake();
        else {
          try {
            executionStart = StartQuestionRun(broker);
          } catch (Exception failure) {
            broker.Fail(session.Id, questionId, failure.Message);
          }
        }
      }
    }

    executionStart?.SetResult();
    return await waiting;
  }

  // Caller holds the lock. The waking ask can bring an earlier non-waking head along, never skip it.
  private TaskCompletionSource? StartQuestionRun(AgentQuestions broker) {
    LlmSelection selection = store.Executions.ReadLatestSelection(session.Id)
        ?? throw new InvalidOperationException("The recipient agent has no saved model selection.");
    Guid requestId = Guid.NewGuid();
    if (broker.Present(session.Id, requestId) is not { } question)
      return null;

    if (!question.Wakes) {
      broker.Withdraw(session.Id, question, requestId);
      return null;
    }

    var run = new ExecutionIdentity(session.Id, Guid.NewGuid(), session.HistoryRevision);
    LlmRequest firstRequest;
    try {
      firstRequest = session.WriteToStore(() => store.Executions.StartQuestionRun(run, requestId, question, selection));
    } catch {
      broker.Withdraw(session.Id, question, requestId);
      throw;
    }
    session.MarkAcceptedInput();
    TaskCompletionSource executionStart = session.StartExecutionAfterUnlock(run.ExecutionId, selection, firstRequest: firstRequest);
    session.PublishChange();
    return executionStart;
  }

  // Caller holds the session lock; the driver publishes after updating its waiting state.
  internal LlmRequest? CreateNextRequest(ExecutionIdentity identity) {
    if (!session.CanRecordExecution(identity))
      return null;

    Guid requestId = Guid.NewGuid();
    QuestionPresentation? question = agentQuestions?.Present(session.Id, requestId);
    LlmRequest? next = null;
    try {
      next = store.Executions.CreateNextRequest(identity, requestId, question);
      return next;
    } finally {
      if (next is null && question is not null)
        agentQuestions!.Withdraw(session.Id, question, requestId);
    }
  }

  // Project a live, request-bound question after context preparation. Neither the reminder nor
  // the temporary answer-tool offer belongs in saved history or the starting-context cache.
  internal (bool Presented, ChatMessage? Reminder) ReadQuestionForRequest(LlmRequest request) {
    if (agentQuestions?.ReadPresented(session.Id, request.RequestId) is not { } question)
      return (false, null);
    if (request.Context is not { PreviousCompaction: not null } history
        || history.Messages.Any(message => message.AgentQuestionId == question.QuestionId))
      return (true, null);

    string? title = store.Sessions.ReadAgentTitle(question.SenderId).Title;
    return (true, new(ChatRole.User, "[CURRENT QUESTION REMINDER]\n" + AgentQuestionContext.Describe(title, question.Prompt)));
  }

  // The driver calls these under the session lock at the actual wait/wake transitions.
  internal void ParkBackgroundWait(Guid executionId) => agentQuestions?.Park(session.Id, executionId);
  internal void UnparkBackgroundWait(Guid executionId) => agentQuestions?.Unpark(session.Id, executionId);

  // Caller holds the session lock. Abort releases question waiters before joining owned work.
  internal void FailPresentedQuestion() => agentQuestions?.FailPresented(session.Id);

  // Caller holds the session lock. A settlement that ends without presenting the queue head, such
  // as a manual compaction, must not leave the recipient idle with an authorized waking ask
  // pending: run it now, FIFO, or settle it with a clear failure instead of hanging the asker.
  internal TaskCompletionSource? StartPendingQuestionRun() {
    if (!session.CanStartWork || agentQuestions is null)
      return null;
    QueuedQuestion[] waking = [.. agentQuestions.Queued(session.Id).Where(question => question.Wake)];
    if (waking.Length == 0)
      return null;
    try {
      return StartQuestionRun(agentQuestions);
    } catch (Exception failure) {
      foreach (QueuedQuestion question in waking)
        agentQuestions.Fail(session.Id, question.QuestionId, failure.Message);
      return null;
    }
  }

  // The session gate permanently fences new asks before this releases existing waiters.
  // Caller holds the recipient session lock, which also guards question admission.
  internal bool HasPending => agentQuestions?.Queued(session.Id).Count > 0;

  internal void ForgetRecipient() => agentQuestions?.ForgetRecipient(session.Id);

  // Caller holds the session lock. A failed conversation rejects undelivered waking asks
  // instead of starting another run. A bounded child still closes its entire mailbox.
  internal void FinishExecution(bool failedConversation) {
    if (failedConversation)
      agentQuestions?.FailUndeliveredWaking(session.Id);
    if (session.Kind == SessionKind.Subagent)
      agentQuestions?.CloseRecipient(session.Id);
    else
      FailPresentedQuestion();
  }
}
