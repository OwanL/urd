using Urd.Core.Tools;

namespace Urd.Core.Sessions;

public sealed partial class SessionManager {
  // Only the exact open primary can have pending questions. This does not open inactive sessions.
  public async Task<bool> HasPendingUserQuestionAsync(Guid sessionId, CancellationToken cancellation = default) {
    PrimarySession? primary = await registry.RunOpenAsync(() => FindOpenPrimary(sessionId), cancellation);
    return primary is not null && await primary.HasPendingUserQuestionAsync(cancellation);
  }

  internal async Task<string> AskUserAsync(HarnessToolContext caller, string question, string? context,
      IReadOnlyList<string> answers, CancellationToken cancellation) {
    PrimarySession primary = await registry.RunOpenAsync(() => FindOpenPrimary(caller.SessionId)
        ?? throw new InvalidOperationException("Only an open primary session can use ask_user. Subagents must use ask_agent."),
        cancellation);
    return await primary.AskUserAsync(question, context, answers, caller.Background, cancellation);
  }

  // Caller holds the registry lock.
  private PrimarySession? FindOpenPrimary(Guid sessionId) =>
      registry.FindPrimary(sessionId) is { IsClosed: false } primary ? primary : null;
}
