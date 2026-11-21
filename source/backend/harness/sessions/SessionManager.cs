using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Urd.Core.Storage;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Preferences;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Creates, discovers and opens sessions, keeping one live instance per opened session ID.
// Bounded subagent children are created here too: they are registered under the shutdown fence
// and unregistered again once their owning run settles and their disposable resources are gone.
public sealed partial class SessionManager : IAsyncDisposable, ISubagentRunner, IAgentQuestions {
  public ValueTask DisposeAsync() => new(ShutdownSessionsAsync());
  private readonly UrdStore store;
  private readonly Func<ProviderConnection, IChatClient> createChatClient;
  private readonly AgentToolSettings? toolSettings;
  private readonly PreferenceManager preferences;
  private readonly ModelSelector? modelSelector;
  private readonly string? fallbackWorkingDirectory;
  private readonly GitBash? bash;
  private readonly FileMutationQueue files = new();
  private readonly WebBrowser browser;
  private readonly AgentQuestions agentQuestions = new();
  private readonly ReadOnlyCollection<AgentTool> injectedTools = new([]);
  private readonly SessionRegistry registry = new();
  private readonly SubagentRuns subagentRuns;
  private readonly AgentRecipients recipients;
  internal BrowserSetupProgress BrowserProgress { get; }

  public Guid BootId { get; } = Guid.NewGuid();

  public event Action<Guid>? SessionCreated;
  public event Action<Guid>? SessionClosed;

  // Notices are host-wide, ephemeral facts about the shared browser's first-use install.
#pragma warning disable S3264 // BrowserSetupProgress invokes the subscribed callbacks.
  public event Action<BrowserProvisioningNotice>? BrowserProvisioningChanged;
#pragma warning restore S3264

  public SessionManager(UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      AgentToolSettings? toolSettings = null, PreferenceManager? preferences = null, ModelSelector? modelSelector = null) {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(createChatClient);
    this.store = store;
    this.createChatClient = createChatClient;
    BrowserProgress = new BrowserSetupProgress(() => BrowserProvisioningChanged);
    browser = new WebBrowser(progress: BrowserProgress);
    this.preferences = preferences ?? new PreferenceManager(store);
    this.modelSelector = modelSelector;
    subagentRuns = new SubagentRuns(store, this.preferences, modelSelector, agentQuestions, registry, CreateChild);
    recipients = new AgentRecipients(store, agentQuestions, registry);
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
      IReadOnlyList<AgentTool> tools, string workingDirectory, ModelSelector? modelSelector = null)
      : this(store, createChatClient, (AgentToolSettings?)null, null, modelSelector) {
    injectedTools = Array.AsReadOnly(tools.ToArray());
    fallbackWorkingDirectory = Path.GetFullPath(workingDirectory);
  }

  public async Task<PrimarySession> CreatePrimaryAsync() {
    PrimarySession session = await registry.RunOpenAsync(() => {
      // Session defaults are captured once; later preference changes apply to new sessions.
      UserPreferences defaults = preferences.Read(fallbackWorkingDirectory);
      string? workingDirectory = toolSettings is null && injectedTools.Count == 0 ? null : defaults.WorkingDirectory;
      SessionRecord record = store.Sessions.CreatePrimary(Guid.NewGuid(), workingDirectory, defaults.SubagentProviderConnectionIds);
      return registry.TrackPrimary(CreateSession(record));
    });
    Notify(SessionCreated, session.Id);
    return session;
  }

  public Task<PrimarySession> GetPrimaryAsync(Guid sessionId) => registry.RunOpenAsync(() =>
      FindPrimary(sessionId, rejectChild: true)
          ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist."));

  // Cancellation applies only before close is admitted; then Core owns deletion and cleanup.
  public async Task<SessionCloseStatus> ClosePrimaryAsync(Guid sessionId, CancellationToken cancellation = default) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    PrimarySession? session = await registry.RunOpenAsync(() => FindPrimary(sessionId, rejectChild: false), cancellation);
    if (session is null)
      return SessionCloseStatus.NotFound;

    SessionCloseStatus result = await session.CloseIfIdleAsync(registry.ThrowIfShuttingDown, cancellation);
    if (result == SessionCloseStatus.Closed) {
      Notify(SessionClosed, session.Id);
      // Keep ownership until process cleanup succeeds, so racing shutdown can join it too.
      await session.KillProcessesAsync();
      await registry.UntrackPrimaryAsync(sessionId);
    }
    return result;
  }

  // Caller holds the registry lock. A closed handle can still be retained for process cleanup.
  private PrimarySession? FindPrimary(Guid sessionId, bool rejectChild) {
    if (registry.FindPrimary(sessionId) is { } cached)
      return cached.IsClosed ? null : cached;

    SessionRecord? record = store.Sessions.Read(sessionId);
    if (record is null)
      return null;
    if (record.Kind == SessionKind.Primary)
      return registry.TrackPrimary(CreateSession(record));
    if (rejectChild)
      throw new InvalidOperationException($"Session '{sessionId}' is a subagent session, not a primary session.");
    return null;
  }

  public async Task<IReadOnlyList<Guid>?> SetSubagentProvidersAsync(Guid sessionId, IReadOnlyList<Guid>? connectionIds,
      CancellationToken cancellation = default) {
    Guid[]? normalized = PreferenceManager.NormalizeSubagentProviders(connectionIds);
    return await registry.RunOpenAsync<IReadOnlyList<Guid>?>(() => {
      store.Sessions.SetSubagentProviders(sessionId, normalized);
      return normalized;
    }, cancellation);
  }

