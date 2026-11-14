using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
      using SqliteCommand insert = database.Command(transaction, """
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
      if (insert.ExecuteNonQuery() == 0 && ReadRequest(transaction, request.Id) != request)
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
      using SqliteCommand update = database.Command(transaction, """
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
      if (update.ExecuteNonQuery() != 1)
        throw new InvalidOperationException($"Usage request {request.Id} changed while it was being updated.");
      return true;
    });
  }

  internal void RecoverInterrupted() => database.Write(transaction => {
    List<UsageRequest> pending = [];
    using (SqliteCommand read = database.Command(transaction,
        "SELECT record_json FROM usage_requests WHERE finished_at IS NULL;"))
    using (SqliteDataReader reader = read.ExecuteReader()) {
      while (reader.Read())
        pending.Add(Deserialize(reader.GetString(0)));
    }

    DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
    foreach (UsageRequest request in pending) {
      UsageRequest interrupted = request with {
        FinishedAt = finishedAt,
        IsEstimate = true,
        Problem = InterruptedProblem
      };
      using SqliteCommand update = database.Command(transaction, """
        UPDATE usage_requests SET finished_at = $finished, is_estimate = 1, problem = $problem, record_json = $record
        WHERE id = $id AND finished_at IS NULL;
        """,
          ("$id", interrupted.Id), ("$finished", Timestamp(finishedAt)), ("$problem", InterruptedProblem),
          ("$record", Serialize(interrupted)));
      update.ExecuteNonQuery();
    }
    return true;
  });

  internal UsageSummary Read(Guid? sessionId, DateTimeOffset? from, DateTimeOffset? to, bool detail = true) {
    ValidateRange(sessionId, from, to);
    (string where, (string Name, object? Value)[] parameters) = BuildFilter(sessionId, from, to);
    return database.Read(transaction => {
      UsageTotals totals = ReadTotals(transaction, where, parameters);
      if (!detail)
        return new UsageSummary(totals, [], [], []);

      IReadOnlyList<UsageBreakdown> breakdown = ReadBreakdown(transaction, where, parameters);
      IReadOnlyList<UsagePoint> history = ReadHistory(transaction, where, parameters, from, to);
      IReadOnlyList<UsageRequest> recent = ReadRecent(transaction, where, parameters);
      return new UsageSummary(totals, breakdown, history, recent);
    });
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
    using SqliteCommand read = database.Command(transaction,
        "SELECT record_json FROM usage_requests WHERE id = $id;", ("$id", id));
    object? value = read.ExecuteScalar();
    return value is string json ? Deserialize(json) : null;
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

  private UsageTotals ReadTotals(SqliteTransaction transaction, string where,
      (string Name, object? Value)[] parameters) {
    using SqliteCommand read = database.Command(transaction, $"""
      SELECT COALESCE(SUM(cost_nano_usd), 0), COUNT(*), COALESCE(SUM(is_estimate), 0),
          COALESCE(SUM(CASE WHEN cost_nano_usd IS NULL OR problem IS NOT NULL THEN 1 ELSE 0 END), 0),
          COALESCE(SUM(CASE WHEN finished_at IS NULL THEN 1 ELSE 0 END), 0)
      FROM usage_requests {where};
      """, parameters);
    using SqliteDataReader reader = read.ExecuteReader();
    reader.Read();
    return ReadTotals(reader);
  }

  private static UsageTotals ReadTotals(SqliteDataReader reader) => new(ToDollars(reader.GetInt64(0)),
      reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));

  private List<UsageBreakdown> ReadBreakdown(SqliteTransaction transaction, string where,
      (string Name, object? Value)[] parameters) {
    using SqliteCommand read = database.Command(transaction, $"""
      SELECT provider, model, purpose, COALESCE(SUM(cost_nano_usd), 0), COUNT(*), COALESCE(SUM(is_estimate), 0),
          COALESCE(SUM(CASE WHEN cost_nano_usd IS NULL OR problem IS NOT NULL THEN 1 ELSE 0 END), 0),
          COALESCE(SUM(CASE WHEN finished_at IS NULL THEN 1 ELSE 0 END), 0)
      FROM usage_requests {where}
      GROUP BY provider, model, purpose
      ORDER BY COALESCE(SUM(cost_nano_usd), 0) DESC, COUNT(*) DESC,
          provider COLLATE BINARY, model COLLATE BINARY, purpose COLLATE BINARY
      LIMIT {MaximumBreakdown};
      """, parameters);
    using SqliteDataReader reader = read.ExecuteReader();
    List<UsageBreakdown> values = [];
    while (reader.Read())
      values.Add(new UsageBreakdown(reader.GetString(0), reader.GetString(1), reader.GetString(2),
          ReadTotalsAt(reader, 3)));
    return values;
  }

  private List<UsagePoint> ReadHistory(SqliteTransaction transaction, string where,
      (string Name, object? Value)[] parameters, DateTimeOffset? from, DateTimeOffset? to) {
    string? observedStart;
    string? observedEnd;
    long count;
    using (SqliteCommand range = database.Command(transaction, $"""
      SELECT COUNT(*), MIN(started_at), MAX(started_at) FROM usage_requests {where};
      """, parameters))
    using (SqliteDataReader reader = range.ExecuteReader()) {
      reader.Read();
      count = reader.GetInt64(0);
      observedStart = reader.IsDBNull(1) ? null : reader.GetString(1);
      observedEnd = reader.IsDBNull(2) ? null : reader.GetString(2);
    }
    if (count == 0)
      return [];

    DateTimeOffset start = from ?? DateTimeOffset.Parse(observedStart!, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind);
    DateTimeOffset end = to ?? DateTimeOffset.Parse(observedEnd!, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind);
    long durationTicks = end.UtcTicks - start.UtcTicks;
    int bucketCount = durationTicks <= 0 ? 1 : (int)Math.Min(MaximumHistory, count);
    string bucket = $"""
      CASE WHEN $bucketCount = 1 OR julianday($end) <= julianday($start) THEN 0
          ELSE MIN($bucketCount - 1, CAST((julianday(started_at) - julianday($start)) * $bucketCount
              / (julianday($end) - julianday($start)) AS INTEGER)) END
      """;
    (string Name, object? Value)[] historyParameters = [.. parameters,
      ("$start", Timestamp(start)), ("$end", Timestamp(end)), ("$bucketCount", bucketCount)];
    UsageTotals[] buckets = new UsageTotals[bucketCount];
    Array.Fill(buckets, new UsageTotals(0m, 0, 0, 0, 0));
    using (SqliteCommand read = database.Command(transaction, $"""
      SELECT {bucket}, COALESCE(SUM(cost_nano_usd), 0), COUNT(*), COALESCE(SUM(is_estimate), 0),
          COALESCE(SUM(CASE WHEN cost_nano_usd IS NULL OR problem IS NOT NULL THEN 1 ELSE 0 END), 0),
          COALESCE(SUM(CASE WHEN finished_at IS NULL THEN 1 ELSE 0 END), 0)
      FROM usage_requests {where}
      GROUP BY {bucket} ORDER BY 1;
      """, historyParameters))
    using (SqliteDataReader reader = read.ExecuteReader()) {
      while (reader.Read())
        buckets[reader.GetInt32(0)] = ReadTotalsAt(reader, 1);
    }

    List<UsagePoint> history = new(bucketCount);
    for (int index = 0; index < bucketCount; index++) {
      long offsetTicks = (long)((decimal)durationTicks * index / bucketCount);
      var at = new DateTimeOffset(start.UtcTicks + offsetTicks, TimeSpan.Zero);
      history.Add(new UsagePoint(at, buckets[index]));
    }
    return history;
  }

  private List<UsageRequest> ReadRecent(SqliteTransaction transaction, string where,
      (string Name, object? Value)[] parameters) {
    using SqliteCommand read = database.Command(transaction, $"""
      SELECT record_json FROM usage_requests {where}
      ORDER BY started_at DESC, id DESC LIMIT {MaximumRecent};
      """, parameters);
    using SqliteDataReader reader = read.ExecuteReader();
    List<UsageRequest> recent = [];
    while (reader.Read())
      recent.Add(Deserialize(reader.GetString(0)));
    return recent;
  }

  private static UsageTotals ReadTotalsAt(SqliteDataReader reader, int firstColumn) =>
      new(ToDollars(reader.GetInt64(firstColumn)), reader.GetInt64(firstColumn + 1),
          reader.GetInt64(firstColumn + 2), reader.GetInt64(firstColumn + 3), reader.GetInt64(firstColumn + 4));
}
