using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Urd.Core.Storage;
using Urd.Core.Providers;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Creates, discovers and opens sessions, keeping one live instance per opened session ID.
public sealed class SessionManager {
  private readonly UrdStore store;
  private readonly Func<ProviderConnection, IChatClient> createChatClient;
  private readonly AgentToolSettings? toolSettings;
  private readonly string? workingDirectory;
  private readonly GitBash? bash;
  private readonly FileMutationQueue files = new();
  private readonly ReadOnlyCollection<AgentTool> injectedTools = new([]);
  private readonly AsyncLock gate = new();
  private readonly Dictionary<Guid, PrimarySession> sessions = new();
  private TaskCompletionSource? sessionShutdown;

  public Guid Boot { get; } = Guid.NewGuid();

  public event Action<Guid>? SessionCreated;

  public SessionManager(UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      AgentToolSettings? toolSettings = null) {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(createChatClient);
    this.store = store;
    this.createChatClient = createChatClient;
    if (toolSettings is null)
      return;
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Agent tools currently require Windows and Git for Windows.");

    workingDirectory = Path.GetFullPath(toolSettings.WorkingDirectory);
    this.toolSettings = toolSettings with {
      WorkingDirectory = workingDirectory,
      Environment = new ReadOnlyDictionary<string, string>(toolSettings.Environment
          .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase))
    };
    bash = new GitBash(toolSettings.GitBashPath);
  }

  // Focused execution tests supply Urd-owned tools without composing Windows processes.
  internal SessionManager(UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      IReadOnlyList<AgentTool> tools, string workingDirectory) : this(store, createChatClient) {
    injectedTools = Array.AsReadOnly(tools.ToArray());
    this.workingDirectory = Path.GetFullPath(workingDirectory);
  }

  public async Task<PrimarySession> CreatePrimaryAsync() {
    PrimarySession session;
    using (await gate.AcquireAsync()) {
      ThrowIfShuttingDown();
      SessionRecord record = store.Sessions.CreatePrimary(Guid.NewGuid(), workingDirectory);
      session = Track(CreateSession(record));
    }

    NotifySessionCreated(session.Id);
    return session;
  }

  public async Task<PrimarySession> GetPrimaryAsync(Guid sessionId) {
    using var held = await gate.AcquireAsync();
    ThrowIfShuttingDown();
    if (sessions.TryGetValue(sessionId, out PrimarySession? cached))
      return cached;

    SessionRecord record = store.Sessions.Read(sessionId)
        ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
    return Track(CreateSession(record));
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
    var failures = new List<Exception>();
    // Fence every handle before a Stop callback can submit a user message to another session.
    await JoinAsync(Task.WhenAll(sessions.Select(session => session.FenceUserMessagesAsync())));
    await JoinAsync(Task.WhenAll(sessions.Select(session => session.StopExecutionAsync())));
    // Ordinary Stop preserves earlier survivors; backend shutdown kills them only after work drains.
    await JoinAsync(Task.WhenAll(sessions.Select(session => session.KillProcessesAsync())));
    if (failures.Count == 0)
      shutdown.TrySetResult();
    else {
      shutdown.TrySetException(new AggregateException("Session shutdown failed.", failures));
      _ = shutdown.Task.Exception; // Observed even if every caller cancelled its wait.
    }

    async Task JoinAsync(Task pending) {
      try {
        await pending;
      } catch (Exception failure) {
        failures.Add(pending.Exception ?? failure);
      }
    }
  }

  private PrimarySession CreateSession(SessionRecord record) {
    var pending = new PendingToolCalls();
    AgentTool[] controls = [new ListPendingCallsTool(pending), new CancelPendingCallsTool(pending)];
    if (toolSettings is null) {
      var configured = new ToolCatalog(injectedTools.Count == 0 ? [] : [.. injectedTools, .. controls]);
      return new PrimarySession(record, store, createChatClient, configured, pending: pending);
    }
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Agent tools currently require Windows and Git for Windows.");
    if (record.WorkingDirectory is null)
      throw new InvalidOperationException("This chat has no saved working folder. Create a new chat to use agent tools.");

    var processes = new ChatProcesses(bash!);
    var tools = new ToolCatalog([
      new ReadTool(files), new WriteTool(files), new EditTool(files),
      new BashTool(processes, bash!, toolSettings.Environment), .. controls
    ]);
    return new PrimarySession(record, store, createChatClient, tools, processes, pending);
  }

  private void ThrowIfShuttingDown() {
    if (sessionShutdown is not null)
      throw new InvalidOperationException("Session shutdown has started for this manager.");
  }

  private PrimarySession Track(PrimarySession session) {
    sessions.Add(session.Id, session);
    return session;
  }

  // Subscribers should enqueue the notice without blocking; creation is already durable here.
  private void NotifySessionCreated(Guid sessionId) {
    Action<Guid>? callbacks = SessionCreated;
    if (callbacks is null)
      return;

    foreach (Delegate callback in callbacks.GetInvocationList()) {
      try {
        ((Action<Guid>)callback)(sessionId);
      } catch (Exception) {
        // Session creation is already durable; subscriber failures do not undo it.
      }
    }
  }
}
