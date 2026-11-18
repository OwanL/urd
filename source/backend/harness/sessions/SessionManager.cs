using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Urd.Core.Storage;
using Urd.Core.Context;
using Urd.Core.Concurrency;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Preferences;
using Urd.Core.Tools;

namespace Urd.Core.Sessions;

// Creates, discovers and opens sessions, keeping one live instance per opened session ID.
// Bounded subagent children are created here too: they are registered under the shutdown fence
// and unregistered again once their owning run settles and their disposable resources are gone.
public sealed class SessionManager : IAsyncDisposable, ISubagentRunner, IAgentQuestions {
  public ValueTask DisposeAsync() => new(ShutdownSessionsAsync());
  private readonly UrdStore store;
  private readonly Func<ProviderConnection, IChatClient> createChatClient;
  private readonly AgentToolSettings? toolSettings;
  private readonly PreferenceManager preferences;
  private readonly ModelSelector? modelSelector;
  private readonly string? fallbackWorkingDirectory;
  private readonly GitBash? bash;
  private readonly FileMutationQueue files = new();
  private bool browserProvisioningActive;
  private readonly WebBrowser browser;
  private readonly AgentQuestions agentQuestions = new();
  private readonly ReadOnlyCollection<AgentTool> injectedTools = new([]);
  private readonly AsyncLock sessionsLock = new();
  private readonly Dictionary<Guid, PrimarySession> sessions = new();
  private readonly Dictionary<Guid, SubagentSession> children = new();
  private TaskCompletionSource? sessionShutdown;

  public Guid BootId { get; } = Guid.NewGuid();

  public event Action<Guid>? SessionCreated;

  // Notices are host-wide, ephemeral facts about the shared browser's first-use install.
  public event Action<BrowserProvisioningNotice>? BrowserProvisioningChanged;

