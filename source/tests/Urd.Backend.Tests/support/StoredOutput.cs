using System.Text;
using Microsoft.Data.Sqlite;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Support;

// Reads persisted response chunks directly from a test database.
internal static class StoredOutput {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Decodes strictly, so a chunk that split a UTF-8 scalar fails the test.
    internal static string ReadText(TemporaryDatabase database) =>
        string.Concat(Read(database).Select(row => StrictUtf8.GetString(row.Bytes)));

    internal static List<(Guid RequestId, Guid ResponseId, long ChunkIndex, byte[] Bytes, bool Final)>
        Read(TemporaryDatabase database) {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.request_id, q.output_id, o.batch_index, o.text_utf8, o.is_final
            FROM execution_output o JOIN execution_requests q ON q.request_id = o.request_id
            ORDER BY o.request_id, o.batch_index;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var output = new List<(Guid RequestId, Guid ResponseId, long ChunkIndex, byte[] Bytes, bool Final)>();
        while (reader.Read()) {
            output.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
                (byte[])reader.GetValue(3), reader.GetBoolean(4)));
        }

        return output;
    }

    internal static async Task WaitForAnyAsync(TemporaryDatabase database) {
        using var deadline = new CancellationTokenSource(TestTimeout);
        while (database.Scalar("SELECT EXISTS(SELECT 1 FROM execution_output);") is 0L)
            await Task.Delay(10, deadline.Token);
    }
}
