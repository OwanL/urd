using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Urd.Core.Storage;
using Urd.Core.Context;
using Urd.Core.Execution;
using Urd.Core.Harness.Mcp;
using Urd.Core.Providers;
using Urd.Core.Preferences;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Creates, discovers and opens sessions, keeping one live instance per opened session ID.
// Bounded subagent children are created here too: they are registered under the shutdown fence
// and unregistered again once their owning run settles and their disposable resources are gone.
public sealed partial class SessionManager : IAsyncDisposable, ISubagentRunner, IAgentQuestions, IUserQuestions {
  public ValueTask DisposeAsync() => new(ShutdownSessionsAsync());
  private readonly UrdStore store;
  private readonly Func<ProviderConnection, LlmApi?, IChatClient> createChatClient;
  private readonly AgentToolSettings? toolSettings;
  private readonly PreferenceManager preferences;
  private readonly ModelSelector? modelSelector;
  private readonly string? fallbackWorkingDirectory;
  private readonly GitBash? bash;
  private readonly FileMutationQueue files = new();
  private readonly WebBrowser browser;
  private readonly AgentQuestions agentQuestions = new();
  private readonly ReadOnlyCollection<AgentTool> injectedTools;
  private readonly SessionRegistry registry = new();
  private readonly SessionToolCatalog toolCatalog;
  private readonly BrowserViewerAncestry browserViewers;
  private readonly SubagentRuns subagentRuns;
  private readonly AgentRecipients recipients;
  internal BrowserSetupProgress BrowserProgress { get; }
  internal BrowserHost? BrowserHost { get; private set; }

  // Configure before publishing session routes or creating sessions.
  public void ConfigureBrowser(string dataRoot) => ConfigureBrowser(dataRoot, bitwardenExtensionPath: null);

  internal void ConfigureBrowser(string dataRoot, string? bitwardenExtensionPath) {
    if (BrowserHost is not null)
      throw new InvalidOperationException("Browser is already configured.");
    BrowserHost = new BrowserHost(dataRoot, sessionId => HasPendingUserQuestionAsync(sessionId), bitwardenExtensionPath);
    BrowserHost.Changed += OnBrowserChanged;
  }

  private void OnBrowserChanged(Guid owner) {
    if (browserViewers.OwningPrimary(owner) is { } primary)
      Notify(BrowserChanged, primary);
  }

  // Registry ancestry identifies live candidates; only persisted ancestry grants viewer access.
  internal Task<IReadOnlyList<Guid>?> BrowserViewerOwnersAsync(Guid primaryId, CancellationToken cancellation = default) =>
      browserViewers.OwnersAsync(primaryId, cancellation);

  internal Task<bool> CanOpenBrowserVaultAsync(Guid primaryId, CancellationToken cancellation = default) =>
      registry.RunOpenAsync(() => store.Sessions.Read(primaryId) is { Kind: SessionKind.Primary } record &&
          !(record.SnoozedUntil > DateTimeOffset.UtcNow), cancellation);

  public Guid BootId { get; } = Guid.NewGuid();

  public event Action<Guid>? SessionCreated;
  public event Action<Guid>? SessionClosed;
  internal event Action<Guid>? BrowserChanged;

  // Notices are host-wide, ephemeral facts about the shared browser's first-use install.
#pragma warning disable S3264 // BrowserSetupProgress invokes the subscribed callbacks.
  public event Action<BrowserProvisioningNotice>? BrowserProvisioningChanged;
#pragma warning restore S3264

  public SessionManager(UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      AgentToolSettings? toolSettings = null, PreferenceManager? preferences = null, ModelSelector? modelSelector = null,
      IRemoteMcpTools? remoteMcpTools = null)
      : this(store, IgnoreModelApi(createChatClient), toolSettings, preferences, modelSelector, remoteMcpTools) { }

