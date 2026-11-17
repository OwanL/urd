using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Reads boundary-scoped preparation summaries and pages detailed auxiliary-run output on demand.
internal sealed class PrepassDetailsReader(Database database) {
  internal const int PageSize = 60;
  private const int MaximumTextScalars = 8192;
  private const int MaximumErrorScalars = 1024;
  private const int MaximumLabelScalars = 160;
  private const int MaximumResourceScalars = 1024;
  private const long NanoDollarsPerDollar = 1_000_000_000;
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
  private static readonly HashSet<string> Categories = ["tools", "skills", "instructions", "title"];

  internal PrepassDetails? Read(Guid sessionId, long historyRevision, Guid boundaryId, string category,
      int candidateOffset, int resultOffset) {
    Validate(sessionId, historyRevision, category, candidateOffset, resultOffset);
    return database.Read(transaction => Read(transaction, sessionId, historyRevision, boundaryId,
        category, candidateOffset, resultOffset));
  }

  internal IReadOnlyList<PrepassSummary> ReadSummaries(SqliteTransaction transaction, Guid sessionId,
      IReadOnlyCollection<Guid> boundaryIds) => ReadSummaries(transaction, sessionId, boundaryIds, null);

  internal IReadOnlyList<PrepassSummary> ReadRunSummaries(SqliteTransaction transaction, Guid sessionId,
      Guid boundaryId, Guid runId) => ReadSummaries(transaction, sessionId, [boundaryId], runId);

  private PrepassDetails? Read(SqliteTransaction transaction, Guid sessionId, long historyRevision,
      Guid boundaryId, string category, int candidateOffset, int resultOffset) {
    long actualRevision = database.Scalar<long?>(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId))
        ?? throw new KeyNotFoundException("The session does not exist.");
    if (actualRevision != historyRevision)
      throw new HistoryRevisionConflictException(historyRevision, actualRevision);
    if (boundaryId != Guid.Empty && !database.Scalar<bool>(transaction, """
        SELECT EXISTS (SELECT 1 FROM session_compactions
            WHERE session_id = $session AND history_revision = $revision AND compaction_id = $boundary);
        """, ("$session", sessionId), ("$revision", historyRevision), ("$boundary", boundaryId)))
      throw new KeyNotFoundException("The prepass boundary does not belong to this session history.");

    PrepassDetailRow? row = database.QuerySingleOrDefault(transaction, """
      SELECT d.run_id, d.state, d.started_at, d.finished_at, d.llm_selection_json,
          d.candidates_json, d.results_json, d.reasoning_text, d.output_text,
          d.reasoning_truncated, d.output_truncated, d.error,
          CASE WHEN d.category = 'title' THEN (
              SELECT title FROM session_prepass_title t
              WHERE t.session_id = d.session_id AND t.run_id = d.run_id) ELSE NULL END AS title,
          CASE WHEN d.candidates_json IS NULL THEN NULL ELSE json_array_length(d.candidates_json) END AS candidate_count,
          CASE WHEN d.results_json IS NULL THEN NULL ELSE json_array_length(d.results_json) END AS result_count
      FROM session_prepass_details d
      WHERE d.session_id = $session AND d.boundary_id = $boundary AND d.category = $category;
      """, ReadDetailRow, ("$session", sessionId), ("$boundary", boundaryId), ("$category", category));
    if (row is null)
      return null;

    LlmSelection? selection = ReadSelection(row.SelectionJson);
    string? modelId = selection?.ModelId;
    string? reasoningEffort = selection is null ? null : ReadReasoningEffort(selection);
    bool candidatesExist = row.CandidatesJson is not null && category != "title";
    bool resultsExist = row.ResultsJson is not null && category != "title";
    IReadOnlyList<PrepassResource> candidates = candidatesExist
        ? ReadResourcePage(transaction, row.CandidatesJson!, candidateOffset) : Array.Empty<PrepassResource>();
    IReadOnlyList<PrepassResource> results = resultsExist && row.CandidatesJson is not null
        ? ReadResultPage(transaction, row.CandidatesJson, row.ResultsJson!, resultOffset)
        : Array.Empty<PrepassResource>();
    (decimal? cost, bool estimated) = ReadCost(transaction, sessionId, row.RunId, category, row.StartedAt, row.FinishedAt);
    (string reasoning, bool reasoningTrimmed) = ScalarTail(row.Reasoning, MaximumTextScalars);
    (string output, bool outputTrimmed) = ScalarTail(row.Output, MaximumTextScalars);