  public SessionManager(UrdStore store, Func<ProviderConnection, IChatClient> createChatClient,
      AgentToolSettings? toolSettings = null, PreferenceManager? preferences = null, ModelSelector? modelSelector = null) {
    ArgumentNullException.ThrowIfNull(store);
    ArgumentNullException.ThrowIfNull(createChatClient);
    this.store = store;
    this.createChatClient = createChatClient;
    browser = new WebBrowser(progress: new BrowserSetupProgress(OnBrowserProgress));
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
      IReadOnlyList<AgentTool> tools, string workingDirectory, ModelSelector? modelSelector = null)
      : this(store, createChatClient, (AgentToolSettings?)null, null, modelSelector) {
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

    if (children.ContainsKey(sessionId))
      throw new InvalidOperationException($"Session '{sessionId}' is a subagent session, not a primary session.");

    SessionRecord record = store.Sessions.Read(sessionId)
        ?? throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
    if (record.Kind != SessionKind.Primary)
      throw new InvalidOperationException($"Session '{sessionId}' is a subagent session, not a primary session.");
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

  // Capture open handles only; saved primaries that have never been opened are not live here.
  // Read each session outside the registry lock and recheck children after those reads.
  async Task<IReadOnlyList<LiveAgent>> ISubagentRunner.ListLiveAgentsAsync(CancellationToken cancellation) {
    SessionBase[] handles;
    using (await sessionsLock.AcquireAsync(cancellation))
      handles = [.. sessions.Values, .. children.Values];

    var observations = new List<(Guid Id, Guid? ParentId, string? Title, bool Generating, string State)>();
    foreach (SessionBase session in handles) {
      string? status = null;
      using IDisposable subscription = await session.SubscribeAsync(change => status ??= change.Status, watched: false, cancellation);
      SessionSnapshot snapshot = await session.ReadSnapshotAsync(cancellation);
      (string? title, bool generating) = store.Sessions.ReadAgentTitle(session.Id);
      SessionRecord record = store.Sessions.Read(session.Id)!;
      string state = status switch {
        "waiting" => "waiting for background task",
        "running" or "compacting" => "active",
        _ => snapshot.UnsettledExecutionId is null ? "idle" : "active"
      };
      observations.Add((session.Id, record.ParentSessionId, title, generating, state));
    }

    using (await sessionsLock.AcquireAsync(cancellation))
      observations.RemoveAll(agent => !sessions.ContainsKey(agent.Id) && !children.ContainsKey(agent.Id));

    IReadOnlyDictionary<Guid, AgentTitle> titles = AgentTitles.ForListing(
        observations.Select(agent => (agent.Id, agent.Title, agent.Generating)).ToArray());
    return observations.Select(agent => new LiveAgent(titles[agent.Id], agent.State,
        agent.ParentId is { } parent && titles.TryGetValue(parent, out AgentTitle? parentTitle) ? parentTitle : null)).ToArray();
  }

  // Fences new work and stops this manager's primary sessions and children, waiting for saved
  // settlement. Cancellation abandons only this wait. The caller still owns the store and client.
  public async Task ShutdownSessionsAsync(CancellationToken waitCancellation = default) {
    TaskCompletionSource shutdown;
    SessionBase[]? sessionsToStop = null;
    using (await sessionsLock.AcquireAsync(CancellationToken.None)) {
      if (sessionShutdown is null) {
        // Nothing survives shutdown: every waiting ask fails before its caller is stopped.
        agentQuestions.CloseAll();
        sessionsToStop = [.. sessions.Values, .. children.Values];
        sessionShutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
      }

      shutdown = sessionShutdown;
    }

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
      new ListPendingCallsTool(pending), new CancelPendingCallsTool(pending), new SubagentTool(), new ListAgentsTool()];
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

  private void ThrowIfShuttingDown() {
    if (sessionShutdown is not null)
      throw new InvalidOperationException("Session shutdown has started for this manager.");
  }

  private PrimarySession Track(PrimarySession session) {
    sessions.Add(session.Id, session);
    return session;
  }

  private SubagentSession TrackChild(SubagentSession child) {
    children.Add(child.Id, child);
    return child;
  }

  // Runs one bounded child to its final report. The child is registered under the shutdown fence
  // before any of its work runs, cancellation stops and joins it, and its processes and registry
  // entry are released only after the run finishes. Cross-session waits hold no manager locks.
  async Task<SubagentRunResult> ISubagentRunner.RunSubagentAsync(HarnessToolContext caller, SubagentRunRequest request,
      CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(caller);
    ArgumentNullException.ThrowIfNull(request);
    SubagentAssignment assignment = await PrepareAssignmentAsync(caller, request, cancellation);

    SubagentSession? child = null;
    try {
      child = await CreateSubagentAsync(assignment, cancellation);
      return await RunChildAsync(child, assignment, cancellation);
    } finally {
      if (child is not null) {
        await CleanupChildAsync(child);
      }
    }
  }

  private async Task CleanupChildAsync(SubagentSession child) {
    try {
      await child.KillProcessesAsync();
    } catch (Exception failure) {
      throw new ToolCleanupException("Subagent process cleanup failed.", failure);
    } finally {
      agentQuestions.CloseRecipient(child.Id);
      await UnregisterSubagentAsync(child.Id);
    }
  }

  // Reads the parent identity, inherits its working directory, resolves the bucket chat and
  // captures the chosen parent context. A misdirected caller context fails here, not in the child.
  private async Task<SubagentAssignment> PrepareAssignmentAsync(HarnessToolContext caller,
      SubagentRunRequest request, CancellationToken cancellation) {
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Task);
    if (modelSelector is null)
      throw new InvalidOperationException("Subagent buckets require a configured model selector.");

    UserPreferences captured = preferences.Read();
    LlmSelection selection = await modelSelector.ResolveAsync(request.Bucket, captured, cancellation);
    SessionRecord parent = store.Sessions.Read(caller.SessionId)
        ?? throw new InvalidOperationException($"The calling session '{caller.SessionId}' does not exist.");
    string? contextBlock = ReadParentContext(parent, caller, request, captured.CompactionTriggerTokenCount);
    return new(parent.Id, caller.CallId, parent.WorkingDirectory, request.Task, contextBlock, selection);
  }

  private string? ReadParentContext(SessionRecord parent, HarnessToolContext caller, SubagentRunRequest request,
      int? parentCompactionTriggerTokens) {
    if (request.Context == SubagentContextMode.None)
      return null;

    // Only Recent budgets need the parent's own chat selection; stored facts are enough here.
    LlmSelection? parentSelection = request.Context == SubagentContextMode.Recent
        ? store.Snapshots.ReadRunSelection(parent.Id, caller.ExecutionId) : null;
    return store.Subagents.Read(new ExecutionIdentity(parent.Id, caller.ExecutionId, parent.HistoryRevision),
        caller.RequestId, caller.CallId, request.Context, parentSelection?.ContextWindow, parentCompactionTriggerTokens);
  }