  internal SessionManager(UrdStore store, Func<ProviderConnection, LlmApi?, IChatClient> createChatClient,
      AgentToolSettings? toolSettings = null, PreferenceManager? preferences = null, ModelSelector? modelSelector = null,
      IRemoteMcpTools? remoteMcpTools = null)
      : this(store, createChatClient, toolSettings, preferences, modelSelector, remoteMcpTools, [],
          Path.GetPathRoot(Environment.CurrentDirectory)) { }

  // Focused execution tests supply Urd-owned tools without composing Windows processes.
  internal SessionManager(UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      IReadOnlyList<AgentTool> tools, string workingDirectory, ModelSelector? modelSelector = null)
      : this(store, IgnoreModelApi(createChatClient), tools, workingDirectory, modelSelector) { }

  internal SessionManager(UrdStore store, Func<ProviderConnection, LlmApi?, IChatClient> createChatClient,
      IReadOnlyList<AgentTool> tools, string workingDirectory, ModelSelector? modelSelector = null)
      : this(store, createChatClient, null, null, modelSelector, null, tools, Path.GetFullPath(workingDirectory)) { }

  private static Func<ProviderConnection, LlmApi?, IChatClient> IgnoreModelApi(Func<ProviderConnection, IChatClient> createChatClient) {
    ArgumentNullException.ThrowIfNull(createChatClient);
    return (connection, _) => createChatClient(connection);
  }