  // Discovers saved identities without opening handles or starting execution.
  public Task<SessionIdPage> ListPrimaryIdsAsync(string? cursor = null, CancellationToken cancellationToken = default) =>
      registry.RunOpenAsync(() => {
        cancellationToken.ThrowIfCancellationRequested();
        return store.Sessions.ListPrimaryIds(cursor);
      }, cancellationToken);

  Task<IReadOnlyList<LiveAgent>> ISubagentRunner.ListLiveAgentsAsync(CancellationToken cancellation) =>
      recipients.ListLiveAgentsAsync(cancellation);

  // Fences new work and stops this manager's primary sessions and children, waiting for saved
  // settlement. Cancellation abandons only this wait. The caller still owns the store and client.
  public async Task ShutdownSessionsAsync(CancellationToken waitCancellation = default) {
    (TaskCompletionSource shutdown, SessionBase[]? sessionsToStop) = await registry.BeginShutdownAsync(agentQuestions);

    // Start outside the registry lock: cancellation callbacks may reenter the manager.
    if (sessionsToStop is not null)
      _ = StopSessionsAsync(sessionsToStop, shutdown);

    await shutdown.Task.WaitAsync(waitCancellation);
  }

  private async Task StopSessionsAsync(SessionBase[] sessions, TaskCompletionSource shutdown) {
    var failures = new List<Exception>();
    // Fence every handle before a Stop callback can submit a user message to another session.
    await JoinAsync(Task.WhenAll(sessions.Select(session => session.StopAcceptingUserMessagesAsync())));
    await JoinAsync(Task.WhenAll(sessions.Select(session => session.StopExecutionAsync())));
    // Ordinary Stop preserves earlier survivors; backend shutdown kills them only after work drains.
    await JoinAsync(Task.WhenAll(sessions.Select(session => session.KillProcessesAsync())));
    await JoinAsync(browser.DisposeAsync().AsTask());
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
    ChatProcesses? processes = null;
    (string directory, ToolCatalog catalog) = await registry.RunOpenAsync(() => {
      string directory = preferences.Read(fallbackWorkingDirectory).WorkingDirectory!;
      ToolCatalog catalog = CreateToolCatalog(new PendingToolCalls(), true, out processes);
      return (directory, catalog);
    }, cancellation);
    // Catalog construction starts no processes; still dispose its empty shell owner.
    await using var catalogProcesses = processes;
    var (skills, instructions) = await store.InstructionDiscovery.FindMetadataAsync(directory, cancellation);
    return new(catalog.Tools.Select(tool => new StartingResourceOption(tool.Name, tool.Name)).ToArray(),
        skills.Select(skill => new StartingResourceOption(skill.Identity, skill.Name)).ToArray(),
        instructions.Select(path => new StartingResourceOption(path, path)).ToArray());
  }

  private PrimarySession CreateSession(SessionRecord record) {
    var pending = new PendingToolCalls();
    ToolCatalog catalog = CreateToolCatalog(pending, record.WorkingDirectory is not null, out ChatProcesses? processes);
    return new PrimarySession(record, store, createChatClient, catalog, processes, pending, preferences, modelSelector, this);
  }

  private SubagentSession CreateChild(SessionRecord record) {
    var pending = new PendingToolCalls();
    ToolCatalog catalog = CreateToolCatalog(pending, record.WorkingDirectory is not null, out ChatProcesses? processes);
    return new SubagentSession(record, store, createChatClient, catalog, processes, pending, preferences, modelSelector, this);
  }

  private ToolCatalog CreateToolCatalog(PendingToolCalls pending, bool processTools, out ChatProcesses? processes) {
    processes = null;
    AgentTool[] shared = [new WebSearchTool(browser), new WebReadTool(browser),
      new ListPendingCallsTool(pending), new CancelPendingCallsTool(pending), new SubagentTool(), new ListAgentsTool(),
      new AskAgentTool(), new AnswerAgentTool(), new AskUserTool(this)];
    if (toolSettings is null || !processTools)
      return new([.. injectedTools, .. shared]);
    if (!OperatingSystem.IsWindows())
      throw new PlatformNotSupportedException("Agent tools currently require Windows and Git for Windows.");
    processes = new ChatProcesses(bash!);
    return new([
      new ReadTool(files), new WriteTool(files), new EditTool(files),
      new BashTool(processes, bash!, toolSettings.Environment), .. shared
    ]);
  }

  Task<SubagentRunResult> ISubagentRunner.RunSubagentAsync(HarnessToolContext caller, SubagentRunRequest request,
      CancellationToken cancellation) => subagentRuns.RunSubagentAsync(caller, request, cancellation);

  Task<AgentQuestionResult> IAgentQuestions.AskAgentAsync(HarnessToolContext caller, string recipient,
      string prompt, bool wake, CancellationToken cancellation) => recipients.AskAgentAsync(caller, recipient, prompt, wake, cancellation);

  bool IAgentQuestions.AnswerAgent(HarnessToolContext caller, string answer) => recipients.AnswerAgent(caller, answer);

  internal Task<bool> IsSubagentLiveAsync(Guid sessionId) => registry.IsChildAsync(sessionId);
  internal AgentQuestions Questions => agentQuestions;

  // The lifecycle transition is durable before subscribers see it.
  private static void Notify(Action<Guid>? callbacks, Guid sessionId) {
    if (callbacks is null)
      return;

    foreach (Delegate callback in callbacks.GetInvocationList()) {
      try {
        ((Action<Guid>)callback)(sessionId);
      } catch (Exception) {
        // Subscriber failures cannot undo an already committed change.
      }
    }
  }
}
