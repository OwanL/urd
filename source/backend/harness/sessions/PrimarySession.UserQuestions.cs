using Urd.Core.Tools;

namespace Urd.Core.Sessions;

public sealed partial class PrimarySession {
  // The session lock owns publication and read/answer admission. Each waiter belongs to its invoking execution.
  private readonly Dictionary<Guid, (PendingUserQuestion Question, TaskCompletionSource<string> Reply)> userQuestions = new();

  protected override bool HasPendingUserQuestion => userQuestions.Count != 0;

  public Task<PendingUserQuestion[]> ReadUserQuestionsAsync(CancellationToken cancellation = default) =>
      ReadUnderLockAsync(() => userQuestions.Values.Where(pending => !pending.Reply.Task.IsCompleted)
          .Select(pending => pending.Question).ToArray(), cancellation);

  internal Task<bool> HasPendingUserQuestionAsync(CancellationToken cancellation) =>
      ReadUnderLockAsync(() => userQuestions.Values.Any(pending => !pending.Reply.Task.IsCompleted), cancellation);

  public Task<bool> AnswerUserQuestionAsync(Guid questionId, string answer, CancellationToken cancellation = default) {
    AskUserTool.ReadRequiredText(answer, "Answer");
    return ReadUnderLockAsync(() => {
      if (!userQuestions.TryGetValue(questionId, out var pending) || !pending.Reply.TrySetResult(answer))
        return false;

      RemoveUserQuestion(questionId);
      return true;
    }, cancellation);
  }

  internal async Task<string> AskUserAsync(string question, string? context, IReadOnlyList<string> answers,
      bool background, CancellationToken cancellation) {
    var pending = new PendingUserQuestion(Guid.NewGuid(), Id, question, context, answers, SourceTitle: null, Background: background);
    var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subscribed = cancellation.Register(() => reply.TrySetCanceled(cancellation));
    await ReadUnderLockAsync(() => {
      cancellation.ThrowIfCancellationRequested();
      userQuestions.Add(pending.Id, (pending, reply));
      PublishChange();
      return true;
    }, cancellation);
    try {
      return await reply.Task;
    } finally {
      await ReadUnderLockAsync(() => RemoveUserQuestion(pending.Id), CancellationToken.None);
    }
  }

  private bool RemoveUserQuestion(Guid id) {
    if (!userQuestions.Remove(id))
      return false;

    PublishChange();
    return true;
  }
}
