using System.Data.Common;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;

namespace Urd.Backend.Tests.Support;

// Reads durable authorization, claim and result evidence without invoking tools.
internal static class StoredTools {
  internal static List<StoredTool> Read(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
      SELECT run_id, request_id, call_index, call_id, name, arguments_json, arguments_excerpt,
          started, outcome, result_json, error
      FROM execution_tools ORDER BY request_id, call_index;
      """;
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Cast<DbDataRecord>()
        .Select(row => new StoredTool(Guid.Parse(row.GetString(0)), Guid.Parse(row.GetString(1)), row.GetInt64(2),
            row.GetString(3), row.GetString(4), row.GetString(5), row.GetString(6), row.GetInt64(7) != 0,
            row.IsDBNull(8) ? null : (ExecutionOutcome)row.GetInt64(8),
            row.IsDBNull(9) ? null : row.GetString(9), row.IsDBNull(10) ? null : row.GetString(10)))
        .ToList();
  }
}

internal sealed record StoredTool(Guid ExecutionId, Guid RequestId, long Index, string CallId, string Name,
    string ArgumentsJson, string ArgumentsExcerpt, bool Started, ExecutionOutcome? Outcome, string? ResultJson,
    string? Error);
