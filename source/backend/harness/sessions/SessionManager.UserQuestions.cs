using Urd.Core.Tools;

namespace Urd.Core.Sessions;

public sealed partial class SessionManager {
  internal async Task<string> AskUserAsync(HarnessToolContext caller, string question, string? context,
      IReadOnlyList<string> answers, CancellationToken cancellation) {
    PrimarySession primary = await registry.RunOpenAsync(() => {
      Guid id = caller.SessionId;
      while (registry.FindChild(id) is { } child)
        id = child.ParentSessionId;
      return registry.FindPrimary(id) ?? throw new InvalidOperationException("The asking agent has no open primary session.");
    }, cancellation);
    string? sourceTitle = caller.SessionId == primary.Id ? null
        : store.Sessions.ReadAgentTitle(caller.SessionId).Title ?? "Subagent";
    var pending = new PendingUserQuestion(Guid.NewGuid(), question, context, answers, sourceTitle, caller.Background);
    return await primary.AskUserAsync(pending, cancellation);
  }
}
