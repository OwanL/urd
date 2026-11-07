using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Retains idempotent message acceptances/refusals and admits each accepted submission with its execution.
internal sealed class UserMessageStorage(Database database) {
  internal (UserMessageReceipt Receipt, bool NewlyAccepted) Admit(
      Guid sessionId, long historyRevision, UserMessageSubmission submission, bool accepting, Guid proposedExecutionId,
      Guid? activeExecutionId = null) {
    Validate(sessionId, submission);
    return database.Write(transaction => {
      UserMessageReceipt? retained = ReadReceipt(sessionId, submission.SubmissionId, transaction);
      if (retained is not null) {
        EnsureSameSubmission(retained, submission);
        return (retained, false);
      }

      long actualRevision = ReadRevision(transaction, sessionId);
      MessageRefusalReason? refusal = DecideRefusal(
          transaction, sessionId, actualRevision, historyRevision, submission, accepting, activeExecutionId);
      if (refusal is { } reason) {
        var refused = new UserMessageReceipt(sessionId, submission, MessageAcceptance.Refused, reason, null);
        InsertReceipt(transaction, refused);
        return (refused, false);
      }

#pragma warning disable S3928 // Captured method argument; validation must follow retained decisions.
      Guid? currentExecutionId = ReadActiveExecution(transaction, sessionId);
      Guid executionId = currentExecutionId ?? proposedExecutionId;
      if (executionId == Guid.Empty)
        throw new ArgumentException("Execution ID cannot be empty.", nameof(proposedExecutionId));
#pragma warning restore S3928

      var accepted = new UserMessageReceipt(sessionId, submission, MessageAcceptance.Accepted, null, executionId);
      InsertReceipt(transaction, accepted);
      if (currentExecutionId is null) {
        using SqliteCommand insertExecution = database.Command(transaction, """
          INSERT INTO execution_runs (session_id, run_id, history_revision, input_id)
          VALUES ($session, $run, $revision, $input);
          """,
            ("$session", sessionId), ("$run", executionId),
            ("$revision", actualRevision), ("$input", submission.SubmissionId));
        insertExecution.ExecuteNonQuery();
      }

      using SqliteCommand insert = database.Command(transaction, """
        INSERT INTO execution_inputs (session_id, input_id, run_id) VALUES ($session, $input, $run);
        INSERT INTO execution_messages (session_id, history_revision, run_id, role, message_id, input_id)
        VALUES ($session, $revision, $run, 0, $input, $input);
        """,
          ("$session", sessionId), ("$run", executionId),
          ("$revision", actualRevision), ("$input", submission.SubmissionId));
      insert.ExecuteNonQuery();
      return (accepted, true);
    });
  }

  internal UserMessageReceipt? ReadReceipt(Guid sessionId, Guid submissionId)
      => database.Guarded(() => ReadReceipt(sessionId, submissionId, transaction: null));

  private UserMessageReceipt? ReadReceipt(Guid sessionId, Guid submissionId, SqliteTransaction? transaction) {
    using SqliteCommand command = database.Command(transaction, """
      SELECT expected_history_revision, original_text_utf8, decision, run_id, refusal_reason FROM input_decisions
      WHERE session_id = $session AND input_id = $input;
      """,
        ("$session", sessionId),
        ("$input", submissionId));
    using SqliteDataReader reader = command.ExecuteReader();
    if (!reader.Read())
      return null;

    // Locals name the SELECT columns in order.
    long expectedRevision = reader.GetInt64(0);
    string originalText = StorageText.StrictUtf8.GetString((byte[])reader.GetValue(1));
    var acceptance = (MessageAcceptance)reader.GetInt32(2);
    Guid? executionId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
    MessageRefusalReason? refusalReason = reader.IsDBNull(4) ? null : (MessageRefusalReason)reader.GetInt32(4);
    var original = new UserMessageSubmission(submissionId, expectedRevision, originalText);
    return new UserMessageReceipt(sessionId, original, acceptance, refusalReason, executionId);
  }

