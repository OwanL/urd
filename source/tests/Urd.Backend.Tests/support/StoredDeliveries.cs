using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;

namespace Urd.Backend.Tests.Support;

// Reads durable input delivery, request order and message index evidence.
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

  internal static List<(Guid RequestId, long Index)> ReadRequestOrder(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT request_id, request_index FROM execution_requests ORDER BY run_id, request_index;";
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .Select(row => (Guid.Parse(row.GetString(0)), row.GetInt64(1)))
        .ToList();
  }

  internal static List<(ChatRole Role, Guid MessageId)> ReadMessages(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT role, message_id FROM execution_messages ORDER BY ordinal;";
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .Select(row => (row.GetInt64(0) == 0 ? ChatRole.User : ChatRole.Assistant, Guid.Parse(row.GetString(1))))
        .ToList();
  }
}

internal sealed record StoredDelivery(Guid InputId, Guid RunId, Guid? RequestId, ExecutionOutcome? Outcome);
