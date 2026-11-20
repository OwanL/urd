using Microsoft.Data.Sqlite;
using Urd.Core.Context;

namespace Urd.Core.Storage;

// Called only after the manager has fenced and joined all work owned by the session.
internal sealed class SessionClosureStorage(Database database, SessionImageCleanup images) {
  internal bool DeletePrimary(Guid sessionId, CancellationToken cancellationToken) => database.RunLocked(() => {
    cancellationToken.ThrowIfCancellationRequested();
    var deleted = database.Write(transaction => DeletePrimary(transaction, sessionId, cancellationToken));
    if (deleted is not { } orphaned)
      return false;

    images.Delete(orphaned.Tool, orphaned.Markdown);
    return true;
  });

  private (List<string> Tool, List<string> Markdown)? DeletePrimary(SqliteTransaction transaction, Guid sessionId,
      CancellationToken cancellationToken) {
    if (!database.Scalar<bool>(transaction, """
        SELECT EXISTS(SELECT 1 FROM sessions WHERE session_id = $session AND kind = $primary);
        """, ("$session", sessionId), ("$primary", (int)SessionKind.Primary)))
      return null;

    database.Execute(transaction, """
        CREATE TEMP TABLE closing_sessions (session_id TEXT PRIMARY KEY) WITHOUT ROWID;
        WITH RECURSIVE owned(session_id) AS (
          SELECT $session
          UNION ALL
          SELECT s.session_id FROM sessions s JOIN owned o ON s.parent_session_id = o.session_id
        )
        INSERT INTO closing_sessions SELECT session_id FROM owned;
        """, ("$session", sessionId));
    if (database.Scalar<bool>(transaction, """
        SELECT EXISTS(SELECT 1 FROM execution_runs
          WHERE session_id IN closing_sessions AND outcome IS NULL);
        """))
      throw new InvalidOperationException("Closure must await execution settlement.");

    images.CollectCandidates(transaction);
    CompactUsage(transaction, sessionId);
    DeleteHistory(transaction);
    var orphaned = images.ReadOrphans(transaction);
    database.Execute(transaction, "DROP TABLE closing_sessions;");
    cancellationToken.ThrowIfCancellationRequested();
    return orphaned;
  }

  private void CompactUsage(SqliteTransaction transaction, Guid sessionId) => database.Execute(transaction, """
      INSERT INTO closed_session_usage (session_id, first_started_at, last_started_at, last_finished_at,
          cost_nano_usd, requests, estimated_requests, unknown_requests, errors, input_tokens, output_tokens,
          reasoning_tokens, total_tokens)
      SELECT $session, MIN(started_at), MAX(started_at), MAX(finished_at), COALESCE(SUM(cost_nano_usd), 0),
          COUNT(*), SUM(is_estimate OR finished_at IS NULL),
          SUM(cost_nano_usd IS NULL OR problem IS NOT NULL OR finished_at IS NULL),
          SUM(problem IS NOT NULL OR finished_at IS NULL), SUM(json_extract(record_json, '$.tokens.inputTokens')),
          SUM(json_extract(record_json, '$.tokens.outputTokens')), SUM(json_extract(record_json, '$.tokens.reasoningTokens')),
          SUM(json_extract(record_json, '$.tokens.totalTokens'))
      FROM usage_requests WHERE session_id IN closing_sessions HAVING COUNT(*) > 0;

      INSERT INTO closed_usage_totals (started_at, provider, model, purpose, cost_nano_usd, requests,
          estimated_requests, unknown_requests)
      SELECT started_at, provider, model, purpose, COALESCE(SUM(cost_nano_usd), 0), COUNT(*),
          SUM(is_estimate OR finished_at IS NULL), SUM(cost_nano_usd IS NULL OR problem IS NOT NULL OR finished_at IS NULL)
      FROM usage_requests WHERE session_id IN closing_sessions GROUP BY started_at, provider, model, purpose
      ON CONFLICT (started_at, provider, model, purpose) DO UPDATE SET cost_nano_usd = cost_nano_usd + excluded.cost_nano_usd,
          requests = requests + excluded.requests, estimated_requests = estimated_requests + excluded.estimated_requests,
          unknown_requests = unknown_requests + excluded.unknown_requests;
      DELETE FROM usage_requests WHERE session_id IN closing_sessions;
      """, ("$session", sessionId));

  private void DeleteHistory(SqliteTransaction transaction) => database.Execute(transaction, """
      CREATE TEMP TABLE closing_requests (request_id TEXT PRIMARY KEY) WITHOUT ROWID;
      INSERT INTO closing_requests
      SELECT q.request_id FROM execution_requests q JOIN execution_runs r ON q.run_id = r.run_id
      WHERE r.session_id IN closing_sessions;

      DELETE FROM execution_messages WHERE session_id IN closing_sessions;
      DELETE FROM transcript_position_totals WHERE session_id IN closing_sessions;
      DELETE FROM execution_inputs WHERE session_id IN closing_sessions;
      DELETE FROM execution_tool_completions WHERE session_id IN closing_sessions;
      DELETE FROM execution_tools WHERE request_id IN closing_requests;
      DELETE FROM execution_provider_context WHERE request_id IN closing_requests;
      DELETE FROM execution_output WHERE request_id IN closing_requests;
      -- A compaction references a kept request; later requests reference that compaction.
      UPDATE execution_requests SET compaction_id = NULL WHERE request_id IN closing_requests;
      DELETE FROM session_compactions WHERE session_id IN closing_sessions;
      DELETE FROM execution_requests WHERE request_id IN closing_requests;
      DROP TABLE closing_requests;

      DELETE FROM session_prepass_title WHERE session_id IN closing_sessions;
      DELETE FROM session_pruning_status WHERE session_id IN closing_sessions;
      DELETE FROM execution_runs WHERE session_id IN closing_sessions;
      DELETE FROM message_edits WHERE session_id IN closing_sessions;
      DELETE FROM input_decisions WHERE session_id IN closing_sessions;
      -- The self-reference uses NO ACTION, so delete the complete tree in one statement.
      DELETE FROM sessions WHERE session_id IN closing_sessions;
      """);
}
