using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Usage;

namespace Urd.Core.Storage;

// Persists provider usage independently from session history and transcript retention.
internal sealed class UsageStorage(Database database) {
  private const string InterruptedProblem = "Host interrupted before final provider usage.";
  private const long NanoDollarsPerDollar = 1_000_000_000;
  private const int MaximumBreakdown = 64;
  private const int MaximumHistory = 64;
  private const int MaximumRecent = 20;
  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

  internal void Begin(UsageRequest request) {
    Validate(request);
    string json = Serialize(request);
    long? cost = ToNanoDollars(request.CostUsd);
    database.Write(transaction => {
      int insertedUsageRequestCount = database.Execute(transaction, """
        INSERT INTO usage_requests (
            id, session_id, started_at, finished_at, provider, model, purpose, cost_nano_usd,
            is_estimate, problem, record_json)
        VALUES ($id, $session, $started, $finished, $provider, $model, $purpose, $cost,
            $estimate, $problem, $record)
        ON CONFLICT(id) DO NOTHING;
        """,
          ("$id", request.Id), ("$session", request.SessionId), ("$started", Timestamp(request.StartedAt)),
          ("$finished", request.FinishedAt is DateTimeOffset finished ? Timestamp(finished) : null),
          ("$provider", request.Provider), ("$model", request.Model), ("$purpose", request.Purpose),
          ("$cost", cost), ("$estimate", request.IsEstimate ? 1 : 0), ("$problem", request.Problem),
          ("$record", json));
      if (insertedUsageRequestCount == 0 && ReadRequest(transaction, request.Id) != request)
        throw new InvalidOperationException($"Usage request {request.Id} already exists with different data.");
      return true;
    });
  }

  internal void Update(UsageRequest request) {
    Validate(request);
    string json = Serialize(request);
    long? cost = ToNanoDollars(request.CostUsd);
    database.Write(transaction => {
      UsageRequest? existing = ReadRequest(transaction, request.Id);
      if (existing is null)
        throw new KeyNotFoundException($"Usage request {request.Id} does not exist.");

      bool isFinished = existing.FinishedAt is not null;
      if (isFinished) {
        if (existing != request)
          throw new InvalidOperationException($"Completed usage request {request.Id} cannot be changed.");
        return true;
      }

      EnsureSameBinding(existing, request);
      int updatedUsageRequestCount = database.Execute(transaction, """
        UPDATE usage_requests SET session_id = $session, started_at = $started, finished_at = $finished,
            provider = $provider, model = $model, purpose = $purpose, cost_nano_usd = $cost,
            is_estimate = $estimate, problem = $problem, record_json = $record
        WHERE id = $id AND finished_at IS NULL;
        """,
          ("$id", request.Id), ("$session", request.SessionId), ("$started", Timestamp(request.StartedAt)),
          ("$finished", request.FinishedAt is DateTimeOffset finished ? Timestamp(finished) : null),
          ("$provider", request.Provider), ("$model", request.Model), ("$purpose", request.Purpose),
          ("$cost", cost), ("$estimate", request.IsEstimate ? 1 : 0), ("$problem", request.Problem),
          ("$record", json));
      if (updatedUsageRequestCount != 1)
        throw new InvalidOperationException($"Usage request {request.Id} changed while it was being updated.");
      return true;
    });
  }

  internal void RecoverInterrupted() => database.Write(transaction => {
    List<UsageRequest> pending = database.Query(transaction,
        "SELECT record_json FROM usage_requests WHERE finished_at IS NULL;", row => Deserialize(row.String("record_json")));

    DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
    foreach (UsageRequest request in pending) {
      UsageRequest interrupted = request with {
        FinishedAt = finishedAt,
        IsEstimate = true,
        Problem = InterruptedProblem
      };
      database.Execute(transaction, """
        UPDATE usage_requests SET finished_at = $finished, is_estimate = 1, problem = $problem, record_json = $record
        WHERE id = $id AND finished_at IS NULL;
        """,
          ("$id", interrupted.Id), ("$finished", Timestamp(finishedAt)), ("$problem", InterruptedProblem),
          ("$record", Serialize(interrupted)));
    }
    return true;
  });

