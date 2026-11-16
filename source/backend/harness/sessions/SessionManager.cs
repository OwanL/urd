using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Urd.Core.Storage;
using Urd.Core.Context;
using Urd.Core.Concurrency;
using Urd.Core.Providers;
using Urd.Core.Preferences;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Creates, discovers and opens sessions, keeping one live instance per opened session ID.
public sealed class SessionManager {
  private readonly UrdStore store;
  private readonly Func<ProviderConnection, IChatClient> createChatClient;
  private readonly AgentToolSettings? toolSettings;
  private readonly PreferenceManager preferences;
  private readonly ModelSelector? modelSelector;
  private readonly string? fallbackWorkingDirectory;
  private readonly GitBash? bash;
  private readonly FileMutationQueue files = new();
  private readonly ReadOnlyCollection<AgentTool> injectedTools = new([]);
  private readonly AsyncLock sessionsLock = new();
  private readonly Dictionary<Guid, PrimarySession> sessions = new();
  private TaskCompletionSource? sessionShutdown;

  public Guid BootId { get; } = Guid.NewGuid();

  public event Action<Guid>? SessionCreated;

  public SessionManager(UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      AgentToolSettings? toolSettings = null, PreferenceManager? preferences = null, ModelSelector? modelSelector = null) {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(createChatClient);
    this.store = store;
    this.createChatClient = createChatClient;
    this.preferences = preferences ?? new PreferenceManager(store);
    this.modelSelector = modelSelector;
    fallbackWorkingDirectory = Path.GetPathRoot(Environment.CurrentDirectory);
    if (toolSettings is null)
      return;
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Agent tools currently require Windows and Git for Windows.");
    this.toolSettings = toolSettings with {
      Environment = new ReadOnlyDictionary<string, string>(toolSettings.Environment
          .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase))
    };
    bash = new GitBash(toolSettings.GitBashPath);
  }

  // Focused execution tests supply Urd-owned tools without composing Windows processes.
  internal SessionManager(UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      IReadOnlyList<AgentTool> tools, string workingDirectory) : this(store, createChatClient) {
    injectedTools = Array.AsReadOnly(tools.ToArray());
    fallbackWorkingDirectory = Path.GetFullPath(workingDirectory);
  }

  public async Task<PrimarySession> CreatePrimaryAsync() {
    PrimarySession session;
    using (await sessionsLock.AcquireAsync()) {
      ThrowIfShuttingDown();
      // Changing the preference affects only chats created afterwards.
      string? workingDirectory = toolSettings is null && injectedTools.Count == 0
          ? null : preferences.Read(fallbackWorkingDirectory).WorkingDirectory;
      SessionRecord record = store.Sessions.CreatePrimary(Guid.NewGuid(), workingDirectory);
      session = Track(CreateSession(record));
    }

    NotifySessionCreated(session.Id);
    return session;
  }

  public async Task<PrimarySession> GetPrimaryAsync(Guid sessionId) {
    using var _ = await sessionsLock.AcquireAsync();
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
    using var _ = await sessionsLock.AcquireAsync(cancellationToken);
    ThrowIfShuttingDown();
    cancellationToken.ThrowIfCancellationRequested();
    return store.Sessions.ListPrimaryIds(cursor);
  }

  // Fences new work and stops this manager's live sessions, waiting for saved settlement.
  // Cancellation abandons only this wait. The caller still owns the store and client.
  public async Task ShutdownSessionsAsync(CancellationToken waitCancellation = default) {
    TaskCompletionSource shutdown;
    PrimarySession[]? sessionsToStop = null;
    using (await sessionsLock.AcquireAsync(CancellationToken.None)) {
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
    await JoinAsync(Task.WhenAll(sessions.Select(session => session.StopAcceptingUserMessagesAsync())));
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

  public async Task<StartingResourceCatalog> ReadStartingResourcesAsync(CancellationToken cancellation = default) {
    string directory;
    ToolCatalog catalog;
    ChatProcesses? processes;
    using (await sessionsLock.AcquireAsync(cancellation)) {
      ThrowIfShuttingDown();
      directory = preferences.Read(fallbackWorkingDirectory).WorkingDirectory!;
      catalog = CreateToolCatalog(new PendingToolCalls(), true, out processes);
    }
    // Catalog construction starts no processes; still dispose its empty shell owner.
    await using var catalogProcesses = processes;
    DiscoveredInstructionResources resources = await new InstructionResourceDiscovery().FindAsync(directory, cancellation);
    return new(catalog.Tools.Select(tool => new StartingResourceOption(tool.Name, tool.Name)).ToArray(),
        resources.Skills.Select(skill => new StartingResourceOption(skill.Identity, skill.Name)).ToArray(),
        resources.Instructions.Select(document => new StartingResourceOption(document.Identity, document.Path)).ToArray());
  }

  private PrimarySession CreateSession(SessionRecord record) {
    var pending = new PendingToolCalls();
    ToolCatalog catalog = CreateToolCatalog(pending, record.WorkingDirectory is not null, out ChatProcesses? processes);
    return new PrimarySession(record, store, createChatClient, catalog, processes, pending, preferences, modelSelector);
  }

  private ToolCatalog CreateToolCatalog(PendingToolCalls pending, bool processTools, out ChatProcesses? processes) {
    processes = null;
    AgentTool[] controls = [new ListPendingCallsTool(pending), new CancelPendingCallsTool(pending)];
    if (toolSettings is null || !processTools)
      return new(injectedTools.Count == 0 ? [] : [.. injectedTools, .. controls]);
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Agent tools currently require Windows and Git for Windows.");
    processes = new ChatProcesses(bash!);
    return new([
      new ReadTool(files), new WriteTool(files), new EditTool(files),
      new BashTool(processes, bash!, toolSettings.Environment), .. controls
    ]);
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
