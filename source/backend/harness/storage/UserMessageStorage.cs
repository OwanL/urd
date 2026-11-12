using Microsoft.Data.Sqlite;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Retains idempotent message acceptances/refusals and admits each accepted submission with its execution.
internal sealed class UserMessageStorage(Database database) {
  internal (UserMessageReceipt Receipt, bool NewlyAccepted) Admit(
      Guid sessionId, long historyRevision, UserMessageSubmission submission, bool accepting, Guid proposedExecutionId,
      Guid? activeExecutionId = null) {
    ArgumentNullException.ThrowIfNull(submission);
    return database.Write(transaction => Admit(transaction, sessionId, historyRevision, submission,
        accepting, proposedExecutionId, activeExecutionId));
  }

  internal (UserMessageReceipt Receipt, bool NewlyAccepted) Admit(SqliteTransaction transaction,
      Guid sessionId, long historyRevision, UserMessageSubmission submission, bool accepting, Guid proposedExecutionId,
      Guid? activeExecutionId = null) {
    UserMessageReceipt? retained = ReadReceipt(sessionId, submission.SubmissionId, transaction);
    if (retained is not null) {
      EnsureSameSubmission(retained, submission);
      return (retained, false);
    }

    Validate(sessionId, submission);
    long actualRevision = ReadRevision(transaction, sessionId);
    MessageRefusalReason? refusal = DecideRefusal(
        transaction, sessionId, actualRevision, historyRevision, submission, accepting, activeExecutionId);
    if (refusal is { } reason) {
      var refused = new UserMessageReceipt(sessionId, submission, MessageAcceptance.Refused, reason, null);
      InsertReceipt(transaction, refused);
      return (refused, false);
    }

    Guid? currentExecutionId = ReadActiveExecution(transaction, sessionId);
    Guid executionId = currentExecutionId ?? proposedExecutionId;
    if (executionId == Guid.Empty)
      throw new ArgumentException("Execution ID cannot be empty.", nameof(proposedExecutionId));

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
      """,
        ("$session", sessionId), ("$run", executionId), ("$input", submission.SubmissionId));
    insert.ExecuteNonQuery();
    return (accepted, true);
  }

  internal UserMessageReceipt? ReadReceipt(Guid sessionId, Guid submissionId)
      => database.Guarded(() => ReadReceipt(sessionId, submissionId, transaction: null));

  private UserMessageReceipt? ReadReceipt(Guid sessionId, Guid submissionId, SqliteTransaction? transaction) {
    using SqliteCommand command = database.Command(transaction, """
      SELECT expected_history_revision, original_text_utf8, decision, run_id, refusal_reason, llm_selection_json, superseded
      FROM input_decisions
      WHERE session_id = $session AND input_id = $input;
      """,
        ("$session", sessionId),
        ("$input", submissionId));
    using SqliteDataReader reader = command.ExecuteReader();
    if (!reader.Read())
      return null;

    if (reader.GetBoolean(6))
      throw new UserMessageConflictException(submissionId);

    // Locals name the SELECT columns in order.
    long expectedRevision = reader.GetInt64(0);
    string originalText = StorageText.StrictUtf8.GetString((byte[])reader.GetValue(1));
    var acceptance = (MessageAcceptance)reader.GetInt32(2);
    Guid? executionId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
    MessageRefusalReason? refusalReason = reader.IsDBNull(4) ? null : (MessageRefusalReason)reader.GetInt32(4);
    LlmSelection selection = LlmSelectionJson.Read(reader.GetString(5))
        ?? throw new InvalidOperationException("The retained receipt has no LLM selection.");
    var original = new UserMessageSubmission(submissionId, expectedRevision, originalText, selection);
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
        "SELECT CASE WHEN compaction_selection_json IS NOT NULL THEN 2 ELSE stopping END FROM execution_runs WHERE session_id = $session AND outcome IS NULL;",
        ("$session", sessionId));
    object? stopFlag = read.ExecuteScalar();
    if (!accepting || stopFlag is 1L)
      return MessageRefusalReason.Stopping;

    if (actualRevision != historyRevision || submission.ExpectedHistoryRevision != actualRevision)
      return MessageRefusalReason.StaleRevision;

    if (stopFlag is 2L)
      return MessageRefusalReason.Capacity;
    if (stopFlag is null)
      return null;

    Guid? currentExecutionId = ReadActiveExecution(transaction, sessionId);
    if (activeExecutionId is null || currentExecutionId != activeExecutionId)
      return MessageRefusalReason.Capacity;

    using SqliteCommand count = database.Command(transaction,
        "SELECT COUNT(*) FROM execution_inputs WHERE run_id = $run AND outcome IS NULL;",
        ("$run", currentExecutionId));
    if ((long)count.ExecuteScalar()! >= 8)
      return MessageRefusalReason.Capacity;

    using SqliteCommand selectionRead = database.Command(transaction, """
      SELECT d.llm_selection_json FROM execution_runs r
      JOIN input_decisions d ON d.session_id = r.session_id AND d.input_id = r.input_id
      WHERE r.session_id = $session AND r.run_id = $run;
      """, ("$session", sessionId), ("$run", currentExecutionId));
    LlmSelection? initiatingSelection = LlmSelectionJson.Read(selectionRead.ExecuteScalar() as string);
    return LlmSelectionJson.SameOperation(initiatingSelection, submission.LlmSelection)
        ? null : MessageRefusalReason.SelectionLocked;
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
          session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason,
          llm_selection_json)
      SELECT session_id, $input, $revision, $text, $decision, $run, $reason, $selection
      FROM sessions WHERE session_id = $session;
      """,
        ("$session", receipt.SessionId),
        ("$input", receipt.Submission.SubmissionId),
        ("$revision", receipt.Submission.ExpectedHistoryRevision),
        ("$text", StorageText.StrictUtf8.GetBytes(receipt.Submission.Text)),
        ("$decision", (int)receipt.Acceptance),
        ("$run", receipt.ExecutionId),
        ("$reason", receipt.RefusalReason is { } reason ? (int)reason : null),
        ("$selection", LlmSelectionJson.Write(receipt.Submission.LlmSelection)));
    if (insert.ExecuteNonQuery() == 0)
      throw new KeyNotFoundException($"Session {receipt.SessionId} does not exist.");
  }

  private static void EnsureSameSubmission(UserMessageReceipt retained, UserMessageSubmission submission) {
    if (retained.Submission != submission)
      throw new UserMessageConflictException(submission.SubmissionId);
  }

  private static void Validate(Guid sessionId, UserMessageSubmission submission) {
    if (sessionId == Guid.Empty)
      throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));

    if (submission.SubmissionId == Guid.Empty)
      throw new ArgumentException("Submission ID cannot be empty.", nameof(submission));

    if (submission.LlmSelection is not { Connection: not null } selection)
      throw new ArgumentException("An LLM selection and connection are required.", nameof(submission));

    selection.Validate();

    ArgumentOutOfRangeException.ThrowIfNegative(submission.ExpectedHistoryRevision);
    _ = StorageText.StrictUtf8.GetByteCount(submission.Text);
  }
}
