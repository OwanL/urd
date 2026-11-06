using Microsoft.Data.Sqlite;

namespace Urd.Backend.Tests.Support;

// Reads persisted input decision rows directly from a test database.
internal static class StoredInputs {
    internal static List<StoredDecision> Read(TemporaryDatabase database) {
        using SqliteConnection connection = database.OpenReadOnlyConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT input_id, run_id FROM input_decisions ORDER BY input_id;";
        using SqliteDataReader reader = command.ExecuteReader();
        var decisions = new List<StoredDecision>();
        while (reader.Read()) {
            decisions.Add(new(Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1))));
        }

        return decisions;
    }
}

// Persisted decision row: the input and the run it was admitted as, if accepted.
internal sealed record StoredDecision(Guid InputId, Guid? RunId);
