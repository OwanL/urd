using Urd.Core.Tools;

namespace Urd.Core.Sessions;

public sealed partial class PrimarySession {
  // The primary's session lock owns publication and read/answer admission. Each waiter
  // belongs to its invoking execution, which may be a descendant of this session.
  private readonly Dictionary<Guid, (PendingUserQuestion Question, TaskCompletionSource<string> Reply)> userQuestions = new();

  public Task<PendingUserQuestion[]> ReadUserQuestionsAsync(CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => userQuestions.Values.Where(pending => !pending.Reply.Task.IsCompleted)
          .Select(pending => pending.Question).ToArray(), cancellation);

  internal Task<bool> HasPendingUserQuestionAsync(Guid askingSessionId, CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => userQuestions.Values.Any(pending => pending.Question.AskingSessionId == askingSessionId
          && !pending.Reply.Task.IsCompleted), cancellation);

  public Task<bool> AnswerUserQuestionAsync(Guid questionId, string answer, CancellationToken cancellation = default) {
    AskUserTool.ReadRequiredText(answer, "Answer");
    return ReadUnderLockAsync(() => {
      if (!userQuestions.TryGetValue(questionId, out var pending) || !pending.Reply.TrySetResult(answer))
        return false;

      RemoveUserQuestion(questionId);
      return true;
    }, cancellation);
  }

  internal async Task<string> AskUserAsync(PendingUserQuestion question, CancellationToken cancellation) {
    var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subscribed = cancellation.Register(() => reply.TrySetCanceled(cancellation));
    await ReadUnderLockAsync(() => {
      cancellation.ThrowIfCancellationRequested();
      userQuestions.Add(question.Id, (question, reply));
      PublishChange();
      return true;
    }, cancellation);
    try {
      return await reply.Task;
    } finally {
      await ReadUnderLockAsync(() => RemoveUserQuestion(question.Id), CancellationToken.None);
    }
  }

  private bool RemoveUserQuestion(Guid id) {
    if (!userQuestions.Remove(id))
      return false;

    PublishChange();
    return true;
  }
}
