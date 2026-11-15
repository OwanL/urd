using Microsoft.Data.Sqlite;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Retains idempotent message acceptances/refusals and submits each accepted message with its execution.
internal sealed class UserMessageStorage(Database database) {
  internal (UserMessageReceipt Receipt, bool NewlyAccepted) Submit(
      Guid sessionId, long historyRevision, UserMessageSubmission submission, bool accepting, Guid proposedExecutionId,
      Guid? activeExecutionId = null) {
    ArgumentNullException.ThrowIfNull(submission);
    return database.Write(transaction => Submit(transaction, sessionId, historyRevision, submission,
        accepting, proposedExecutionId, activeExecutionId));
  }

  internal (UserMessageReceipt Receipt, bool NewlyAccepted) Submit(SqliteTransaction transaction,
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
      database.Execute(transaction, """
        INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, kind,
            llm_selection_json, working_started_at)
        VALUES ($session, $run, $revision, $input, $kind, $selection, $started);
        """,
          ("$session", sessionId), ("$run", executionId),
          ("$revision", actualRevision), ("$input", submission.SubmissionId),
          ("$kind", (int)ExecutionRunKind.UserInput),
          ("$selection", LlmSelectionJson.Write(submission.LlmSelection)),
          ("$started", StorageText.UtcTimestamp()));
    }

    database.Execute(transaction, """
      INSERT INTO execution_inputs (session_id, input_id, run_id) VALUES ($session, $input, $run);
      """,
        ("$session", sessionId), ("$run", executionId), ("$input", submission.SubmissionId));
    return (accepted, true);
  }

  internal UserMessageReceipt? ReadReceipt(Guid sessionId, Guid submissionId)
      => database.RunLocked(() => ReadReceipt(sessionId, submissionId, transaction: null));

  private UserMessageReceipt? ReadReceipt(Guid sessionId, Guid submissionId, SqliteTransaction? transaction) =>
      database.QuerySingleOrDefault(transaction, """
        SELECT expected_history_revision, original_text_utf8, decision, run_id, refusal_reason, llm_selection_json, superseded
        FROM input_decisions
        WHERE session_id = $session AND input_id = $input;
        """, row => {
          if (row.Bool("superseded"))
            throw new UserMessageConflictException(submissionId);
          LlmSelection selection = LlmSelectionJson.Read(row.String("llm_selection_json"))
              ?? throw new InvalidOperationException("The retained receipt has no LLM selection.");
          var original = new UserMessageSubmission(submissionId, row.Long("expected_history_revision"),
              StorageText.StrictUtf8.GetString((byte[])row["original_text_utf8"]), selection);
          return new UserMessageReceipt(sessionId, original, (MessageAcceptance)row.Int("decision"),
              (MessageRefusalReason?)row.NullableInt("refusal_reason"), row.NullableGuid("run_id"));
        }, ("$session", sessionId), ("$input", submissionId));

  private long ReadRevision(SqliteTransaction transaction, Guid sessionId) =>
      database.Scalar<long?>(transaction, "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId))
          ?? throw new KeyNotFoundException($"Session {sessionId} does not exist.");

  private MessageRefusalReason? DecideRefusal(SqliteTransaction transaction, Guid sessionId, long actualRevision,
      long historyRevision, UserMessageSubmission submission, bool accepting, Guid? activeExecutionId) {
    long? stopFlag = database.Scalar<long?>(transaction,
        "SELECT CASE WHEN kind = $compaction THEN 2 ELSE stopping END FROM execution_runs WHERE session_id = $session AND outcome IS NULL;",
        ("$session", sessionId), ("$compaction", (int)ExecutionRunKind.Compaction));
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

    if (database.Scalar<long>(transaction,
        "SELECT COUNT(*) FROM execution_inputs WHERE run_id = $run AND outcome IS NULL;", ("$run", currentExecutionId)) >= 8)
      return MessageRefusalReason.Capacity;

    LlmSelection? initiatingSelection = LlmSelectionJson.Read(database.Scalar<string?>(transaction, """
      SELECT llm_selection_json FROM execution_runs
      WHERE session_id = $session AND run_id = $run;
      """, ("$session", sessionId), ("$run", currentExecutionId)));
    return LlmSelectionJson.SameOperation(initiatingSelection, submission.LlmSelection)
        ? null : MessageRefusalReason.SelectionLocked;
  }

  private Guid? ReadActiveExecution(SqliteTransaction transaction, Guid sessionId) =>
      database.QuerySingleOrDefault<Guid?>(transaction,
          "SELECT run_id FROM execution_runs WHERE session_id = $session AND outcome IS NULL;",
          row => row.Guid("run_id"), ("$session", sessionId));

  private void InsertReceipt(SqliteTransaction transaction, UserMessageReceipt receipt) {
    // Length-bearing UTF-8 bytes preserve embedded NUL as well as exact Unicode.
    if (database.Execute(transaction, """
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
        ("$selection", LlmSelectionJson.Write(receipt.Submission.LlmSelection))) == 0)
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
