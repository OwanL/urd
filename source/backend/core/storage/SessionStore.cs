using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Opens the session database and exposes its focused storage operations.
public sealed class SessionStore : IDisposable {
  private readonly Database database;

  private SessionStore(SqliteConnection connection) {
    database = new(connection);
    Sessions = new(database);
    UserMessages = new(database);
    Executions = new(database);
    LlmResponses = new(database);
    Tools = new(database);
    Content = new(database);
    Snapshots = new(database, Content);
  }

  internal SessionStorage Sessions { get; }
  internal UserMessageStorage UserMessages { get; }
  internal SessionExecutionStorage Executions { get; }
  internal LlmResponseStorage LlmResponses { get; }
  internal ToolStorage Tools { get; }
  internal SnapshotReader Snapshots { get; }
  internal MessageContentReader Content { get; }

  public static SessionStore Open(string databasePath) {
    ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
      DataSource = Path.GetFullPath(databasePath),
      Mode = SqliteOpenMode.ReadWriteCreate,
      Pooling = false
    }.ToString());

    try {
      connection.Open();
      DatabaseSchema.Initialize(connection);
      return new SessionStore(connection);
    } catch {
      connection.Dispose();
      throw;
    }
  }

  public void Dispose() => database.Dispose();
}
