namespace Urd.Core.Sessions;

// Live only. The resulting tool output is recorded by execution, not by the browser command.
public sealed record PendingUserQuestion(Guid Id, Guid AskingSessionId, string Question, string? Context,
    IReadOnlyList<string> Answers, string? SourceTitle, bool Background);
