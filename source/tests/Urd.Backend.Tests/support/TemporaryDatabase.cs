using Microsoft.Data.Sqlite;

namespace Urd.Backend.Tests.Support;

// Isolated SQLite file in a temp directory, with raw SQL helpers for test setup and checks.
internal sealed class TemporaryDatabase : IDisposable {
  private readonly string directory;

  internal TemporaryDatabase(string directoryPrefix = "urd-identity-tests") {
    directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{directoryPrefix}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    Path = System.IO.Path.Combine(directory, "urd.sqlite");
  }

  internal string Path { get; }

  internal void Execute(string sql) {
    using SqliteConnection connection = OpenConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
  }

  internal object? Scalar(string sql) {
    using SqliteConnection connection = OpenConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar();
  }

  internal List<T> ReadRows<T>(string sql, Func<SqliteDataReader, T> map) {
    using SqliteConnection connection = OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = sql;
    using SqliteDataReader reader = command.ExecuteReader();
    var rows = new List<T>();
    while (reader.Read())
      rows.Add(map(reader));
    return rows;
  }

  internal string[] ExplainQueryPlan(string sql, params (string Name, object Value)[] parameters) {
    using SqliteConnection connection = OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "EXPLAIN QUERY PLAN " + sql;
    foreach ((string name, object value) in parameters)
      command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value);
    using SqliteDataReader reader = command.ExecuteReader();
    var steps = new List<string>();
    while (reader.Read())
      steps.Add(reader.GetString(3));
    return [.. steps];
  }

  // Opens a read-only connection for the shared Stored* evidence readers.
  internal SqliteConnection OpenReadOnlyConnection() => OpenConnection(SqliteOpenMode.ReadOnly);

  // Simulates history advanced by a peer store, for stale-revision fencing tests.
  internal void ForceHistoryRevision(long revision) =>
      Execute($"UPDATE sessions SET history_revision = {revision};");

  public void Dispose() => Directory.Delete(directory, recursive: true);

  private SqliteConnection OpenConnection(SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) {
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
      DataSource = Path,
      Pooling = false,
      Mode = mode
    }.ToString());
    try {
      connection.Open();
      return connection;
    } catch {
      connection.Dispose();
      throw;
    }
  }
}
