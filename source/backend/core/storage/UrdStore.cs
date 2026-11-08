using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Opens the shared database and exposes its focused storage operations.
public sealed class UrdStore : IDisposable {
  private readonly Database database;

  private UrdStore(SqliteConnection connection) {
    database = new(connection);
    Sessions = new(database);
    UserMessages = new(database);
    Executions = new(database);
    LlmResponses = new(database);
    Tools = new(database);
    Content = new(database);
    Snapshots = new(database, Content);
    Providers = new(database);
  }

  internal ProviderStorage Providers { get; }
  internal SessionStorage Sessions { get; }
  internal UserMessageStorage UserMessages { get; }
  internal SessionExecutionStorage Executions { get; }
  internal LlmResponseStorage LlmResponses { get; }
  internal ToolStorage Tools { get; }
  internal SnapshotReader Snapshots { get; }
  internal MessageContentReader Content { get; }

  public static UrdStore Open(string databasePath) {
    ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
      DataSource = Path.GetFullPath(databasePath),
      Mode = SqliteOpenMode.ReadWriteCreate,
      Pooling = false
    }.ToString());

    try {
      connection.Open();
      DatabaseSchema.Initialize(connection);
      return new UrdStore(connection);
    } catch {
      connection.Dispose();
      throw;
    }
  }

  public void Dispose() => database.Dispose();
}
