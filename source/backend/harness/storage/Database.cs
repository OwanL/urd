using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Serializes access to the connection and owns transaction lifetimes.
internal sealed class Database(SqliteConnection connection) : IDisposable
{
    private readonly object _gate = new();
    private bool _disposed;

    internal T Write<T>(Func<SqliteTransaction, T> operation)
        => Transact(operation, deferred: false);

    internal T Read<T>(Func<SqliteTransaction, T> operation)
        => Transact(operation, deferred: true);

    internal T WithoutTransaction<T>(Func<T> operation)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(typeof(SessionStore).FullName);
            }

            return operation();
        }
    }

    internal SqliteCommand Command(
        SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value is Guid id ? id.ToString("D") : value ?? DBNull.Value);
        }

        return command;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            connection.Dispose();
            _disposed = true;
        }
    }

    private T Transact<T>(Func<SqliteTransaction, T> operation, bool deferred)
    {
        return WithoutTransaction(() =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred);
            T result = operation(transaction);
            transaction.Commit();
            return result;
        });
    }
}
