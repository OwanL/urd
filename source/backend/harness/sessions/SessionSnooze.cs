namespace Urd.Core.Sessions;

public sealed record SnoozedSession(Guid SessionId, DateTimeOffset Until, string? Title);

public sealed record SnoozedSessions(DateTimeOffset Now, IReadOnlyList<SnoozedSession> Sessions);

public enum SessionSnoozeStatus {
  Changed,
  Unchanged,
  Conflict
}
