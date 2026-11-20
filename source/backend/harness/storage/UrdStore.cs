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
  private readonly InstructionResourceCatalog instructionCatalog;
  private readonly CancellationTokenSource instructionWarmStop = new();
  private Task? instructionWarm;
  private int disposed;

  private UrdStore(SqliteConnection connection, string databasePath) {
    database = new(connection);
    instructionCatalog = new(databasePath + ".instructions.sqlite", new FdResourceScanner());
    InstructionDiscovery = new(instructionCatalog, InstructionResourceDiscovery.PackagedDirectory);
    var toolResults = new ToolResultCodec(new ToolImageStore(databasePath + ".tool-images"));
    Sessions = new(database);
    StartingContexts = new(database);
    Prepass = new(database);
    Compactions = new(database, toolResults);
    UserMessages = new(database);
    Continuations = new(database, toolResults);
    MessageEdits = new(database, UserMessages);
    LlmResponses = new(database);
    MarkdownImageFiles = new(database, databasePath + ".markdown-images");
    MarkdownImageImports = new(MarkdownImageFiles);
    AuxiliaryConversations = new(database, toolResults);
    Executions = new(database, toolResults, AuxiliaryConversations, UserMessages);
    Tools = new(database, toolResults);
    Content = new(database);
    Subagents = new(database, toolResults);
    PrepassDetails = new(database);
    Snapshots = new(database, Content, PrepassDetails);
    History = new(database, PrepassDetails);
    Positions = new(database, History);
    NotSentInputs = new(database);
    Providers = new(database);
    ModelSettings = new(database);
    Preferences = new(database);
    Usage = new(database);
  }

  internal InstructionResourceDiscovery InstructionDiscovery { get; }
  internal UsageStorage Usage { get; }
  internal ProviderStorage Providers { get; }
  internal ModelSettingsStorage ModelSettings { get; }
  internal PreferencesStorage Preferences { get; }
  internal SessionStorage Sessions { get; }
  internal StartingContextStorage StartingContexts { get; }
  internal PrepassStorage Prepass { get; }
  internal CompactionStorage Compactions { get; }
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
    MarkdownImageFiles.RemoveUnreferencedFiles();
  }

  public bool IsSetupComplete() => database.Read(transaction =>
      database.Scalar<long>(transaction, "SELECT EXISTS(SELECT 1 FROM setup_completion WHERE singleton = 1);") != 0);

  public SetupStatus ReadSetupStatus() => Preferences.WithCurrent(preferences =>
      database.Read(transaction => ReadSetupStatus(transaction, preferences)));

  // An incomplete result leaves setup untouched; completion remains valid once recorded.
  public SetupStatus CompleteSetup() => Preferences.WithCurrent(preferences => database.Write(transaction => {
    SetupStatus status = ReadSetupStatus(transaction, preferences);
    if (status.Completed || !status.HasProvider || !status.HasModelBuckets || !status.HasWorkingDirectory)
      return status;
    database.Execute(transaction, "INSERT INTO setup_completion (singleton) VALUES (1) ON CONFLICT DO NOTHING;");
    return status with { Completed = true };
  }));

  private SetupStatus ReadSetupStatus(SqliteTransaction transaction, UserPreferences preferences) {
    bool completed = database.Scalar<long>(transaction,
        "SELECT EXISTS(SELECT 1 FROM setup_completion WHERE singleton = 1);") != 0;
    AuxiliaryModelPreferences models = preferences.ModelChoices;
    DateTimeOffset freshAfter = DateTimeOffset.UtcNow.AddMinutes(1);
    var available = Providers.ListWithCatalog(transaction)
        .Where(state => state.Catalog is { Models.Count: > 0 }
            && ProviderManager.CanUsePersistedState(state, freshAfter))
        .ToDictionary(state => state.Connection.Id,
            state => state.Catalog!.Models.Select(model => model.Id).ToHashSet(StringComparer.Ordinal));
    bool hasProvider = available.Count > 0;
    bool HasAssignment(IReadOnlyList<ConfiguredModel> assignments) => assignments.Any(model =>
        available.TryGetValue(model.ConnectionId, out var ids) && ids.Contains(model.ModelId));
    bool hasBuckets = HasAssignment(models.Small) && HasAssignment(models.Medium) && HasAssignment(models.Large);
    string? directory = preferences.WorkingDirectory;
    bool hasDirectory = !string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory)
        && Directory.Exists(directory);
    return new(completed, hasProvider, hasBuckets, hasDirectory);
  }

  public UsageSummary ReadUsage(Guid? sessionId = null, DateTimeOffset? from = null,
      DateTimeOffset? to = null, bool detail = true) => Usage.Read(sessionId, from, to, detail);

  // Host startup opts in; ordinary stores and test fixtures never initiate a broad scan.
  public void WarmInstructionResources(string directory) {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    if (instructionWarm is not null)
      throw new InvalidOperationException("Instruction resource warming has already started.");
    instructionWarm = Task.Run(async () => {
      try {
        await instructionCatalog.FindPathsAsync(directory, instructionWarmStop.Token);
      } catch (Exception) {
        // A later consumer retries and observes any failure; warming is best effort.
      }
    }, CancellationToken.None);
  }

  public void Dispose() {
    if (Interlocked.Exchange(ref disposed, 1) != 0)
      return;
    instructionWarmStop.Cancel();
    instructionCatalog.DisposeAsync().AsTask().GetAwaiter().GetResult();
    instructionWarm?.GetAwaiter().GetResult();
    instructionWarmStop.Dispose();
    MarkdownImageImports.Dispose();
    database.Dispose();
  }
}
