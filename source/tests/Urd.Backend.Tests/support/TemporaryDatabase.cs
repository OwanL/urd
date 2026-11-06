using Microsoft.Data.Sqlite;

namespace Urd.Backend.Tests.Support;

// Isolated SQLite file in a temp directory, with raw SQL helpers for test setup and checks.
internal sealed class TemporaryDatabase : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"urd-identity-tests-{Guid.NewGuid():N}");

    public TemporaryDatabase()
    {
        Directory.CreateDirectory(_directory);
        Path = System.IO.Path.Combine(_directory, "urd.sqlite");
    }

    public string Path { get; }

    public void Execute(string sql)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public object? Scalar(string sql)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
