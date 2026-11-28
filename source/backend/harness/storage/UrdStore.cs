using Microsoft.Data.Sqlite;
using Urd.Core.Usage;
using Urd.Core.Context;
using Urd.Core.MarkdownRendering;
using Urd.Core.Preferences;
using Urd.Core.Providers;

namespace Urd.Core.Storage;

// Opens the shared database and exposes its focused storage operations.
public sealed class UrdStore : IDisposable {
  private readonly Database database;
  private readonly SessionImageCleanup sessionImages;
  private readonly SessionClosureStorage sessionClosure;
  private readonly InstructionResourceCatalog instructionCatalog;
  private int disposed;

  private UrdStore(SqliteConnection connection, string databasePath) {
    database = new(connection);
    instructionCatalog = new(databasePath + ".instructions.sqlite", new FdResourceScanner());
    InstructionDiscovery = new(instructionCatalog, InstructionResourceDiscovery.PackagedDirectory);
    string toolImagesPath = databasePath + ".tool-images";
    string markdownImagesPath = databasePath + ".markdown-images";
    string userImagesPath = databasePath + ".user-images";
    sessionImages = new(database, toolImagesPath, markdownImagesPath, userImagesPath);
    UserImages = new(database, userImagesPath);
    sessionClosure = new(database, sessionImages);
    var toolResults = new ToolResultCodec(new ToolImageStore(toolImagesPath));
    Sessions = new(database);
    StartingContexts = new(database);
    Prepass = new(database);
    Compactions = new(database, toolResults);
    UserMessages = new(database);
    Continuations = new(database, toolResults);
    MessageEdits = new(database, UserMessages);
    LlmResponses = new(database);
    MarkdownImageFiles = new(database, markdownImagesPath);
    MarkdownImageImports = new(MarkdownImageFiles);
    AuxiliaryConversations = new(database, toolResults);
    Executions = new(database, toolResults, AuxiliaryConversations, UserMessages);
    Tools = new(database, toolResults);
    Content = new(database);
    Subagents = new(database);
    PrepassDetails = new(database);
    Snapshots = new(database, Content, PrepassDetails);
    History = new(database, PrepassDetails);
    Positions = new(database, History);
    NotSentInputs = new(database);
    Providers = new(database);
    McpServers = new(database);
    ModelSettings = new(database);
    Preferences = new(database);
    Usage = new(database);
  }

  internal InstructionResourceDiscovery InstructionDiscovery { get; }
  internal UsageStorage Usage { get; }
  internal ProviderStorage Providers { get; }
  public McpServerStorage McpServers { get; }
  internal ModelSettingsStorage ModelSettings { get; }
  internal PreferencesStorage Preferences { get; }
  internal SessionStorage Sessions { get; }
  internal StartingContextStorage StartingContexts { get; }
  internal PrepassStorage Prepass { get; }
  internal CompactionStorage Compactions { get; }
  internal UserImageStorage UserImages { get; }
  internal UserMessageStorage UserMessages { get; }
  internal ContinuationStorage Continuations { get; }
  internal MessageEditStorage MessageEdits { get; }
  internal SessionExecutionStorage Executions { get; }
  internal LlmResponseStorage LlmResponses { get; }
  internal MarkdownImageStorage MarkdownImageFiles { get; }
  internal MarkdownImageImports MarkdownImageImports { get; }
  internal AuxiliaryConversationStorage AuxiliaryConversations { get; }
  internal ToolStorage Tools { get; }
  internal SnapshotReader Snapshots { get; }
  internal MessageContentReader Content { get; }
  internal TranscriptHistoryReader History { get; }
  internal PrepassDetailsReader PrepassDetails { get; }
  internal TranscriptPositionReader Positions { get; }
  internal NotSentInputReader NotSentInputs { get; }
  internal SubagentContextReader Subagents { get; }

