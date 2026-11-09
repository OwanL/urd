using System.Buffers;
using System.Text;
using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Reads small session facts without loading a transcript window.
internal sealed class SnapshotReader {
  private const int PreviewUtf16Limit = 256;
  private const int PendingInputLimit = 8;
  private readonly Database database;
  private readonly MessageContentReader content;

  internal const string FirstUserSql = """
    SELECT m.run_id, m.message_id FROM execution_messages m
    JOIN execution_inputs i ON i.session_id = m.session_id AND i.run_id = m.run_id
        AND i.input_id = m.input_id
    WHERE m.session_id = $session AND m.history_revision = $revision AND m.role = 0
        AND i.request_id IS NOT NULL
    ORDER BY m.ordinal LIMIT 1;
    """;

  internal const string PendingInputsSql = """
    SELECT i.input_id, coalesce(substr(d.original_text_utf8, 1, $bytes), X'')
    FROM execution_inputs i
    JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
    WHERE i.run_id = (
        SELECT run_id FROM execution_runs WHERE session_id = $session AND outcome IS NULL
    ) AND i.request_id IS NULL AND i.outcome IS NULL
    ORDER BY i.ordinal LIMIT $limit;
    """;

  internal const string SnapshotUnsettledSql = """
    SELECT run_id, stopping FROM execution_runs
    WHERE session_id = $session AND outcome IS NULL;
    """;

  internal const string SnapshotSettlementSql = """
    SELECT run_id, outcome FROM execution_runs
    WHERE session_id = $session AND outcome IS NOT NULL
    ORDER BY ordinal DESC LIMIT 1;
    """;

  internal const string SnapshotSelectionSql = """
    SELECT d.llm_selection_json FROM execution_runs r
    JOIN input_decisions d ON d.session_id = r.session_id AND d.input_id = r.input_id
    WHERE r.session_id = $session AND r.run_id = $run;
    """;

  internal SnapshotReader(Database database, MessageContentReader content) {
    this.database = database;
    this.content = content;
  }

  // Reads stored facts only. SessionBase supplies live admission and Stop state before exposure.
  internal SessionSnapshot ReadStored(Guid sessionId) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    return database.Read(transaction => Read(transaction, sessionId));
  }

  private SessionSnapshot Read(SqliteTransaction transaction, Guid sessionId) {
    long historyRevision = ReadHistoryRevision(transaction, sessionId);
    (Guid? unsettledExecution, bool stopRequested) = ReadUnsettledExecution(transaction, sessionId);
    SessionExecutionResult? lastSettlement = ReadLastSettlement(transaction, sessionId);
    LlmSelection? latestSelection = ReadLatestSelection(
        transaction, sessionId, unsettledExecution ?? lastSettlement?.ExecutionId);
    string? firstUserPreview = ReadFirstUserPreview(transaction, sessionId, historyRevision);
    PendingInputPreview[] pendingInputs = ReadPendingInputs(transaction, sessionId);

    // Storage cannot establish live admission eligibility; the session replaces this fence.
    return new(sessionId, historyRevision, AdmissionFenced: true,
        unsettledExecution, stopRequested, lastSettlement, firstUserPreview, pendingInputs, latestSelection);
  }

  private long ReadHistoryRevision(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand revisionRead = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
    if (revisionRead.ExecuteScalar() is not long historyRevision)
      throw new KeyNotFoundException($"Session {sessionId} does not exist.");

    return historyRevision;
  }

  private (Guid? ExecutionId, bool StopRequested) ReadUnsettledExecution(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand activeRead = database.Command(transaction, SnapshotUnsettledSql, ("$session", sessionId));
    using SqliteDataReader reader = activeRead.ExecuteReader();
    return reader.Read() ? (reader.GetGuid(0), reader.GetBoolean(1)) : (null, false);
  }

  private SessionExecutionResult? ReadLastSettlement(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand terminalRead = database.Command(transaction, SnapshotSettlementSql,
        ("$session", sessionId));
    using SqliteDataReader reader = terminalRead.ExecuteReader();
    return reader.Read() ? new(reader.GetGuid(0), (ExecutionOutcome)reader.GetInt32(1)) : null;
  }

  private LlmSelection? ReadLatestSelection(SqliteTransaction transaction, Guid sessionId, Guid? executionId) {
    if (executionId is null)
      return null;

    // The initiating submission owns selection even when it is outside the message window.
    using SqliteCommand command = database.Command(transaction, SnapshotSelectionSql,
        ("$session", sessionId), ("$run", executionId));
    return LlmSelectionJson.Read(command.ExecuteScalar() as string);
  }

  private string? ReadFirstUserPreview(SqliteTransaction transaction, Guid sessionId, long historyRevision) {
    using SqliteCommand firstUser = database.Command(transaction, FirstUserSql,
        ("$session", sessionId), ("$revision", historyRevision));
    using SqliteDataReader reader = firstUser.ExecuteReader();
    if (!reader.Read())
      return null;

    var message = new MessageRef(reader.GetGuid(0), reader.GetGuid(1), MessageRole.User, historyRevision);
    reader.Close();
    return content.ReadPreview(transaction, sessionId, message, PreviewUtf16Limit).Text;
  }

  private PendingInputPreview[] ReadPendingInputs(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand pendingRead = database.Command(transaction, PendingInputsSql,
        ("$session", sessionId), ("$limit", PendingInputLimit),
        ("$bytes", PreviewUtf16Limit * 3 + 3));
    using SqliteDataReader reader = pendingRead.ExecuteReader();
    var inputs = new List<PendingInputPreview>(PendingInputLimit);
    while (reader.Read())
      inputs.Add(new(reader.GetGuid(0), ReadPreview((byte[])reader.GetValue(1))));

    return inputs.ToArray();
  }

  private static string ReadPreview(byte[] utf8) {
    var text = new StringBuilder(Math.Min(PreviewUtf16Limit, utf8.Length));
    Span<char> scalar = stackalloc char[2];
    int offset = 0;
    while (offset < utf8.Length && text.Length < PreviewUtf16Limit) {
      OperationStatus status = Rune.DecodeFromUtf8(utf8.AsSpan(offset), out Rune rune, out int bytes);
      if (status == OperationStatus.NeedMoreData)
        break;
      if (status != OperationStatus.Done)
        throw new InvalidDataException("A pending input contains invalid UTF-8.");
      if (rune.Utf16SequenceLength > PreviewUtf16Limit - text.Length)
        break;

      int units = rune.EncodeToUtf16(scalar);
      text.Append(scalar[..units]);
      offset += bytes;
    }

    return text.ToString();
  }
}
