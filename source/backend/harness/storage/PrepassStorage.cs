using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Retains the one-shot session title attempt and boundary-scoped prepass facts.
internal readonly record struct PrepassPreviewWrite(bool Accepted, char? PendingOutput, char? PendingReasoning);

internal sealed class PrepassStorage(Database database) {
  private const int MaximumPreviewScalars = 8192;
  private const int MaximumErrorScalars = 1024;
  private static readonly HashSet<string> Categories = ["tools", "skills", "instructions"];
  private static readonly HashSet<string> States = ["running", "completed", "fallback", "cancelled", "interrupted"];

  // The first execution ordinal owns the only title attempt, including when it never publishes a title.
  internal bool TryClaimTitle(ExecutionIdentity parent) => database.Write(transaction => {
    int claimed = database.Execute(transaction, $"""
      INSERT INTO session_prepass_title (session_id, run_id)
      SELECT r.session_id, r.run_id FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
      WHERE r.session_id = $session AND r.run_id = $run AND {ExecutionSql.CurrentRunCondition}
          AND r.ordinal = (SELECT MIN(first.ordinal) FROM execution_runs first WHERE first.session_id = r.session_id)
          AND NOT EXISTS (SELECT 1 FROM session_prepass_title WHERE session_id = r.session_id);
      """, ExecutionSql.Parameters(parent));
    if (claimed == 0)
      return false;

    if (InsertTitleDetail(transaction, parent) != 1)
      throw new InvalidOperationException("The title detail could not be initialized with its claim.");
    return true;
  });

  // Publishing settles the one-shot title attempt exactly once: a generated title completes the
  // attempt, while a failed attempt publishes its fallback and keeps the generation error.
  internal bool PublishTitle(ExecutionIdentity parent, string title) => SettleTitle(parent, title, "completed", null);

  internal bool PublishFallbackTitle(ExecutionIdentity parent, string title, string? generationError) =>
      SettleTitle(parent, title, "fallback", generationError);

  private bool SettleTitle(ExecutionIdentity parent, string title, string detailState, string? error) {
    ArgumentNullException.ThrowIfNull(title);
    _ = StorageText.StrictUtf8.GetByteCount(title);
    if (!TitleGenerator.IsValid(title))
      throw new ArgumentException("A published title must contain 1–25 characters on one line.", nameof(title));
    string? boundedError = BoundError(error);

    return database.Write(transaction => {
      // The uniqueness check and the write share one immediate transaction, so concurrent sessions
      // publishing the same title serialize before either observes the other's choice.
      int updated = database.Execute(transaction, $"""
        UPDATE session_prepass_title SET title = $title
        WHERE session_id = $session AND run_id = $run AND title IS NULL
            AND EXISTS (SELECT 1 FROM session_prepass_details d
                WHERE d.session_id = $session AND d.run_id = $run AND d.category = 'title'
                    AND d.boundary_id = $boundary AND d.state = 'running')
            AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
                WHERE r.session_id = $session AND r.run_id = $run AND {ExecutionSql.CurrentRunCondition});
        """, ExecutionSql.Parameters(parent, ("$title", FinalizeTitle(transaction, title)), ("$boundary", Guid.Empty)));
      if (updated == 0)
        return false;

      if (database.Execute(transaction, $"""
          UPDATE session_prepass_details SET state = $state, finished_at = $finished, error = $error
          WHERE session_id = $session AND run_id = $run AND boundary_id = $boundary AND category = 'title'
              AND state = 'running'
              AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
                  WHERE r.session_id = $session AND r.run_id = $run AND {ExecutionSql.CurrentRunCondition});
          """, ExecutionSql.Parameters(parent, ("$boundary", Guid.Empty), ("$state", detailState),
            ("$finished", StorageText.UtcTimestamp()), ("$error", boundedError))) != 1)
        throw new InvalidOperationException("The published title has no matching running detail.");
      return true;
    });
  }

