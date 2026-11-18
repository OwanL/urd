using Microsoft.Data.Sqlite;
using Urd.Core.Usage;
using Urd.Core.Context;
using Urd.Core.MarkdownRendering;

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
