using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Compaction;

namespace Urd.Core.Storage;

internal static class StoredContextUsage {
  // Editing rebases old rows onto the new revision without updating their measurements.
  internal const string RevisionFloorSql = """
    CASE WHEN $revision = 0 THEN 0 ELSE COALESCE((
        SELECT r.ordinal FROM message_edits e JOIN execution_runs r ON r.run_id = e.run_id
        WHERE e.session_id = $session AND e.history_revision = $revision LIMIT 1), 0) END
    """;

  internal static long? ReadTokens(string? json) {
    if (json is null) return null;

    try {
      UsageDetails? usage = JsonSerializer.Deserialize<UsageDetails>(json);
      return usage?.InputTokenCount is >= 0 && usage.OutputTokenCount is >= 0
          ? ContextTokenBudget.AddSaturating(usage.InputTokenCount.Value, usage.OutputTokenCount.Value) : null;
    } catch (JsonException) {
      return null;
    }
  }
}