  public static UrdStore Open(string databasePath) {
    ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
    databasePath = Path.GetFullPath(databasePath);
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
      DataSource = databasePath,
      Mode = SqliteOpenMode.ReadWriteCreate,
      Pooling = false
    }.ToString());

    try {
      connection.Open();
      DatabaseSchema.Initialize(connection);
      return new UrdStore(connection, databasePath);
    } catch {
      connection.Dispose();
      throw;
    }
  }

  // The host must hold exclusive data-directory ownership and have no live sessions.
  // Recovery records uncertainty; it never sends a request or invokes a tool.
  public void RecoverInterruptedExecutions() {
    Usage.RecoverInterrupted();
    Executions.RecoverInterruptedExecutions();
    sessionImages.RemoveUnreferencedFiles();
  }

  // The manager must have fenced new work and verified cleanup of the owned execution tree.
  internal bool DeletePrimarySession(Guid sessionId, CancellationToken cancellationToken = default) =>
      DeletePrimarySessionTree(sessionId, drainedRun: null, cancellationToken) is not null;

  internal IReadOnlyList<Guid>? DeletePrimarySessionTree(Guid sessionId, Guid? drainedRun, CancellationToken cancellationToken) =>
      sessionClosure.DeletePrimary(sessionId, drainedRun, cancellationToken);

  public bool IsSetupComplete() => database.Read(transaction =>
      database.Scalar<long>(transaction, "SELECT EXISTS(SELECT 1 FROM setup_completion WHERE singleton = 1);") != 0);

  public SetupStatus ReadSetupStatus() => CheckSetup(complete: false, Directory.Exists);

  // An incomplete result leaves setup untouched; completion remains valid once recorded.
  public SetupStatus CompleteSetup() => CheckSetup(complete: true, Directory.Exists);

  internal SetupStatus CheckSetup(bool complete, Func<string, bool> directoryExists) {
    SetupStatus? status;
    do {
      string? directory = Preferences.WithCurrent(preferences => preferences.WorkingDirectory);
      bool hasDirectory = !string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory)
          && directoryExists(directory);
      status = Preferences.WithCurrent<SetupStatus?>(preferences => {
        // Use the current model choices, but only with a probe of their snapshot's directory.
        if (!string.Equals(preferences.WorkingDirectory, directory, StringComparison.Ordinal))
          return null;
        Func<SqliteTransaction, SetupStatus> check = transaction => CheckSetup(transaction, preferences, hasDirectory, complete);
        return complete ? database.Write(check) : database.Read(check);
      });
    } while (status is null);
    return status;
  }

  private SetupStatus CheckSetup(SqliteTransaction transaction, UserPreferences preferences, bool hasDirectory, bool complete) {
    bool completed = database.Scalar<long>(transaction,
        "SELECT EXISTS(SELECT 1 FROM setup_completion WHERE singleton = 1);") != 0;
    AuxiliaryModelPreferences models = preferences.ModelChoices;
    DateTimeOffset now = DateTimeOffset.UtcNow;
    var available = Providers.ListWithCatalog(transaction)
        .Where(state => state.Catalog is { Models.Count: > 0 }
            && ProviderManager.CanUsePersistedState(state, now))
        .ToDictionary(state => state.Connection.Id,
            state => state.Catalog!.Models.Select(model => model.Id).ToHashSet(StringComparer.Ordinal));
    bool hasProvider = available.Count > 0;
    bool HasAssignment(IReadOnlyList<ConfiguredModel> assignments) => assignments.Any(model =>
        available.TryGetValue(model.ConnectionId, out var ids) && ids.Contains(model.ModelId));
    bool hasBuckets = HasAssignment(models.Small) && HasAssignment(models.Medium) && HasAssignment(models.Large);
    if (complete && !completed && hasProvider && hasBuckets && hasDirectory) {
      database.Execute(transaction, "INSERT INTO setup_completion (singleton) VALUES (1) ON CONFLICT DO NOTHING;");
      completed = true;
    }
    return new(completed, hasProvider, hasBuckets, hasDirectory);
  }

  public UsageSummary ReadUsage(Guid? sessionId = null, DateTimeOffset? from = null,
      DateTimeOffset? to = null, bool detail = true) => Usage.Read(sessionId, from, to, detail);

  public PromptCacheSummary ReadPromptCache(Guid sessionId) => Usage.ReadPromptCache(sessionId);

  // Use only the saved choice, never the drive-root fallback shown in the folder picker.
  public void WarmInstructionResources() {
    if (Preferences.Read().WorkingDirectory is { } directory)
      instructionCatalog.Warm(directory);
  }

  public void Dispose() {
    if (Interlocked.Exchange(ref disposed, 1) != 0)
      return;
    instructionCatalog.DisposeAsync().AsTask().GetAwaiter().GetResult();
    MarkdownImageImports.Dispose();
    database.Dispose();
  }
}