  private long ReadRevision(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand read = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
    object? value = read.ExecuteScalar();
    if (value is null)
      throw new KeyNotFoundException($"Session {sessionId} does not exist.");

    return (long)value;
  }

  private MessageRefusalReason? DecideRefusal(SqliteTransaction transaction, Guid sessionId, long actualRevision,
      long historyRevision, UserMessageSubmission submission, bool accepting, Guid? activeExecutionId) {
    using SqliteCommand read = database.Command(transaction,
        "SELECT stopping FROM execution_runs WHERE session_id = $session AND outcome IS NULL;",
        ("$session", sessionId));
    object? stopFlag = read.ExecuteScalar();
    if (!accepting || stopFlag is 1L)
      return MessageRefusalReason.Stopping;

    if (actualRevision != historyRevision || submission.ExpectedHistoryRevision != actualRevision)
      return MessageRefusalReason.StaleRevision;

    if (stopFlag is null)
      return null;

    Guid? currentExecutionId = ReadActiveExecution(transaction, sessionId);
    if (activeExecutionId is null || currentExecutionId != activeExecutionId)
      return MessageRefusalReason.Capacity;

    using SqliteCommand count = database.Command(transaction,
        "SELECT COUNT(*) FROM execution_inputs WHERE run_id = $run AND outcome IS NULL;",
        ("$run", currentExecutionId));
    return (long)count.ExecuteScalar()! >= 8 ? MessageRefusalReason.Capacity : null;
  }

  private Guid? ReadActiveExecution(SqliteTransaction transaction, Guid sessionId) {
    using SqliteCommand read = database.Command(transaction,
        "SELECT run_id FROM execution_runs WHERE session_id = $session AND outcome IS NULL;",
        ("$session", sessionId));
    return read.ExecuteScalar() is string execution ? Guid.Parse(execution) : null;
  }

  private void InsertReceipt(SqliteTransaction transaction, UserMessageReceipt receipt) {
    // Length-bearing UTF-8 bytes preserve embedded NUL as well as exact Unicode.
    using SqliteCommand insert = database.Command(transaction, """
      INSERT INTO input_decisions (
          session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason)
      SELECT session_id, $input, $revision, $text, $decision, $run, $reason
      FROM sessions WHERE session_id = $session;
      """,
        ("$session", receipt.SessionId),
        ("$input", receipt.Submission.SubmissionId),
        ("$revision", receipt.Submission.ExpectedHistoryRevision),
        ("$text", StorageText.StrictUtf8.GetBytes(receipt.Submission.Text)),
        ("$decision", (int)receipt.Acceptance),
        ("$run", receipt.ExecutionId),
        ("$reason", receipt.RefusalReason is { } reason ? (int)reason : null));
    if (insert.ExecuteNonQuery() == 0)
      throw new KeyNotFoundException($"Session {receipt.SessionId} does not exist.");
  }

  private static void EnsureSameSubmission(UserMessageReceipt retained, UserMessageSubmission submission) {
    if (retained.Submission != submission)
      throw new UserMessageConflictException(submission.SubmissionId);
  }

  private static void Validate(Guid sessionId, UserMessageSubmission submission) {
    // Submissions come from public session APIs.
    ArgumentNullException.ThrowIfNull(submission);
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    if (submission.SubmissionId == Guid.Empty)
      throw new ArgumentException("Submission ID cannot be empty.", nameof(submission));

    ArgumentOutOfRangeException.ThrowIfNegative(submission.ExpectedHistoryRevision);
    if (StorageText.StrictUtf8.GetByteCount(submission.Text) > StorageText.MaximumBytes) {
      throw new ArgumentException(
          $"Submission text cannot exceed {StorageText.MaximumBytes} UTF-8 bytes.", nameof(submission));
    }
  }
}
