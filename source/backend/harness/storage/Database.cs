using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Serializes access to the connection and owns transaction lifetimes.
internal sealed class Database(SqliteConnection connection) : IDisposable {
    private readonly object gate = new();
    private bool disposed;

    internal T Write<T>(Func<SqliteTransaction, T> operation)
        => Transact(operation, deferred: false);

    internal T Read<T>(Func<SqliteTransaction, T> operation)
        => Transact(operation, deferred: true);

    // Runs the operation under the gate, refusing work after disposal.
    internal T Guarded<T>(Func<T> operation) {
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
        foreach ((string name, object? value) in parameters) {
            command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value ?? DBNull.Value);
        }

        return command;
    }

    public void Dispose() {
        lock (gate) {
            if (disposed)
                return;

            connection.Dispose();
            disposed = true;
        }
    }

    private T Transact<T>(Func<SqliteTransaction, T> operation, bool deferred) {
        return Guarded(() => {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred);
            T result = operation(transaction);
            transaction.Commit();
            return result;
        });
    }
}