  // Final titles are unique across stored sessions. A duplicate base takes the lowest free " (n)"
  // suffix starting at 2, shortening the base so the suffixed title respects the character limit.
  private string FinalizeTitle(SqliteTransaction transaction, string candidate) {
    HashSet<string> stored = database.Query(transaction,
        "SELECT title FROM session_prepass_title WHERE title IS NOT NULL", row => row.String("title")).ToHashSet();
    if (!stored.Contains(candidate))
      return candidate;

    int suffix = 1;
    string final;
    do {
      suffix++;
      string suffixText = $" ({suffix})";
      int room = TitleGenerator.MaximumCharacters - suffixText.Length;
      if (room < 1)
        throw new InvalidOperationException("No unique title suffix fits within the character limit.");
      final = TitleGenerator.LimitScalars(candidate, room).TrimEnd() + suffixText;
    } while (stored.Contains(final));
    return final;
  }

  internal bool RecordTitleOutcome(ExecutionIdentity parent, string state, string? error) {
    if (state is not ("failed" or "cancelled"))
      throw new ArgumentOutOfRangeException(nameof(state), "Title outcomes must be failed or cancelled.");
    string? boundedError = BoundError(error);

    return database.Write(transaction => database.Execute(transaction, $"""
      UPDATE session_prepass_details SET state = $state, finished_at = $finished, error = $error
      WHERE session_id = $session AND run_id = $run AND boundary_id = $boundary AND category = 'title'
          AND state = 'running'
          AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
              WHERE r.session_id = $session AND r.run_id = $run
                  AND r.history_revision = $revision AND s.history_revision = $revision AND r.outcome IS NULL);
      """, ExecutionSql.Parameters(parent, ("$boundary", Guid.Empty), ("$state", state),
        ("$finished", StorageText.UtcTimestamp()), ("$error", boundedError))) == 1);
  }

  // Running updates are fenced by Stop; terminal results may commit while Stop drains the parent.
  internal bool WritePruningStatus(ExecutionIdentity parent, Guid boundaryId,
      StartingResourcePruningStatus status) => WritePruningProgress(parent, boundaryId, status, null, null);

