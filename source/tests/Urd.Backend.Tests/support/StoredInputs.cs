using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Urd.Backend.Tests.Support;

// Reads persisted input decision rows directly from a test database.
internal static class StoredInputs {
  internal static List<StoredDecision> Read(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT input_id, run_id FROM input_decisions ORDER BY input_id;";
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .Select(row => new StoredDecision(Guid.Parse(row.GetString(0)),
            row.IsDBNull(1) ? null : Guid.Parse(row.GetString(1))))
        .ToList();
  }
}

// Persisted decision row: the input and the run it was admitted as, if accepted.
internal sealed record StoredDecision(Guid InputId, Guid? RunId);