  internal UsageSummary Read(Guid? sessionId, DateTimeOffset? from, DateTimeOffset? to, bool detail = true) {
    ValidateRange(sessionId, from, to);
    (string where, (string Name, object? Value)[] parameters) = BuildFilter(sessionId, from, to);
    return database.Read(transaction => {
      if (sessionId is Guid id && ReadClosedTotals(transaction, id, from, to) is { } closed)
        return new UsageSummary(closed, [], [], []);

      string source = sessionId is null ? "global_usage_totals" : "open_usage_totals";
      UsageTotals totals = ReadTotals(transaction, source, where, parameters);
      if (!detail)
        return new UsageSummary(totals, [], [], []);

      IReadOnlyList<UsageBreakdown> breakdown = ReadBreakdown(transaction, source, where, parameters);
      IReadOnlyList<UsagePoint> history = ReadHistory(transaction, source, where, parameters, breakdown);
      IReadOnlyList<UsageRequest> recent = ReadRecent(transaction, where, parameters);
      return new UsageSummary(totals, breakdown, history, recent);
    });
  }

  internal PromptCacheSummary ReadPromptCache(Guid sessionId) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session identity cannot be empty.", nameof(sessionId));

    return database.Read(transaction => database.QuerySingleOrDefault(transaction, """
      WITH last_turn AS (
        SELECT run_id FROM execution_runs
        WHERE session_id = $session AND outcome = $completed AND kind IN ($user_input, $continuation)
        ORDER BY ordinal DESC LIMIT 1
      ), eligible AS (
        SELECT json_extract(record_json, '$.executionId') = (SELECT run_id FROM last_turn) AS in_last_turn,
            json_extract(record_json, '$.tokens.inputTokens') AS input_tokens,
            json_extract(record_json, '$.tokens.cachedInputTokens') AS cached_tokens,
            is_estimate
        FROM usage_requests
        WHERE session_id = $session AND purpose = 'conversation' AND finished_at IS NOT NULL
      ), measured AS (
        SELECT *, is_estimate = 0 AND typeof(input_tokens) = 'integer' AND input_tokens > 0
            AND typeof(cached_tokens) = 'integer' AND cached_tokens >= 0 AND cached_tokens <= input_tokens AS valid
        FROM eligible
      )
      SELECT EXISTS(SELECT 1 FROM last_turn) AS has_turn,
          COALESCE(SUM(input_tokens) FILTER (WHERE valid), 0) AS session_input,
          COALESCE(SUM(cached_tokens) FILTER (WHERE valid), 0) AS session_cached,
          COUNT(*) FILTER (WHERE valid) AS session_reported,
          COUNT(*) AS session_total,
          COALESCE(SUM(input_tokens) FILTER (WHERE valid AND in_last_turn), 0) AS turn_input,
          COALESCE(SUM(cached_tokens) FILTER (WHERE valid AND in_last_turn), 0) AS turn_cached,
          COUNT(*) FILTER (WHERE valid AND in_last_turn) AS turn_reported,
          COUNT(*) FILTER (WHERE in_last_turn) AS turn_total
      FROM measured;
      """, row => new PromptCacheSummary(
          row.Bool("has_turn") ? new(row.Long("turn_input"), row.Long("turn_cached"),
              row.Long("turn_reported"), row.Long("turn_total")) : null,
          new(row.Long("session_input"), row.Long("session_cached"),
              row.Long("session_reported"), row.Long("session_total"))),
          ("$session", sessionId), ("$completed", (int)ExecutionOutcome.Completed),
          ("$user_input", (int)ExecutionRunKind.UserInput), ("$continuation", (int)ExecutionRunKind.Continuation))
        ?? throw new InvalidDataException("Prompt cache totals are missing."));
  }

  private static void Validate(UsageRequest request) {
    ArgumentNullException.ThrowIfNull(request);
    if (request.Id == Guid.Empty || request.ConnectionId == Guid.Empty || request.SessionId == Guid.Empty
        || request.ExecutionId == Guid.Empty)
      throw new ArgumentException("Usage request identities cannot be empty.", nameof(request));
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Provider);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
    ArgumentException.ThrowIfNullOrWhiteSpace(request.Purpose);
    if (request.FinishedAt is DateTimeOffset finished && finished < request.StartedAt)
      throw new ArgumentException("Usage request cannot finish before it starts.", nameof(request));
    _ = ToNanoDollars(request.CostUsd);
  }

  private static void EnsureSameBinding(UsageRequest existing, UsageRequest updated) {
    if (existing.Id != updated.Id || existing.SessionId != updated.SessionId
        || existing.StartedAt != updated.StartedAt || existing.Provider != updated.Provider
        || existing.Purpose != updated.Purpose || existing.ConnectionId != updated.ConnectionId
        || existing.ExecutionId != updated.ExecutionId)
      throw new InvalidOperationException($"Usage request {updated.Id} cannot change its identity or start binding.");
  }

  private static long? ToNanoDollars(decimal? amount) {
    if (amount is not decimal dollars)
      return null;
    decimal nanoDollars;
    try {
      nanoDollars = dollars * NanoDollarsPerDollar;
    } catch (OverflowException) {
      throw new ArgumentOutOfRangeException(nameof(amount), "Usage cost exceeds the nanodollar storage range.");
    }
    if (nanoDollars != decimal.Truncate(nanoDollars) || nanoDollars < 0 || nanoDollars > long.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(amount), "Usage cost must be exactly representable in nanodollars.");
    return decimal.ToInt64(nanoDollars);
  }

  private static decimal ToDollars(long nanoDollars) => nanoDollars / (decimal)NanoDollarsPerDollar;

  private static string Timestamp(DateTimeOffset value) =>
      value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

  private static string Serialize(UsageRequest request) => JsonSerializer.Serialize(request, JsonOptions);

  private static UsageRequest Deserialize(string json) =>
      JsonSerializer.Deserialize<UsageRequest>(json, JsonOptions)
      ?? throw new InvalidDataException("Stored usage request is empty.");

  private UsageRequest? ReadRequest(SqliteTransaction transaction, Guid id) {
    string? json = database.Scalar<string?>(transaction,
        "SELECT record_json FROM usage_requests WHERE id = $id;", ("$id", id));
    return json is null ? null : Deserialize(json);
  }

  private static void ValidateRange(Guid? sessionId, DateTimeOffset? from, DateTimeOffset? to) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
    if (from.HasValue != to.HasValue)
      throw new ArgumentException("Both range endpoints must be supplied together.");
    if (from is DateTimeOffset start && to is DateTimeOffset end) {
      if (end < start)
        throw new ArgumentException("Usage range end must not precede its start.");
      if (sessionId is null && end.UtcTicks - start.UtcTicks > TimeSpan.FromDays(31).Ticks)
        throw new ArgumentException("Global usage reads require a range of at most 31 days.");
    } else if (sessionId is null) {
      throw new ArgumentException("Global usage reads require finite from and to range endpoints.");
    }
  }

  private static (string Where, (string Name, object? Value)[] Parameters) BuildFilter(
      Guid? sessionId, DateTimeOffset? from, DateTimeOffset? to) {
    List<string> conditions = [];
    List<(string Name, object? Value)> parameters = [];
    if (sessionId is Guid id) {
      conditions.Add("session_id = $session");
      parameters.Add(("$session", id));
    }
    if (from is DateTimeOffset start) {
      conditions.Add("started_at >= $from");
      parameters.Add(("$from", Timestamp(start)));
    }
    if (to is DateTimeOffset end) {
      conditions.Add("started_at <= $to");
      parameters.Add(("$to", Timestamp(end)));
    }
    return (conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions), [.. parameters]);
  }

  // Closed sessions retain lifetime totals, not data from which partial ranges could be reconstructed.
  private UsageTotals? ReadClosedTotals(SqliteTransaction transaction, Guid sessionId, DateTimeOffset? from,
      DateTimeOffset? to) => database.QuerySingleOrDefault(transaction, """
      SELECT cost_nano_usd, requests, estimated_requests, unknown_requests, 0 AS active_requests
      FROM closed_session_usage WHERE session_id = $session
          AND ($from IS NULL OR (first_started_at >= $from AND last_started_at <= $to));
      """, ReadTotals, ("$session", sessionId), ("$from", from is { } start ? Timestamp(start) : null),
      ("$to", to is { } end ? Timestamp(end) : null));

  private UsageTotals ReadTotals(SqliteTransaction transaction, string source, string where,
      (string Name, object? Value)[] parameters) => database.QuerySingleOrDefault(transaction, $"""
      SELECT COALESCE(SUM(cost_nano_usd), 0) AS cost_nano_usd, COALESCE(SUM(requests), 0) AS requests,
          COALESCE(SUM(estimated_requests), 0) AS estimated_requests,
          COALESCE(SUM(unknown_requests), 0) AS unknown_requests,
          COALESCE(SUM(active_requests), 0) AS active_requests
      FROM {source} {where};
      """, ReadTotals, parameters)!;

  private static UsageTotals ReadTotals(SqliteDataReader row) => new(ToDollars(row.Long("cost_nano_usd")),
      row.Long("requests"), row.Long("estimated_requests"), row.Long("unknown_requests"), row.Long("active_requests"));

  private List<UsageBreakdown> ReadBreakdown(SqliteTransaction transaction, string source, string where,
      (string Name, object? Value)[] parameters) => database.Query(transaction, $"""
      WITH usage_groups AS (
        SELECT provider, model, purpose, SUM(cost_nano_usd) AS cost_nano_usd, SUM(requests) AS requests,
            SUM(estimated_requests) AS estimated_requests, SUM(unknown_requests) AS unknown_requests,
            SUM(active_requests) AS active_requests
        FROM {source} {where}
        GROUP BY provider, model, purpose
      ), ranked AS (
        SELECT *, ROW_NUMBER() OVER (ORDER BY cost_nano_usd DESC, requests DESC,
            provider COLLATE BINARY, model COLLATE BINARY, purpose COLLATE BINARY) AS group_rank,
            COUNT(*) OVER () AS group_count
        FROM usage_groups
      ), result AS (
        SELECT provider, model, purpose, cost_nano_usd, requests, estimated_requests, unknown_requests,
            active_requests, 0 AS is_other, group_rank AS sort_order
        FROM ranked WHERE group_count <= $maximumBreakdown OR group_rank <= $retainedBreakdown
        UNION ALL
        SELECT '', '', '', SUM(cost_nano_usd), SUM(requests), SUM(estimated_requests), SUM(unknown_requests),
            SUM(active_requests), 1, MAX(group_rank) + 1
        FROM ranked WHERE group_count > $maximumBreakdown AND group_rank > $retainedBreakdown
        HAVING COUNT(*) > 0
      )
      SELECT provider, model, purpose, cost_nano_usd, requests, estimated_requests, unknown_requests,
          active_requests, is_other
      FROM result ORDER BY sort_order;
      """, row => new UsageBreakdown(row.String("provider"), row.String("model"), row.String("purpose"),
          ReadTotals(row), row.Bool("is_other")), [.. parameters, ("$maximumBreakdown", MaximumBreakdown),
          ("$retainedBreakdown", MaximumBreakdown - 1)]);

  private List<UsagePoint> ReadHistory(SqliteTransaction transaction, string source, string where,
      (string Name, object? Value)[] parameters, IReadOnlyList<UsageBreakdown> breakdown) {
    (long count, DateTimeOffset? observedStart, DateTimeOffset? observedEnd) = database.QuerySingleOrDefault(transaction, $"""
      SELECT COALESCE(SUM(requests), 0) AS requests, MIN(started_at) AS started_at, MAX(started_at) AS ended_at
      FROM {source} {where};
      """, row => (row.Long("requests"), row.NullableTimestamp("started_at"), row.NullableTimestamp("ended_at")), parameters);
    if (count == 0)
      return [];

    DateTimeOffset start = observedStart!.Value;
    DateTimeOffset end = observedEnd!.Value;
    int bucketCount = (int)Math.Min(MaximumHistory, count);
    string bucket = $"""
      CASE WHEN $bucketCount = 1 OR julianday($end) <= julianday($start) THEN 0
          ELSE MIN($bucketCount - 1, CAST((julianday(started_at) - julianday($start)) * $bucketCount
              / (julianday($end) - julianday($start)) AS INTEGER)) END
      """;
    (string Name, object? Value)[] historyParameters = [.. parameters,
      ("$start", Timestamp(start)), ("$end", Timestamp(end)), ("$bucketCount", bucketCount),
      ("$maximumBreakdown", MaximumBreakdown), ("$retainedBreakdown", MaximumBreakdown - 1)];
    Dictionary<(string Provider, string Model, string Purpose, bool IsOther), int> groupIndexes = breakdown
        .Select((group, index) => (group, index))
        .ToDictionary(item => (item.group.Provider, item.group.Model, item.group.Purpose, item.group.IsOther),
            item => item.index);
    UsageTotals emptyTotals = new(0m, 0, 0, 0, 0);
    Dictionary<int, (DateTimeOffset At, UsageTotals[] Totals)> buckets = [];
    using (SqliteCommand read = database.Command(transaction, $"""
      WITH filtered AS (
        SELECT provider, model, purpose, started_at, cost_nano_usd, requests, estimated_requests, unknown_requests,
            active_requests, {bucket} AS bucket
        FROM {source} {where}
      ), bucket_times AS (
        SELECT bucket, MAX(started_at) AS latest_started_at FROM filtered GROUP BY bucket
      ), usage_groups AS (
        SELECT provider, model, purpose, SUM(cost_nano_usd) AS cost_nano_usd, SUM(requests) AS requests,
            SUM(estimated_requests) AS estimated_requests, SUM(unknown_requests) AS unknown_requests,
            SUM(active_requests) AS active_requests
        FROM filtered GROUP BY provider, model, purpose
      ), ranked AS (
        SELECT *, ROW_NUMBER() OVER (ORDER BY cost_nano_usd DESC, requests DESC,
            provider COLLATE BINARY, model COLLATE BINARY, purpose COLLATE BINARY) AS group_rank,
            COUNT(*) OVER () AS group_count
        FROM usage_groups
      ), mapped AS (
        SELECT provider AS source_provider, model AS source_model, purpose AS source_purpose,
            CASE WHEN group_count > $maximumBreakdown AND group_rank > $retainedBreakdown THEN '' ELSE provider END
                AS provider,
            CASE WHEN group_count > $maximumBreakdown AND group_rank > $retainedBreakdown THEN '' ELSE model END AS model,
            CASE WHEN group_count > $maximumBreakdown AND group_rank > $retainedBreakdown THEN '' ELSE purpose END
                AS purpose,
            CASE WHEN group_count > $maximumBreakdown AND group_rank > $retainedBreakdown THEN 1 ELSE 0 END AS is_other
        FROM ranked
      )
      SELECT filtered.bucket, bucket_times.latest_started_at, mapped.provider, mapped.model, mapped.purpose,
          mapped.is_other, SUM(filtered.cost_nano_usd) AS cost_nano_usd, SUM(filtered.requests) AS requests,
          SUM(filtered.estimated_requests) AS estimated_requests, SUM(filtered.unknown_requests) AS unknown_requests,
          SUM(filtered.active_requests) AS active_requests
      FROM filtered JOIN bucket_times USING (bucket) JOIN mapped
          ON mapped.source_provider = filtered.provider AND mapped.source_model = filtered.model
              AND mapped.source_purpose = filtered.purpose
      GROUP BY filtered.bucket, bucket_times.latest_started_at, mapped.provider, mapped.model, mapped.purpose,
          mapped.is_other
      ORDER BY filtered.bucket, mapped.is_other, mapped.provider COLLATE BINARY,
          mapped.model COLLATE BINARY, mapped.purpose COLLATE BINARY;
      """, historyParameters))
    using (SqliteDataReader reader = read.ExecuteReader()) {
      while (reader.Read()) {
        int bucketIndex = reader.Int("bucket");
        DateTimeOffset at = reader.Timestamp("latest_started_at");
        if (!buckets.TryGetValue(bucketIndex, out var point)) {
          UsageTotals[] totals = new UsageTotals[breakdown.Count];
          Array.Fill(totals, emptyTotals);
          point = (at, totals);
          buckets.Add(bucketIndex, point);
        }
        var group = (reader.String("provider"), reader.String("model"), reader.String("purpose"), reader.Bool("is_other"));
        if (!groupIndexes.TryGetValue(group, out int groupIndex))
          throw new InvalidDataException("Usage history contains an unselected provider/model/purpose group.");
        point.Totals[groupIndex] = ReadTotals(reader);
      }
    }

    List<UsagePoint> history = new(buckets.Count);
    foreach (var bucketValue in buckets.OrderBy(item => item.Key).Select(item => item.Value)) {
      (DateTimeOffset at, UsageTotals[] totals) = bucketValue;
      UsageBreakdown[] pointBreakdown = breakdown.Select((group, index) =>
          group with { Totals = totals[index] }).ToArray();
      history.Add(new UsagePoint(at, SumTotals(pointBreakdown), pointBreakdown));
    }
    return history;
  }

  private static UsageTotals SumTotals(IEnumerable<UsageBreakdown> groups) {
    UsageTotals[] totals = groups.Select(group => group.Totals).ToArray();
    return new UsageTotals(totals.Sum(value => value.CostUsd), totals.Sum(value => value.Requests),
        totals.Sum(value => value.EstimatedRequests), totals.Sum(value => value.UnknownRequests),
        totals.Sum(value => value.ActiveRequests));
  }

  private List<UsageRequest> ReadRecent(SqliteTransaction transaction, string where,
      (string Name, object? Value)[] parameters) => database.Query(transaction, $"""
      SELECT record_json FROM usage_requests {where}
      ORDER BY started_at DESC, id DESC LIMIT {MaximumRecent};
      """, row => Deserialize(row.String("record_json")), parameters);
}
