using Urd.Core.Concurrency;

namespace Urd.Core.Sessions;

// Owns live session handles and serializes admission against shutdown.
internal sealed class SessionRegistry {
  private readonly AsyncLock sessionsLock = new();
  private readonly Dictionary<Guid, PrimarySession> sessions = new();
  private readonly Dictionary<Guid, SubagentSession> children = new();
  private volatile TaskCompletionSource? shutdown;

  internal async Task<T> RunOpenAsync<T>(Func<T> work, CancellationToken cancellation = default) {
    using var held = await sessionsLock.AcquireAsync(cancellation);
    ThrowIfShuttingDown();
    return work();
  }

  internal PrimarySession? FindPrimary(Guid id) => sessions.GetValueOrDefault(id);
  internal PrimarySession TrackPrimary(PrimarySession session) {
    sessions.Add(session.Id, session);
    return session;
  }
  // Caller holds the registry lock.
  internal SubagentSession? FindChild(Guid id) => children.GetValueOrDefault(id);
  internal void TrackChild(SubagentSession child) => children.Add(child.Id, child);

  // Caller holds the registry lock. Do not follow branches through an unregistered parent.
  internal IReadOnlyList<Guid> LiveDescendantIds(Guid primaryId) {
    if (children.Count == 0)
      return [];

    var byParent = children.Values.ToLookup(child => child.ParentSessionId, child => child.Id);
    List<Guid> descendants = [.. byParent[primaryId]];
    for (int i = 0; i < descendants.Count; i++)
      descendants.AddRange(byParent[descendants[i]]);
    return descendants;
  }

  internal async Task UntrackPrimaryAsync(Guid id) {
    using var held = await sessionsLock.AcquireAsync();
    sessions.Remove(id);
  }

  internal async Task UntrackChildAsync(Guid id) {
    using var held = await sessionsLock.AcquireAsync();
    children.Remove(id);
  }

  internal async Task<bool> IsChildAsync(Guid id) {
    using var held = await sessionsLock.AcquireAsync();
    return children.ContainsKey(id);
  }

  internal async Task<SessionBase[]> SnapshotAsync(CancellationToken cancellation = default) {
    using var held = await sessionsLock.AcquireAsync(cancellation);
    return [.. sessions.Values.Where(session => !session.IsClosed), .. children.Values];
  }

  internal async Task<HashSet<Guid>> LiveIdsAsync(CancellationToken cancellation = default) {
    using var held = await sessionsLock.AcquireAsync(cancellation);
    return [.. sessions.Values.Where(session => !session.IsClosed).Select(session => session.Id), .. children.Keys];
  }

  internal async Task<bool> IsLiveAsync(Guid id, CancellationToken cancellation = default) {
    using var held = await sessionsLock.AcquireAsync(cancellation);
    return sessions.GetValueOrDefault(id) is { IsClosed: false } || children.ContainsKey(id);
  }

  internal Task<SessionBase?> FindOpenAsync(Guid id, CancellationToken cancellation = default) =>
      RunOpenAsync<SessionBase?>(() => sessions.GetValueOrDefault(id) is { IsClosed: false } primary
          ? primary : children.GetValueOrDefault(id), cancellation);

  internal async Task<(TaskCompletionSource Shutdown, SessionBase[]? Handles)> BeginShutdownAsync(AgentQuestions questions) {
    using var held = await sessionsLock.AcquireAsync(CancellationToken.None);
    SessionBase[]? handles = null;
    if (shutdown is null) {
      shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
      questions.CloseAll();
      handles = [.. sessions.Values, .. children.Values];
    }
    return (shutdown, handles);
  }

  internal void ThrowIfShuttingDown() {
    if (shutdown is not null)
      throw new InvalidOperationException("Session shutdown has started for this manager.");
  }
}