    return new(row.RunId, boundaryId, category, row.State, modelId, reasoningEffort, row.StartedAt, row.FinishedAt,
        cost, estimated, category == "title" ? 0 : row.CandidateCount ?? 0,
        category == "title" ? null : row.ResultCount, candidates, results, candidateOffset, resultOffset,
        reasoning, output, row.ReasoningTruncated || reasoningTrimmed, row.OutputTruncated || outputTrimmed,
        row.Title, ScalarPrefix(row.Error, MaximumErrorScalars));
  }

  private IReadOnlyList<PrepassSummary> ReadSummaries(SqliteTransaction transaction, Guid sessionId,
      IReadOnlyCollection<Guid> boundaryIds, Guid? runId) {
    if (boundaryIds.Count == 0)
      return Array.Empty<PrepassSummary>();

    Guid[] boundaries = [.. boundaryIds.Distinct()];
    string[] placeholders = [.. Enumerable.Range(0, boundaries.Length).Select(index => $"$boundary{index}")];
    string runFilter = runId is null ? string.Empty : "AND run_id = $run";
    var parameters = new List<(string Name, object? Value)> { ("$session", sessionId) };
    parameters.AddRange(placeholders.Select((name, index) => (name, (object?)boundaries[index])));
    if (runId is Guid run)
      parameters.Add(("$run", run));

    return database.Query(transaction, $$"""
      SELECT boundary_id, category, state, used_session_model,
          CASE WHEN category = 'title' OR candidates_json IS NULL THEN NULL ELSE json_array_length(candidates_json) END
              AS candidate_count,
          CASE WHEN category = 'title' OR results_json IS NULL THEN NULL ELSE json_array_length(results_json) END
              AS result_count,
          error
      FROM session_prepass_details
      WHERE session_id = $session AND boundary_id IN ({{string.Join(", ", placeholders)}}) {{runFilter}}
      ORDER BY boundary_id, CASE category WHEN 'tools' THEN 0 WHEN 'skills' THEN 1
          WHEN 'instructions' THEN 2 ELSE 3 END;
      """, row => new PrepassSummary(row.Guid("boundary_id"), row.String("category"), row.String("state"),
          row.NullableInt("candidate_count"), row.NullableInt("result_count"),
          ScalarPrefix(row.NullableString("error"), MaximumErrorScalars), row.Bool("used_session_model")),
      parameters.ToArray());
  }

  private List<PrepassResource> ReadResourcePage(SqliteTransaction transaction, string json, int offset) =>
      database.Query(transaction, """
        SELECT value FROM json_each($json)
        ORDER BY CAST(key AS INTEGER) LIMIT $limit OFFSET $offset;
        """, row => ReadResource(row.String("value")), ("$json", json), ("$limit", PageSize), ("$offset", offset));

  private List<PrepassResource> ReadResultPage(SqliteTransaction transaction, string candidatesJson,
      string resultsJson, int offset) => database.Query(transaction, """
        SELECT candidate.value
        FROM json_each($results_json) selected
        JOIN json_each($candidates_json) candidate
            ON coalesce(json_extract(candidate.value, '$.id'), json_extract(candidate.value, '$.Id')) = selected.value
        WHERE CAST(selected.key AS INTEGER) >= $offset
        ORDER BY CAST(selected.key AS INTEGER) LIMIT $limit;
        """, row => ReadResource(row.String("value")), ("$results_json", resultsJson),
        ("$candidates_json", candidatesJson), ("$offset", offset), ("$limit", PageSize));

  private (decimal? Cost, bool Estimated) ReadCost(SqliteTransaction transaction, Guid sessionId, Guid runId,
      string category, DateTimeOffset startedAt, DateTimeOffset? finishedAt) {
    var usage = database.QuerySingleOrDefault(transaction, """
      SELECT COUNT(*) AS request_count, SUM(cost_nano_usd) AS cost_nano_usd,
          COALESCE(SUM(CASE WHEN cost_nano_usd IS NULL OR problem IS NOT NULL THEN 1 ELSE 0 END), 0) AS unknown_requests,
          MAX(is_estimate) AS is_estimate
      FROM usage_requests
      WHERE session_id = $session AND json_extract(record_json, '$.executionId') = $run AND purpose = $purpose
          AND started_at >= $started AND ($finished IS NULL OR started_at <= $finished);
      """, row => (Count: row.Long("request_count"), NanoDollars: row.NullableLong("cost_nano_usd"),
        Unknown: row.Long("unknown_requests"), Estimated: row.NullableInt("is_estimate") == 1),
        ("$session", sessionId), ("$run", runId), ("$purpose", category == "title" ? "title" : $"pruning.{category}"),
        ("$started", Timestamp(startedAt)),
        ("$finished", finishedAt is DateTimeOffset end ? Timestamp(end) : null));
    if (usage.Count == 0)
      return (null, false);
    return (usage.Unknown == 0 && usage.NanoDollars is long amount
        ? amount / (decimal)NanoDollarsPerDollar : null, usage.Estimated);
  }

  private static PrepassDetailRow ReadDetailRow(SqliteDataReader row) => new(row.Guid("run_id"), row.String("state"),
      row.Timestamp("started_at"), row.NullableTimestamp("finished_at"), row.NullableString("llm_selection_json"),
      row.NullableString("candidates_json"), row.NullableString("results_json"), row.NullableString("reasoning_text"),
      row.NullableString("output_text"), row.Bool("reasoning_truncated"), row.Bool("output_truncated"),
      row.NullableString("error"), row.NullableString("title"), row.NullableInt("candidate_count"), row.NullableInt("result_count"));

  private static PrepassResource ReadResource(string json) {
    try {
      PrepassResource resource = JsonSerializer.Deserialize<PrepassResource>(json, JsonOptions)
          ?? throw new InvalidDataException("A stored prepass resource is empty.");
      if (string.IsNullOrWhiteSpace(resource.Id) || string.IsNullOrWhiteSpace(resource.Label))
        throw new InvalidDataException("A stored prepass resource has no identity or label.");
      return new(ScalarPrefix(resource.Id, MaximumResourceScalars)!, ScalarPrefix(resource.Label, MaximumLabelScalars)!,
          resource.Path is null ? null : ScalarPrefix(resource.Path, MaximumResourceScalars));
    } catch (JsonException failure) {
      throw new InvalidDataException("A stored prepass resource is invalid.", failure);
    }
  }

  private static LlmSelection? ReadSelection(string? json) {
    try {
      return LlmSelectionJson.Read(json);
    } catch (JsonException failure) {
      throw new InvalidDataException("A stored prepass model selection is invalid.", failure);
    }
  }

  internal static string ReadReasoningEffort(LlmSelection selection) => selection.ReadReasoningEffort() switch {
    ReasoningEffort.None => "none",
    ReasoningEffort.Low => "low",
    ReasoningEffort.Medium => "medium",
    ReasoningEffort.High => "high",
    ReasoningEffort.ExtraHigh => "extraHigh",
    _ => "high"
  };

  private static (string Text, bool Trimmed) ScalarTail(string? value, int maximumScalars) {
    if (string.IsNullOrEmpty(value))
      return (string.Empty, false);
    Rune[] runes = [.. value.EnumerateRunes()];
    int start = Math.Max(0, runes.Length - maximumScalars);
    StringBuilder result = new(Math.Min(value.Length, maximumScalars));
    for (int index = start; index < runes.Length; index++)
      result.Append(runes[index].ToString());
    return (result.ToString(), start > 0);
  }

  private static string Timestamp(DateTimeOffset value) =>
      value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

  private static string? ScalarPrefix(string? value, int maximumScalars) {
    if (value is null)
      return null;
    StringBuilder result = new(Math.Min(value.Length, maximumScalars));
    int count = 0;
    foreach (Rune rune in value.EnumerateRunes()) {
      if (count++ == maximumScalars)
        break;
      result.Append(rune.ToString());
    }
    return result.ToString();
  }

  private static void Validate(Guid sessionId, long historyRevision, string category,
      int candidateOffset, int resultOffset) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("A session identity is required.", nameof(sessionId));
    ArgumentOutOfRangeException.ThrowIfNegative(historyRevision);
    if (!Categories.Contains(category))
      throw new ArgumentException("The prepass category is invalid.", nameof(category));
    ArgumentOutOfRangeException.ThrowIfNegative(candidateOffset);
    ArgumentOutOfRangeException.ThrowIfNegative(resultOffset);
  }

  private sealed record PrepassDetailRow(Guid RunId, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
      string? SelectionJson, string? CandidatesJson, string? ResultsJson, string? Reasoning, string? Output,
      bool ReasoningTruncated, bool OutputTruncated, string? Error, string? Title,
      int? CandidateCount, int? ResultCount);
}
