using Urd.Core.Concurrency;

namespace Urd.Core.Sessions;

// Owns live session handles and serializes admission against shutdown.
internal sealed class SessionRegistry {
  private readonly AsyncLock sessionsLock = new();
  private readonly Dictionary<Guid, PrimarySession> sessions = new();
  private readonly Dictionary<Guid, SubagentSession> children = new();
  private TaskCompletionSource? shutdown;

  internal async Task<T> RunOpenAsync<T>(Func<T> work, CancellationToken cancellation = default) {
    using var held = await sessionsLock.AcquireAsync(cancellation);
    ThrowIfShuttingDown();
    return work();
  }

  internal PrimarySession? FindPrimary(Guid id) => sessions.GetValueOrDefault(id);
  internal bool HasChild(Guid id) => children.ContainsKey(id);
  internal PrimarySession TrackPrimary(PrimarySession session) {
    sessions.Add(session.Id, session);
    return session;
  }
  internal void TrackChild(SubagentSession child) => children.Add(child.Id, child);

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
    return [.. sessions.Values, .. children.Values];
  }

  internal async Task<HashSet<Guid>> LiveIdsAsync(CancellationToken cancellation = default) {
    using var held = await sessionsLock.AcquireAsync(cancellation);
    return [.. sessions.Keys, .. children.Keys];
  }

  internal async Task<bool> IsLiveAsync(Guid id, CancellationToken cancellation = default) {
    using var held = await sessionsLock.AcquireAsync(cancellation);
    return sessions.ContainsKey(id) || children.ContainsKey(id);
  }

  internal Task<SessionBase?> FindOpenAsync(Guid id, CancellationToken cancellation = default) =>
      RunOpenAsync<SessionBase?>(() => sessions.TryGetValue(id, out PrimarySession? primary)
          ? primary : children.GetValueOrDefault(id), cancellation);

  internal async Task<(TaskCompletionSource Shutdown, SessionBase[]? Handles)> BeginShutdownAsync(AgentQuestions questions) {
    using var held = await sessionsLock.AcquireAsync(CancellationToken.None);
    SessionBase[]? handles = null;
    if (shutdown is null) {
      questions.CloseAll();
      handles = [.. sessions.Values, .. children.Values];
      shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    return (shutdown, handles);
  }

  private void ThrowIfShuttingDown() {
    if (shutdown is not null)
      throw new InvalidOperationException("Session shutdown has started for this manager.");
  }
}
