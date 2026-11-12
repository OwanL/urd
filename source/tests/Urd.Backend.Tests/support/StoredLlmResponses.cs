using System.Data.Common;
using System.Text;
using Microsoft.Data.Sqlite;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Support;

// Reads persisted response chunks directly from a test database. These helpers intentionally cover one request.
internal static class StoredLlmResponses {
  private static readonly UTF8Encoding StrictUtf8 = new(false, true);

  // Decodes strictly, so a chunk that split a UTF-8 scalar fails the test.
  internal static string ReadText(TemporaryDatabase database) =>
      string.Concat(Read(database).Select(row => StrictUtf8.GetString(row.Bytes)));

  internal static List<(Guid RequestId, Guid ResponseId, long ChunkIndex, byte[] Bytes, bool Final)>
      Read(TemporaryDatabase database, Guid? requestId = null) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
      SELECT o.request_id, q.response_id, o.chunk_index, o.text_utf8, o.is_final
      FROM execution_output o JOIN execution_requests q ON q.request_id = o.request_id
      WHERE $request IS NULL OR o.request_id = $request
      ORDER BY o.chunk_index;
      """;
    command.Parameters.AddWithValue("$request", requestId is { } id ? id.ToString("D") : DBNull.Value);
    using SqliteDataReader reader = command.ExecuteReader();
    List<(Guid RequestId, Guid ResponseId, long ChunkIndex, byte[] Bytes, bool Final)> rows = reader.Cast<DbDataRecord>()
        .Select(row => (Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)), row.GetInt64(2),
            (byte[])row.GetValue(3), row.GetInt64(4) != 0))
        .ToList();
    Assert.True(rows.Select(row => row.RequestId).Distinct().Take(2).Count() <= 1,
        "Stored response readers support output from one request only.");
    return rows;
  }

  internal static async Task WaitForAnyAsync(TemporaryDatabase database) {
    using var deadline = new CancellationTokenSource(TestTimeout);
    while (database.Scalar("SELECT EXISTS(SELECT 1 FROM execution_output);") is 0L)
      await Task.Delay(10, deadline.Token);
  }
}
