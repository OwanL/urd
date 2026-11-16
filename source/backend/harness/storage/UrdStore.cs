using Microsoft.Data.Sqlite;
using Urd.Core.Usage;

namespace Urd.Core.Storage;

// Opens the shared database and exposes its focused storage operations.
public sealed class UrdStore : IDisposable {
  private readonly Database database;

  private UrdStore(SqliteConnection connection, string databasePath) {
    database = new(connection);
    var toolResults = new ToolResultCodec(new ToolImageStore(databasePath + ".tool-images"));
    Sessions = new(database);
    StartingContexts = new(database);
    Compactions = new(database, toolResults);
    UserMessages = new(database);
    Continuations = new(database, toolResults);
    MessageEdits = new(database, UserMessages);
    LlmResponses = new(database);
    AuxiliaryConversations = new(database, toolResults);
    Executions = new(database, toolResults, AuxiliaryConversations);
    Tools = new(database, toolResults);
    Content = new(database);
    Snapshots = new(database, Content);
    History = new(database);
    Positions = new(database, History);
    NotSentInputs = new(database);
    Providers = new(database);
    ModelSettings = new(database);
    Preferences = new(database);
    Usage = new(database);
  }

  internal UsageStorage Usage { get; }
  internal ProviderStorage Providers { get; }
  internal ModelSettingsStorage ModelSettings { get; }
  internal PreferencesStorage Preferences { get; }
  internal SessionStorage Sessions { get; }
  internal StartingContextStorage StartingContexts { get; }
  internal CompactionStorage Compactions { get; }
  internal UserMessageStorage UserMessages { get; }
  internal ContinuationStorage Continuations { get; }
  internal MessageEditStorage MessageEdits { get; }
  internal SessionExecutionStorage Executions { get; }
  internal LlmResponseStorage LlmResponses { get; }
  internal AuxiliaryConversationStorage AuxiliaryConversations { get; }
  internal ToolStorage Tools { get; }
  internal SnapshotReader Snapshots { get; }
  internal MessageContentReader Content { get; }
  internal TranscriptHistoryReader History { get; }
  internal TranscriptPositionReader Positions { get; }
  internal NotSentInputReader NotSentInputs { get; }

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
  }

  public UsageSummary ReadUsage(Guid? sessionId = null, DateTimeOffset? from = null,
      DateTimeOffset? to = null, bool detail = true) => Usage.Read(sessionId, from, to, detail);

  public void Dispose() => database.Dispose();
}
