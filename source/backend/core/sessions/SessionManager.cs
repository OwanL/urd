using Microsoft.Extensions.AI;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

// Creates, discovers and opens sessions, keeping one live instance per opened session ID.
public sealed class SessionManager {
  private readonly UrdStore store;
  private readonly IChatClient chatClient;
  private readonly IReadOnlyList<AIFunction> tools;
  private readonly AsyncLock gate = new();
  private readonly Dictionary<Guid, PrimarySession> sessions = new();
  private TaskCompletionSource? sessionShutdown;

  public SessionManager(UrdStore store, IChatClient chatClient, IReadOnlyList<AIFunction>? tools = null) {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(chatClient);
    AIFunction[] configured = tools?.ToArray() ?? [];
    if (configured.Any(tool => tool is null || string.IsNullOrWhiteSpace(tool.Name))
        || configured.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != configured.Length)
      throw new ArgumentException("Tools require distinct nonempty names.", nameof(tools));

    this.store = store;
    this.chatClient = chatClient;
    this.tools = Array.AsReadOnly(configured);
  }

  public async Task<PrimarySession> CreatePrimaryAsync() {
    using var held = await gate.AcquireAsync();
    ThrowIfShuttingDown();
    SessionRecord record = store.Sessions.CreatePrimary(Guid.NewGuid());
    return Track(new PrimarySession(record, store, chatClient, tools));
  }

  public async Task<PrimarySession> GetPrimaryAsync(Guid sessionId) {
    using var held = await gate.AcquireAsync();
    ThrowIfShuttingDown();
    if (sessions.TryGetValue(sessionId, out PrimarySession? cached))
      return cached;

    SessionRecord record = store.Sessions.Read(sessionId)
        ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
    return Track(new PrimarySession(record, store, chatClient, tools));
  }

  // Discovers saved identities without opening handles or starting execution.
  public async Task<SessionIdPage> ListPrimaryIdsAsync(
      string? cursor = null, CancellationToken cancellationToken = default) {
    using var held = await gate.AcquireAsync(cancellationToken);
    ThrowIfShuttingDown();
    cancellationToken.ThrowIfCancellationRequested();
    return store.Sessions.ListPrimaryIds(cursor);
  }

  // Fences new work and stops this manager's live sessions, waiting for saved settlement.
  // Cancellation abandons only this wait. The caller still owns the store and client.
  public async Task ShutdownSessionsAsync(CancellationToken waitCancellation = default) {
    TaskCompletionSource shutdown;
    PrimarySession[]? sessionsToStop = null;
    using (await gate.AcquireAsync(CancellationToken.None)) {
      if (sessionShutdown is null) {
        sessionsToStop = sessions.Values.ToArray();
        sessionShutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
      }

      shutdown = sessionShutdown;
    }

    // Start outside the registry lock: cancellation callbacks may reenter the manager.
    if (sessionsToStop is not null)
      _ = StopSessionsAsync(sessionsToStop, shutdown);

    await shutdown.Task.WaitAsync(waitCancellation);
  }

  private static async Task StopSessionsAsync(PrimarySession[] sessions, TaskCompletionSource shutdown) {
    Task pending = Task.CompletedTask;
    try {
      // Fence every handle before a Stop callback can submit a user message to another session.
      pending = Task.WhenAll(sessions.Select(session => session.FenceUserMessagesAsync()));
      await pending;
      pending = Task.WhenAll(sessions.Select(session => session.StopExecutionAsync()));
      await pending;
      shutdown.TrySetResult();
    } catch (Exception failure) {
      shutdown.TrySetException(pending.Exception ?? failure);
      _ = shutdown.Task.Exception; // Observed even if every caller cancelled its wait.
    }
  }

  private void ThrowIfShuttingDown() {
    if (sessionShutdown is not null)
      throw new InvalidOperationException("Session shutdown has started for this manager.");
  }

  private PrimarySession Track(PrimarySession session) {
    sessions.Add(session.Id, session);
    return session;
  }
}
