using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Urd.Backend.Tests.Support;

// Reads persisted submission receipts directly from a test database.
internal static class StoredSubmissions {
  internal static List<StoredSubmission> Read(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT input_id, run_id FROM input_decisions ORDER BY input_id;";
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .Select(row => new StoredSubmission(Guid.Parse(row.GetString(0)),
            row.IsDBNull(1) ? null : Guid.Parse(row.GetString(1))))
        .ToList();
  }
}

// Persisted receipt row: the submission and its execution, if accepted.
internal sealed record StoredSubmission(Guid SubmissionId, Guid? ExecutionId);
