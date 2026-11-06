using Microsoft.Data.Sqlite;
using Urd.Core.Execution;

namespace Urd.Backend.Tests.Support;

// Reads persisted run and request rows directly from a test database.
internal static class StoredRuns {
    internal static List<StoredRun> Read(TemporaryDatabase database) {
        using SqliteConnection connection = database.OpenReadOnlyConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT session_id, run_id, input_id, stopping, outcome FROM execution_runs ORDER BY ordinal;";
        using SqliteDataReader reader = command.ExecuteReader();
        var runs = new List<StoredRun>();
        while (reader.Read()) {
            runs.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : (ExecutionOutcome)reader.GetInt64(4)));
        }

        return runs;
    }

    internal static List<StoredRequest> ReadRequests(TemporaryDatabase database) {
        using SqliteConnection connection = database.OpenReadOnlyConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT request_id, run_id, outcome, error FROM execution_requests ORDER BY run_id;";
        using SqliteDataReader reader = command.ExecuteReader();
        var requests = new List<StoredRequest>();
        while (reader.Read()) {
            requests.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.IsDBNull(2) ? null : (ExecutionOutcome)reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return requests;
    }
}

// Persisted run row: identity, Stop request plus retained settlement, if settled.
internal sealed record StoredRun(Guid SessionId, Guid RunId, Guid InputId, bool Stopping, ExecutionOutcome? Outcome);

// Persisted request row: identity, retained outcome and failure detail, once completed.
internal sealed record StoredRequest(Guid RequestId, Guid RunId, ExecutionOutcome? Outcome, string? Error);
