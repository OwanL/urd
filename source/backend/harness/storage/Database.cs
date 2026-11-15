using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Serializes access to the connection and owns transaction lifetimes.
internal sealed class Database(SqliteConnection connection) : IDisposable {
  private readonly object gate = new();
  private bool disposed;

  internal T Write<T>(Func<SqliteTransaction, T> operation)
      => RunInTransaction(operation, deferred: false);

  internal T Read<T>(Func<SqliteTransaction, T> operation)
      => RunInTransaction(operation, deferred: true);

  // Runs the operation under the gate, refusing work after disposal.
  internal T RunLocked<T>(Func<T> operation) {
    lock (gate) {
      ObjectDisposedException.ThrowIf(disposed, this);
      return operation();
    }
  }

  internal SqliteCommand Command(
      SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) {
    SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    command.Parameters.AddRange(parameters.Select(parameter => new SqliteParameter(parameter.Name,
        parameter.Value is Guid id ? id.ToString("D") : parameter.Value ?? DBNull.Value)));

    return command;
  }

  internal int Execute(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) {
    using SqliteCommand command = Command(transaction, sql, parameters);
    return command.ExecuteNonQuery();
  }

  internal T Scalar<T>(SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) {
    using SqliteCommand command = Command(transaction, sql, parameters);
    object? value = command.ExecuteScalar();
    return value is null or DBNull ? default!
        : (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
  }

  internal List<T> Query<T>(SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> readRow,
      params (string Name, object? Value)[] parameters) {
    using SqliteCommand command = Command(transaction, sql, parameters);
    using SqliteDataReader reader = command.ExecuteReader();
    List<T> rows = [];
    while (reader.Read())
      rows.Add(readRow(reader));
    return rows;
  }

  internal T? QuerySingleOrDefault<T>(SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> readRow,
      params (string Name, object? Value)[] parameters) {
    using SqliteCommand command = Command(transaction, sql, parameters);
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Read() ? readRow(reader) : default;
  }

  public void Dispose() {
    lock (gate) {
      if (disposed)
        return;

      connection.Dispose();
      disposed = true;
    }
  }

  private T RunInTransaction<T>(Func<SqliteTransaction, T> operation, bool deferred) {
    return RunLocked(() => {
      using SqliteTransaction transaction = connection.BeginTransaction(deferred);
      T result = operation(transaction);
      transaction.Commit();
      return result;
    });
  }
}
