using Urd.Core.Tools;

namespace Urd.Core.Sessions;

public sealed partial class SessionManager {
  // A snapshot of pending questions from this exact live session, not its descendants.
  // Unknown, closed and retained-but-inactive session IDs do not open sessions and return false.
  public async Task<bool> HasPendingUserQuestionAsync(Guid sessionId, CancellationToken cancellation = default) {
    PrimarySession? primary = await registry.RunOpenAsync(() => FindAskingPrimary(sessionId), cancellation);
    return primary is not null && await primary.HasPendingUserQuestionAsync(sessionId, cancellation);
  }

  internal async Task<string> AskUserAsync(HarnessToolContext caller, string question, string? context,
      IReadOnlyList<string> answers, CancellationToken cancellation) {
    PrimarySession primary = await registry.RunOpenAsync(() => FindAskingPrimary(caller.SessionId)
        ?? throw new InvalidOperationException("The asking agent has no open primary session."), cancellation);
    string? sourceTitle = caller.SessionId == primary.Id ? null
        : store.Sessions.ReadAgentTitle(caller.SessionId).Title ?? "Subagent";
    var pending = new PendingUserQuestion(Guid.NewGuid(), caller.SessionId, question, context, answers, sourceTitle, caller.Background);
    return await primary.AskUserAsync(pending, cancellation);
  }

  // Caller holds the registry lock. Do not reopen an inactive session just to read live questions.
  private PrimarySession? FindAskingPrimary(Guid sessionId) {
    while (registry.FindChild(sessionId) is { } child)
      sessionId = child.ParentSessionId;
    return registry.FindPrimary(sessionId) is { IsClosed: false } primary ? primary : null;
  }
}