  private SessionManager(UrdStore store, Func<ProviderConnection, LlmApi?, IChatClient> createChatClient,
      AgentToolSettings? toolSettings, PreferenceManager? preferences, ModelSelector? modelSelector,
      IRemoteMcpTools? remoteMcpTools, IReadOnlyList<AgentTool> injectedTools, string? fallbackWorkingDirectory) {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(createChatClient);
    this.store = store;
    this.createChatClient = createChatClient;
    BrowserProgress = new BrowserSetupProgress(() => BrowserProvisioningChanged);
    browser = new WebBrowser(progress: BrowserProgress);
    this.preferences = preferences ?? new PreferenceManager(store);
    this.modelSelector = modelSelector;
    RemoteMcpTools = remoteMcpTools;
    this.injectedTools = Array.AsReadOnly(injectedTools.ToArray());
    this.fallbackWorkingDirectory = fallbackWorkingDirectory;
    browserViewers = new BrowserViewerAncestry(store, registry);
    subagentRuns = new SubagentRuns(store, this.preferences, modelSelector, agentQuestions, registry, CreateChild, CloseBrowserOwnerAsync);
    recipients = new AgentRecipients(store, agentQuestions, registry);
    if (toolSettings is not null) {
      if (!OperatingSystem.IsWindows())
        throw new PlatformNotSupportedException("Agent tools currently require Windows and Git for Windows.");
      this.toolSettings = toolSettings with {
        Environment = new ReadOnlyDictionary<string, string>(toolSettings.Environment
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase))
      };
      bash = new GitBash(toolSettings.GitBashPath);
    }
    toolCatalog = new SessionToolCatalog(this.toolSettings, bash, files, browser, () => BrowserHost,
        this.injectedTools, this);
  }

  public async Task<PrimarySession> CreatePrimaryAsync() {
    PrimarySession session = await registry.RunOpenAsync(() => {
      // Session defaults are captured once; later preference changes apply to new sessions.
      UserPreferences defaults = preferences.Read(fallbackWorkingDirectory);
      string? workingDirectory = toolCatalog.ProvidesWorkingDirectory ? defaults.WorkingDirectory : null;
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

    (SessionCloseStatus result, IReadOnlyList<Guid>? deletedSessions) =
        await session.CloseIfIdleAsync(registry.ThrowIfShuttingDown, cancellation);
    if (result == SessionCloseStatus.Closed) {
      foreach (Guid deletedSession in deletedSessions!)
        Notify(SessionClosed, deletedSession);
      // Keep ownership until cleanup succeeds, so racing shutdown can join it too.
      try {
        await Task.WhenAll(session.KillProcessesAsync(), CloseBrowserOwnerAsync(sessionId));
        session.CompleteCloseCleanup(null);
      } catch (Exception failure) {
        session.CompleteCloseCleanup(failure);
        throw;
      }
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
    await JoinAsync(Task.WhenAll(sessions.Select(session => session.JoinSnoozeCleanupAsync())));
    await JoinAsync(Task.WhenAll(sessions.Select(session => session.JoinCloseCleanupAsync())));
    // Ordinary Stop preserves earlier survivors; backend shutdown kills them only after work drains.
    await JoinAsync(Task.WhenAll(sessions.Select(session => session.KillProcessesAsync())));
    await JoinAsync(browser.DisposeAsync().AsTask());
    if (BrowserHost is { } browserHost)
      await JoinAsync(browserHost.DisposeAsync().AsTask());
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
    (UserPreferences defaults, ToolCatalog catalog) = await registry.RunOpenAsync(() => {
      UserPreferences defaults = preferences.Read(fallbackWorkingDirectory);
      ToolCatalog catalog = toolCatalog.Create(SessionKind.Primary, new PendingToolCalls(), true, out processes);
      return (defaults, catalog);
    }, cancellation);
    // Catalog construction starts no processes; still dispose its empty shell owner.
    await using var catalogProcesses = processes;
    var (skills, instructions) = await store.InstructionDiscovery.FindMetadataAsync(
        defaults.WorkingDirectory!, defaults.SourceIgnores, cancellation);
    return new(catalog.Tools.Select(tool => new StartingResourceOption(tool.Name, tool.Name)).ToArray(),
        skills.Select(skill => new StartingResourceOption(skill.Identity, skill.Name)).ToArray(),
        instructions.Select(path => new StartingResourceOption(path, path)).ToArray());
  }

  private PrimarySession CreateSession(SessionRecord record) {
    var pending = new PendingToolCalls();
    ToolCatalog catalog = toolCatalog.Create(SessionKind.Primary, pending, record.WorkingDirectory is not null, out ChatProcesses? processes);
    return new PrimarySession(record, store, createChatClient, catalog, processes, pending, preferences, modelSelector, this);
  }

  private SubagentSession CreateChild(SessionRecord record, int depth) {
    var pending = new PendingToolCalls();
    ToolCatalog catalog = toolCatalog.Create(SessionKind.Subagent, pending, record.WorkingDirectory is not null, out ChatProcesses? processes);
    return new SubagentSession(record, store, createChatClient, catalog, processes, pending, preferences, modelSelector, this, depth);
  }

  Task<SubagentRunResult> ISubagentRunner.RunSubagentAsync(HarnessToolContext caller, SubagentRunRequest request,
      CancellationToken cancellation) => subagentRuns.RunSubagentAsync(caller, request, cancellation);

  Task<AgentQuestionResult> IAgentQuestions.AskAgentAsync(HarnessToolContext caller, string recipient,
      string prompt, bool wake, CancellationToken cancellation) => recipients.AskAgentAsync(caller, recipient, prompt, wake, cancellation);

  bool IAgentQuestions.AnswerAgent(HarnessToolContext caller, string answer) => recipients.AnswerAgent(caller, answer);

  Task<string> IUserQuestions.AskUserAsync(HarnessToolContext caller, string question, string? context,
      IReadOnlyList<string> answers, CancellationToken cancellation) =>
      AskUserAsync(caller, question, context, answers, cancellation);

  internal Task<bool> IsSubagentLiveAsync(Guid sessionId) => registry.IsChildAsync(sessionId);
  private Task CloseBrowserOwnerAsync(Guid ownerId) => BrowserHost?.CloseOwnerAsync(ownerId) ?? Task.CompletedTask;

  // Live-handle seam for hosting-free tests: injects children without starting execution.
  internal SessionRegistry Registry => registry;

  internal AgentQuestions Questions => agentQuestions;
  internal IRemoteMcpTools? RemoteMcpTools { get; }

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
