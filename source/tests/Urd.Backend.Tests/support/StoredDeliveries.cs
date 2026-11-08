using System.Data.Common;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;

namespace Urd.Backend.Tests.Support;

// Reads durable submission delivery evidence.
internal static class StoredDeliveries {
  internal static List<StoredDelivery> Read(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
      SELECT input_id, run_id, request_id, outcome FROM execution_inputs ORDER BY ordinal;
      """;
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .Select(row => new StoredDelivery(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)),
            row.IsDBNull(2) ? null : Guid.Parse(row.GetString(2)),
            row.IsDBNull(3) ? null : (ExecutionOutcome)row.GetInt64(3)))
        .ToList();
  }
}

internal sealed record StoredDelivery(Guid SubmissionId, Guid ExecutionId, Guid? RequestId, ExecutionOutcome? Outcome);
