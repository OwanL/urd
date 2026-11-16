using Urd.Core.Execution;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Retains the one-shot session title attempt and boundary-scoped pruning status.
internal sealed class PrepassStorage(Database database) {
  private static readonly HashSet<string> Categories = ["tools", "skills", "instructions"];
  private static readonly HashSet<string> States = ["running", "completed", "fallback", "cancelled", "interrupted"];

  // The first execution ordinal owns the only title attempt, including when it never publishes a title.
  internal bool TryClaimTitle(ExecutionIdentity parent) => database.Write(transaction =>
      database.Execute(transaction, $"""
        INSERT INTO session_prepass_title (session_id, run_id)
        SELECT r.session_id, r.run_id FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
        WHERE r.session_id = $session AND r.run_id = $run AND {ExecutionSql.CurrentRunCondition}
            AND r.ordinal = (SELECT MIN(first.ordinal) FROM execution_runs first WHERE first.session_id = r.session_id)
            AND NOT EXISTS (SELECT 1 FROM session_prepass_title WHERE session_id = r.session_id);
        """, ExecutionSql.Parameters(parent)) == 1);

  internal bool PublishTitle(ExecutionIdentity parent, string title) {
    ArgumentNullException.ThrowIfNull(title);
    _ = StorageText.StrictUtf8.GetByteCount(title);
    if (!TitleGenerator.IsValid(title))
      throw new ArgumentException("A published title must contain 1–25 characters on one line.", nameof(title));

    return database.Write(transaction => database.Execute(transaction, $"""
      UPDATE session_prepass_title SET title = $title
      WHERE session_id = $session AND run_id = $run AND title IS NULL
          AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
              WHERE r.session_id = $session AND r.run_id = $run AND {ExecutionSql.CurrentRunCondition});
      """, ExecutionSql.Parameters(parent, ("$title", title))) == 1);
  }

  // Running updates are fenced by Stop; terminal results may commit while Stop is draining the parent.
  internal bool WritePruningStatus(ExecutionIdentity parent, Guid boundaryId,
      StartingResourcePruningStatus status) {
    ArgumentNullException.ThrowIfNull(status);
    if (!Categories.Contains(status.Category))
      throw new ArgumentException("The pruning category is unsupported.", nameof(status));
    if (!States.Contains(status.State))
      throw new ArgumentException("The pruning state is unsupported.", nameof(status));
    if (status.Total < 0 || status.Retained < 0 || status.Retained > status.Total)
      throw new ArgumentOutOfRangeException(nameof(status), "Pruning counts must satisfy 0 <= retained <= total.");
    if (status.Error is not null)
      _ = StorageText.StrictUtf8.GetByteCount(status.Error);

    return database.Write(transaction => database.Execute(transaction, $"""
      INSERT INTO session_pruning_status (session_id, run_id, boundary_id, category, state, total, retained, error)
      SELECT r.session_id, r.run_id, $boundary, $category, $state, $total, $retained, $error
      FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
      WHERE r.session_id = $session AND r.run_id = $run
          AND r.history_revision = $revision AND s.history_revision = $revision AND r.outcome IS NULL
          AND (r.stopping = 0 OR $state <> 'running')
          AND $boundary = COALESCE((SELECT c.compaction_id FROM session_compactions c
              WHERE c.session_id = $session AND c.history_revision = $revision
              ORDER BY c.ordinal DESC LIMIT 1), '00000000-0000-0000-0000-000000000000')
      ON CONFLICT(session_id, boundary_id, category) DO UPDATE SET
          run_id = excluded.run_id, state = excluded.state,
          total = excluded.total, retained = excluded.retained, error = excluded.error
      WHERE session_pruning_status.run_id <> excluded.run_id
          OR session_pruning_status.state = 'running' OR session_pruning_status.state = excluded.state;
      """, ExecutionSql.Parameters(parent, ("$boundary", boundaryId), ("$category", status.Category),
        ("$state", status.State), ("$total", status.Total), ("$retained", status.Retained),
        ("$error", status.Error))) == 1);
  }
}
