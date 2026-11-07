using System.Data.Common;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;

namespace Urd.Backend.Tests.Support;

// Reads persisted execution and request rows directly from a test database.
internal static class StoredExecutions {
  internal static List<StoredExecution> Read(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText =
        "SELECT session_id, run_id, input_id, stopping, outcome FROM execution_runs ORDER BY ordinal;";
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .Select(row => new StoredExecution(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)),
            Guid.Parse(row.GetString(2)), row.GetInt64(3) != 0,
            row.IsDBNull(4) ? null : (ExecutionOutcome)row.GetInt64(4)))
        .ToList();
  }

  internal static List<StoredRequest> ReadRequests(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText =
        "SELECT request_id, run_id, outcome, error FROM execution_requests ORDER BY run_id;";
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .Select(row => new StoredRequest(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)),
            row.IsDBNull(2) ? null : (ExecutionOutcome)row.GetInt64(2),
            row.IsDBNull(3) ? null : row.GetString(3)))
        .ToList();
  }
}

// Persisted execution row: identity, Stop request plus retained settlement, if settled.
internal sealed record StoredExecution(Guid SessionId, Guid ExecutionId, Guid SubmissionId, bool Stopping, ExecutionOutcome? Outcome);

// Persisted request row: identity, retained outcome and failure detail, once completed.
internal sealed record StoredRequest(Guid RequestId, Guid ExecutionId, ExecutionOutcome? Outcome, string? Error);