  // Status and its detailed resource facts are published in the same transaction.
  internal bool WritePruningProgress(ExecutionIdentity parent, Guid boundaryId,
      StartingResourcePruningStatus status, IReadOnlyList<PrepassResource>? candidates,
      IReadOnlyList<string>? resultIds) {
    ArgumentNullException.ThrowIfNull(status);
    if (!Categories.Contains(status.Category))
      throw new ArgumentException("The pruning category is unsupported.", nameof(status));
    if (!States.Contains(status.State))
      throw new ArgumentException("The pruning state is unsupported.", nameof(status));
    if (status.Total < 0 || status.Retained < 0 || status.Retained > status.Total)
      throw new ArgumentOutOfRangeException(nameof(status), "Pruning counts must satisfy 0 <= retained <= total.");

    string? error = BoundError(status.Error);
    string? candidatesJson = candidates is null ? null : WriteCandidates(candidates);
    string? resultsJson = resultIds is null ? null : WriteResultIds(resultIds);
    return database.Write(transaction => {
      int changed = database.Execute(transaction, $"""
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
          ("$state", status.State), ("$total", status.Total), ("$retained", status.Retained), ("$error", error)));
      if (changed == 0)
        return false;

      if (WritePruningDetail(transaction, parent, boundaryId, status, candidatesJson, resultsJson, error) != 1)
        throw new InvalidOperationException("The pruning status could not be committed with its detail.");
      return true;
    });
  }

  // Selection facts are saved before an auxiliary conversation can contact its provider.
  // Unrelated auxiliary purposes, which have no prepass detail, are intentionally harmless.
  internal bool RecordModel(ExecutionIdentity parent, string purpose, AuxiliaryModelResolution model) {
    ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
    ArgumentNullException.ThrowIfNull(model);
    string? category = CategoryForPurpose(purpose);
    if (category is null)
      return true;
    model.Selection.Validate();
    string selectionJson = LlmSelectionJson.Write(model.Selection)!;

    return database.Write(transaction => {
      Guid boundaryId = category == "title" ? Guid.Empty : CurrentBoundary(transaction, parent);
      int changed = database.Execute(transaction, $"""
        UPDATE session_prepass_details SET llm_selection_json = $selection, used_session_model = $used
        WHERE session_id = $session AND run_id = $run AND boundary_id = $boundary AND category = $category
            AND state = 'running'
            AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
                WHERE r.session_id = $session AND r.run_id = $run AND {ExecutionSql.CurrentRunCondition});
        """, ExecutionSql.Parameters(parent, ("$boundary", boundaryId), ("$category", category),
          ("$selection", selectionJson), ("$used", model.UsedSessionModel ? 1 : 0)));
      if (changed == 1)
        return true;

      bool activeDetail = database.Scalar<bool>(transaction, """
        SELECT EXISTS (SELECT 1 FROM session_prepass_details
            WHERE session_id = $session AND run_id = $run AND boundary_id = $boundary
                AND category = $category AND state = 'running');
        """, ExecutionSql.Parameters(parent, ("$boundary", boundaryId), ("$category", category)));
      return !activeDetail;
    });
  }

  // Retains only the current running detail. A purpose with no corresponding prepass is a no-op.
  internal PrepassPreviewWrite RecordPreview(ExecutionIdentity parent, string purpose, string? output = null, string? reasoning = null,
      char? pendingOutput = null, char? pendingReasoning = null) {
    string? category = CategoryForPurpose(purpose);
    if (category is null)
      return new(true, null, null);
    if (output is null && reasoning is null)
      return new(true, pendingOutput, pendingReasoning);

    return database.Write<PrepassPreviewWrite>(transaction => {
      Guid boundaryId = category == "title" ? Guid.Empty : CurrentBoundary(transaction, parent);
      PreviewState? current = database.QuerySingleOrDefault(transaction, """
        SELECT d.output_text, d.reasoning_text, d.output_truncated, d.reasoning_truncated,
            r.stopping, r.outcome, r.history_revision = $revision AND s.history_revision = $revision AS current_revision
        FROM session_prepass_details d
        JOIN execution_runs r ON r.run_id = d.run_id AND r.session_id = d.session_id
        JOIN sessions s ON s.session_id = d.session_id
        WHERE d.session_id = $session AND d.run_id = $run AND d.boundary_id = $boundary
            AND d.category = $category AND d.state = 'running';
        """, row => new PreviewState(row.String("output_text"), row.String("reasoning_text"),
          row.Bool("output_truncated"), row.Bool("reasoning_truncated"), row.Bool("stopping"),
          row.NullableInt("outcome") is not null, row.Bool("current_revision")),
        ExecutionSql.Parameters(parent, ("$boundary", boundaryId), ("$category", category)));
      if (current is null)
        return new(true, null, null);
      if (current.Stopping || current.Settled || !current.CurrentRevision)
        return new(false, pendingOutput, pendingReasoning);

      char? nextOutputPending = pendingOutput;
      char? nextReasoningPending = pendingReasoning;
      string outputText = current.Output;
      string reasoningText = current.Reasoning;
      bool outputTruncated = current.OutputTruncated;
      bool reasoningTruncated = current.ReasoningTruncated;
      if (output is not null)
        outputText = AppendTail(outputText, output, ref nextOutputPending, ref outputTruncated);
      if (reasoning is not null)
        reasoningText = AppendTail(reasoningText, reasoning, ref nextReasoningPending, ref reasoningTruncated);

      int updated = database.Execute(transaction, $"""
        UPDATE session_prepass_details SET output_text = $output, reasoning_text = $reasoning,
            output_truncated = $output_truncated, reasoning_truncated = $reasoning_truncated
        WHERE session_id = $session AND run_id = $run AND boundary_id = $boundary AND category = $category
            AND state = 'running'
            AND EXISTS (SELECT 1 FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
                WHERE r.session_id = $session AND r.run_id = $run AND {ExecutionSql.CurrentRunCondition});
        """, ExecutionSql.Parameters(parent, ("$boundary", boundaryId), ("$category", category),
          ("$output", outputText), ("$reasoning", reasoningText),
          ("$output_truncated", outputTruncated ? 1 : 0), ("$reasoning_truncated", reasoningTruncated ? 1 : 0)));
      if (updated == 0)
        return new(false, pendingOutput, pendingReasoning);

      return new(true, nextOutputPending, nextReasoningPending);
    });
  }

  private int InsertTitleDetail(SqliteTransaction transaction, ExecutionIdentity parent) =>
      database.Execute(transaction, $"""
        INSERT INTO session_prepass_details (session_id, run_id, boundary_id, category, state, started_at)
        SELECT r.session_id, r.run_id, $boundary, 'title', 'running', $started
        FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
        WHERE r.session_id = $session AND r.run_id = $run AND {ExecutionSql.CurrentRunCondition};
        """, ExecutionSql.Parameters(parent, ("$boundary", Guid.Empty), ("$started", StorageText.UtcTimestamp())));

  private int WritePruningDetail(SqliteTransaction transaction, ExecutionIdentity parent,
      Guid boundaryId, StartingResourcePruningStatus status, string? candidatesJson, string? resultsJson, string? error) {
    string timestamp = StorageText.UtcTimestamp();
    return database.Execute(transaction, $"""
      INSERT INTO session_prepass_details (
          session_id, run_id, boundary_id, category, state, started_at, finished_at,
          candidates_json, results_json, error)
      SELECT r.session_id, r.run_id, $boundary, $category, $state, $timestamp,
          CASE WHEN $state = 'running' THEN NULL ELSE $timestamp END,
          $candidates, $results, $error
      FROM execution_runs r JOIN sessions s ON s.session_id = r.session_id
      WHERE r.session_id = $session AND r.run_id = $run
          AND r.history_revision = $revision AND s.history_revision = $revision AND r.outcome IS NULL
          AND (r.stopping = 0 OR $state <> 'running')
          AND $boundary = COALESCE((SELECT c.compaction_id FROM session_compactions c
              WHERE c.session_id = $session AND c.history_revision = $revision
              ORDER BY c.ordinal DESC LIMIT 1), '00000000-0000-0000-0000-000000000000')
      ON CONFLICT(session_id, boundary_id, category) DO UPDATE SET
          run_id = excluded.run_id,
          state = excluded.state,
          started_at = CASE WHEN session_prepass_details.run_id = excluded.run_id
              THEN session_prepass_details.started_at ELSE excluded.started_at END,
          finished_at = excluded.finished_at,
          llm_selection_json = CASE WHEN session_prepass_details.run_id = excluded.run_id
              THEN COALESCE(excluded.llm_selection_json, session_prepass_details.llm_selection_json)
              ELSE excluded.llm_selection_json END,
          -- Same-run model facts persist; a new run's detail loses the previous run's recorded model.
          used_session_model = CASE WHEN session_prepass_details.run_id = excluded.run_id
              THEN session_prepass_details.used_session_model ELSE 0 END,
          candidates_json = CASE WHEN session_prepass_details.run_id = excluded.run_id
              THEN COALESCE(excluded.candidates_json, session_prepass_details.candidates_json)
              ELSE excluded.candidates_json END,
          results_json = CASE WHEN session_prepass_details.run_id <> excluded.run_id THEN excluded.results_json
              WHEN excluded.state IN ('cancelled', 'interrupted', 'failed') THEN NULL
              ELSE COALESCE(excluded.results_json, session_prepass_details.results_json) END,
          reasoning_text = CASE WHEN session_prepass_details.run_id = excluded.run_id
              THEN session_prepass_details.reasoning_text ELSE '' END,
          output_text = CASE WHEN session_prepass_details.run_id = excluded.run_id
              THEN session_prepass_details.output_text ELSE '' END,
          reasoning_truncated = CASE WHEN session_prepass_details.run_id = excluded.run_id
              THEN session_prepass_details.reasoning_truncated ELSE 0 END,
          output_truncated = CASE WHEN session_prepass_details.run_id = excluded.run_id
              THEN session_prepass_details.output_truncated ELSE 0 END,
          error = excluded.error
      WHERE session_prepass_details.run_id <> excluded.run_id
          OR session_prepass_details.state = 'running' OR session_prepass_details.state = excluded.state;
      """, ExecutionSql.Parameters(parent, ("$boundary", boundaryId), ("$category", status.Category),
        ("$state", status.State), ("$timestamp", timestamp), ("$candidates", candidatesJson),
        ("$results", resultsJson), ("$error", error)));
  }

  private Guid CurrentBoundary(SqliteTransaction transaction, ExecutionIdentity parent) =>
      database.QuerySingleOrDefault<Guid?>(transaction, """
        SELECT COALESCE((SELECT compaction_id FROM session_compactions
            WHERE session_id = $session AND history_revision = $revision ORDER BY ordinal DESC LIMIT 1),
            '00000000-0000-0000-0000-000000000000') AS boundary;
        """, row => row.Guid("boundary"), ("$session", parent.SessionId), ("$revision", parent.HistoryRevision))
          ?? Guid.Empty;

  private static string? CategoryForPurpose(string purpose) => purpose switch {
    "pruning.tools" => "tools",
    "pruning.skills" => "skills",
    "pruning.instructions" => "instructions",
    "title" => "title",
    _ => null
  };

  private static string? BoundError(string? error) {
    if (error is null)
      return null;
    _ = StorageText.StrictUtf8.GetByteCount(error);
    return TitleGenerator.LimitScalars(error, MaximumErrorScalars);
  }

  private static string WriteCandidates(IReadOnlyList<PrepassResource> candidates) {
    ArgumentNullException.ThrowIfNull(candidates);
    foreach (PrepassResource candidate in candidates) {
      ArgumentNullException.ThrowIfNull(candidate);
      ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Id);
      ArgumentNullException.ThrowIfNull(candidate.Label);
      _ = StorageText.StrictUtf8.GetByteCount(candidate.Id);
      _ = StorageText.StrictUtf8.GetByteCount(candidate.Label);
      if (candidate.Path is not null)
        _ = StorageText.StrictUtf8.GetByteCount(candidate.Path);
    }
    return JsonSerializer.Serialize(candidates);
  }

  private static string WriteResultIds(IReadOnlyList<string> resultIds) {
    ArgumentNullException.ThrowIfNull(resultIds);
    foreach (string id in resultIds) {
      ArgumentException.ThrowIfNullOrWhiteSpace(id);
      _ = StorageText.StrictUtf8.GetByteCount(id);
    }
    return JsonSerializer.Serialize(resultIds);
  }

  private static string AppendTail(string current, string addition, ref char? pendingHighSurrogate, ref bool truncated) {
    var runes = new List<Rune>();
    foreach (Rune rune in current.EnumerateRunes())
      runes.Add(rune);

    string combined = pendingHighSurrogate is { } pending ? string.Concat(pending, addition) : addition;
    pendingHighSurrogate = null;
    for (int index = 0; index < combined.Length;) {
      OperationStatus status = Rune.DecodeFromUtf16(combined.AsSpan(index), out Rune rune, out int consumed);
      if (status == OperationStatus.NeedMoreData && char.IsHighSurrogate(combined[index])) {
        pendingHighSurrogate = combined[index];
        break;
      }
      if (status != OperationStatus.Done) {
        rune = Rune.ReplacementChar;
        consumed = 1;
      }
      runes.Add(rune);
      index += consumed;
    }

    if (runes.Count > MaximumPreviewScalars) {
      runes.RemoveRange(0, runes.Count - MaximumPreviewScalars);
      truncated = true;
    }
    var tail = new StringBuilder(runes.Count);
    foreach (Rune rune in runes)
      tail.Append(rune.ToString());
    return tail.ToString();
  }

  private sealed record PreviewState(string Output, string Reasoning, bool OutputTruncated, bool ReasoningTruncated,
      bool Stopping, bool Settled, bool CurrentRevision);
}