  private async Task<SubagentSession> CreateSubagentAsync(SubagentAssignment assignment, CancellationToken cancellation) {
    using (await sessionsLock.AcquireAsync(cancellation)) {
      ThrowIfShuttingDown();
      // Children are deliberately absent from SessionCreated: presentation is deferred and the
      // primary tab list never includes them.
      return TrackChild(CreateChild(store.Sessions.CreateSubagent(Guid.NewGuid(),
          assignment.ParentSessionId, assignment.OwningCallId, assignment.WorkingDirectory)));
    }
  }

  private async Task<SubagentRunResult> RunChildAsync(SubagentSession child, SubagentAssignment assignment,
      CancellationToken cancellation) {
    bool owned = true;
    try {
      Guid executionId;
      try {
        executionId = await child.StartAssignmentAsync(assignment.Task, assignment.ContextBlock, assignment.Selection);
      } catch (Exception) when (cancellation.IsCancellationRequested) {
        // Creation saw the cancellation after registration: stop the owned child before it runs.
        owned = false;
        await StopChildAsync(child);
        return Error(child.Id, CancelledReport);
      }

      try {
        SessionExecutionResult settled = await child.WaitForExecutionFinishAsync(executionId, CancellationToken.None)
            .WaitAsync(cancellation);
        SubagentRunResult result = ReadReport(child.Id, settled);
        owned = false;
        return result;
      } catch (OperationCanceledException) {
        // Cancellation stops the child and joins its settlement before the report is lost with it.
        owned = false;
        await StopChildAsync(child);
        return Error(child.Id, CancelledReport);
      }
    } finally {
      if (owned) {
        await StopChildAsync(child);
      }
    }
  }

  private static async Task StopChildAsync(SubagentSession child) {
    try {
      await child.StopExecutionAsync();
    } catch (Exception failure) {
      throw new ToolCleanupException("Subagent stop cleanup failed.", failure);
    }
  }

  private SubagentRunResult ReadReport(Guid childSessionId, SessionExecutionResult settled) {
    switch (settled.Outcome) {
      case ExecutionOutcome.Completed:
        string report = store.Executions.ReadFinalReport(childSessionId, settled.ExecutionId);
        return string.IsNullOrWhiteSpace(report)
            ? Error(childSessionId, "The subagent task produced no report.")
            : new(childSessionId, report, Error: null);
      case ExecutionOutcome.Cancelled:
        return Error(childSessionId, CancelledReport);
      default:
        return Error(childSessionId, settled.Error is { } failure
            ? $"The subagent task failed: {failure}" : "The subagent task failed.");
    }
  }

  private static SubagentRunResult Error(Guid childSessionId, string message) =>
      new(childSessionId, Report: null, Error: message);

  // Removed children are finished or already fenced; their transcripts stay inspectable.
  private async Task UnregisterSubagentAsync(Guid childSessionId) {
    using (await sessionsLock.AcquireAsync())
      children.Remove(childSessionId);
  }

  // Test observation of live child ownership without opening a removed handle.
  internal async Task<bool> IsSubagentLiveAsync(Guid sessionId) {
    using (await sessionsLock.AcquireAsync())
      return children.ContainsKey(sessionId);
  }

  // Test observation of the question broker without opening a session handle.
  internal AgentQuestions Questions => agentQuestions;

  // Resolves the recipient once, pins its internal identity and never resolves again. The ask
  // waits on its broker record, which settles exactly once for every terminal outcome.
  async Task<AgentQuestionResult> IAgentQuestions.AskAgentAsync(HarnessToolContext caller, string recipient,
      string prompt, bool wake, CancellationToken cancellation) {
    ArgumentNullException.ThrowIfNull(caller);
    ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
    ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

    (Guid? recipientId, string? error) = recipient == ParentAlias
        ? await ResolveParentAsync(caller.SessionId, cancellation)
        : await ResolveTitleAsync(recipient, cancellation);
    if (recipientId is not { } pinned)
      return new(null, error);

    Task<AgentQuestionResult>? waiting;
    using (await sessionsLock.AcquireAsync(cancellation)) {
      ThrowIfShuttingDown();
      // The pinned identity is rechecked here so a disposal that raced resolution fences enqueue.
      waiting = sessions.ContainsKey(pinned) || children.ContainsKey(pinned)
          ? agentQuestions.Enqueue(pinned, caller.SessionId, caller.RequestId, caller.CallId, prompt, wake, cancellation)
          : null;
    }

    if (waiting is null)
      return new(null, FinishedMessage(recipient));
    return await waiting;
  }

  bool IAgentQuestions.AnswerAgent(HarnessToolContext caller, string answer) {
    ArgumentNullException.ThrowIfNull(caller);
    ArgumentException.ThrowIfNullOrWhiteSpace(answer);
    return agentQuestions.AnswerCurrent(caller.SessionId, answer);
  }

  private async Task<(Guid? RecipientId, string? Error)> ResolveTitleAsync(string recipient, CancellationToken cancellation) {
    SessionBase[] handles;
    using (await sessionsLock.AcquireAsync(cancellation))
      handles = [.. sessions.Values, .. children.Values];

    // The same live-handle snapshot as list_agents, so addressing agrees with discovery.
    List<(Guid Id, string? Title, bool Generating)> titled = [.. handles.Select(session => {
      (string? title, bool generating) = store.Sessions.ReadAgentTitle(session.Id);
      return (session.Id, title, generating);
    })];

    RecipientLookup lookup = AgentAddresses.Match(AgentTitles.ForListing(titled), recipient);
    if (lookup.SessionId is { } matched)
      return (matched, null);
    if (lookup.Ambiguous)
      return (null, $"The title '{recipient}' matches multiple live agents; address a different title.");
    if (lookup.PlaceholderMatch)
      return (null, PlaceholderMessage(recipient));

    // A finished child keeps its immutable final title in storage and is recognized here.
    IReadOnlyList<Guid> finished = store.Sessions.ReadSubagentIdsByTitle(recipient);
    return (null, finished.Count > 0 ? FinishedMessage(recipient) : UnknownMessage(recipient));
  }

  private async Task<(Guid? RecipientId, string? Error)> ResolveParentAsync(Guid callerId, CancellationToken cancellation) {
    SessionRecord? caller = store.Sessions.Read(callerId);
    if (caller?.ParentSessionId is not { } parentId)
      return (null, "The calling agent has no parent.");

    bool live;
    using (await sessionsLock.AcquireAsync(cancellation))
      live = sessions.ContainsKey(parentId) || children.ContainsKey(parentId);
    if (!live)
      return (null, "The parent agent has finished and cannot answer questions.");

    if (store.Sessions.ReadAgentTitle(parentId).Title is null)
      return (null, "The parent agent has no final title yet; wait a few seconds and try again.");
    return (parentId, null);
  }

  private const string ParentAlias = "parent";

  private static string PlaceholderMessage(string recipient) =>
      $"The agent '{recipient}' has no final title yet; wait a few seconds and try again.";

  private static string FinishedMessage(string recipient) =>
      $"The agent '{recipient}' has finished and cannot answer questions.";

  private static string UnknownMessage(string recipient) =>
      $"No live agent has the title '{recipient}'; use list_agents for current titles.";

  // Final result message for runs the parent cannot observe a report from.
  private const string CancelledReport = "The subagent task was cancelled.";

  // One prepared bounded child: ownership, inherited directory and its whole assignment.
  private sealed record SubagentAssignment(Guid ParentSessionId, string OwningCallId, string? WorkingDirectory,
      string Task, string? ContextBlock, LlmSelection Selection);

  // Translates raw install reports into provisioning notices, suppressing terminal reports
  // that follow an unrelated launch failure while no download was surfaced.
  internal void OnBrowserProgress(string report) {
    if (report == WebBrowserInstall.DownloadStarted)
      browserProvisioningActive = true;
    else if (!browserProvisioningActive)
      return;
    else if (report is WebBrowserInstall.DownloadFinished or WebBrowserInstall.SetupFailed)
      browserProvisioningActive = false;

    BrowserProvisioningStage stage = report switch {
      WebBrowserInstall.DownloadStarted => BrowserProvisioningStage.Downloading,
      WebBrowserInstall.DownloadFinished => BrowserProvisioningStage.Ready,
      _ => BrowserProvisioningStage.Failed
    };
    NotifyBrowserProvisioning(
        new BrowserProvisioningNotice(stage, stage == BrowserProvisioningStage.Downloading ? report : null));
  }

  private void NotifyBrowserProvisioning(BrowserProvisioningNotice notice) {
    Action<BrowserProvisioningNotice>? callbacks = BrowserProvisioningChanged;
    if (callbacks is null)
      return;

    foreach (Delegate callback in callbacks.GetInvocationList()) {
      try {
        ((Action<BrowserProvisioningNotice>)callback)(notice);
      } catch (Exception) {
        // Provisioning is observable only; subscriber failures are ignored.
      }
    }
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
