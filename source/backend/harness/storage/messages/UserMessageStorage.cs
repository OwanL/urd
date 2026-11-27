using Microsoft.Data.Sqlite;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

internal enum InputProvenance { Human, AgentQuestion, InheritedContext, AgentAssignment }

// Retains idempotent message acceptances/refusals and submits each accepted message with its execution.
internal sealed class UserMessageStorage(Database database) {
  private const int MaxImagesPerMessage = 8;
  internal (UserMessageReceipt Receipt, bool NewlyAccepted) Submit(
      Guid sessionId, long historyRevision, UserMessageSubmission submission, bool accepting, Guid proposedExecutionId,
      Guid? activeExecutionId = null, InputProvenance provenance = InputProvenance.Human, Guid? senderSessionId = null) {
    ArgumentNullException.ThrowIfNull(submission);
    return database.Write(transaction => Submit(transaction, sessionId, historyRevision, submission,
        accepting, proposedExecutionId, activeExecutionId, provenance, senderSessionId));
  }

  internal (UserMessageReceipt Receipt, bool NewlyAccepted) Submit(SqliteTransaction transaction,
      Guid sessionId, long historyRevision, UserMessageSubmission submission, bool accepting, Guid proposedExecutionId,
      Guid? activeExecutionId = null, InputProvenance provenance = InputProvenance.Human, Guid? senderSessionId = null) {
    UserMessageReceipt? retained = ReadReceipt(sessionId, submission.SubmissionId, transaction);
    if (retained is not null) {
      EnsureSameSubmission(retained, submission);
      return (retained, false);
    }

    Validate(sessionId, submission);
    ValidateImages(transaction, sessionId, submission.ImageIds, submission.LlmSelection!);
    long actualRevision = ReadRevision(transaction, sessionId);
    ActiveRun? active = database.QuerySingleOrDefault(transaction, """
      SELECT r.run_id, r.kind, r.stopping, r.llm_selection_json, q.run_id AS queued_run
      FROM execution_runs r LEFT JOIN execution_runs q ON q.queued_after_compaction = r.run_id AND q.outcome IS NULL
      WHERE r.session_id = $session AND r.outcome IS NULL AND r.queued_after_compaction IS NULL;
      """, row => new ActiveRun(row.Guid("run_id"), row.Int("kind") == (int)ExecutionRunKind.Compaction,
          row.Bool("stopping"), row.NullableString("llm_selection_json"), row.NullableGuid("queued_run")),
        ("$session", sessionId));
    Guid? compactionId = active is { IsCompaction: true } ? active.Id : null;
    Guid? deliveryRun = active?.DeliveryId;
    MessageRefusalReason? refusal = DecideRefusal(transaction, actualRevision, historyRevision, submission,
        accepting, active, activeExecutionId, provenance);
    if (refusal is { } reason) {
      var refused = new UserMessageReceipt(sessionId, submission, MessageAcceptance.Refused, reason, null);
      InsertReceipt(transaction, refused);
      return (refused, false);
    }

    Guid executionId = deliveryRun ?? proposedExecutionId;
    if (executionId == Guid.Empty)
      throw new ArgumentException("Execution ID cannot be empty.", nameof(proposedExecutionId));

    var accepted = new UserMessageReceipt(sessionId, submission, MessageAcceptance.Accepted, null, executionId);
    InsertReceipt(transaction, accepted);
    if (deliveryRun is null) {
      database.Execute(transaction, """
        INSERT INTO execution_runs (session_id, run_id, history_revision, input_id, kind,
            llm_selection_json, working_started_at, queued_after_compaction)
        VALUES ($session, $run, $revision, $input, $kind, $selection, $started, $compaction);
        """,
          ("$session", sessionId), ("$run", executionId),
          ("$revision", actualRevision), ("$input", submission.SubmissionId),
          ("$kind", (int)ExecutionRunKind.UserInput),
          ("$selection", LlmSelectionJson.Write(submission.LlmSelection)),
          ("$started", compactionId is null ? StorageText.UtcTimestamp() : null), ("$compaction", compactionId));
    }

    database.Execute(transaction, """
      INSERT INTO execution_inputs (session_id, input_id, run_id, provenance, sender_session_id)
      VALUES ($session, $input, $run, $provenance, $sender);
      """,
        ("$session", sessionId), ("$run", executionId), ("$input", submission.SubmissionId),
        ("$provenance", (int)provenance), ("$sender", senderSessionId));
    return (accepted, true);
  }

  // Writes one agent question into the request presenting it: an accepted decision on the run's own
  // selection and an input already claimed by that request, attributed to the asking session.
  internal void InsertPresentedQuestion(SqliteTransaction transaction, ExecutionIdentity run, Guid requestId,
      QuestionPresentation question) {
    if (database.Execute(transaction, """
      INSERT INTO input_decisions (
          session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, llm_selection_json)
      SELECT r.session_id, $input, r.history_revision, $text, $accepted, r.run_id,
          COALESCE(r.llm_selection_json, d.llm_selection_json)
      FROM execution_runs r LEFT JOIN input_decisions d ON d.session_id = r.session_id AND d.input_id = r.input_id
      WHERE r.session_id = $session AND r.run_id = $run;
      INSERT INTO execution_inputs (session_id, input_id, run_id, request_id, provenance, sender_session_id)
      VALUES ($session, $input, $run, $request, $provenance, $sender);
      """,
        ("$session", run.SessionId), ("$run", run.ExecutionId), ("$request", requestId),
        ("$input", question.QuestionId), ("$text", StorageText.StrictUtf8.GetBytes(question.Prompt)),
        ("$accepted", (int)MessageAcceptance.Accepted), ("$provenance", (int)InputProvenance.AgentQuestion),
        ("$sender", question.SenderId)) != 2)
      throw new InvalidOperationException("The presented question could not be saved.");
  }

  internal UserMessageReceipt? ReadReceipt(Guid sessionId, Guid submissionId)
      => database.RunLocked(() => ReadReceipt(sessionId, submissionId, transaction: null));

  private UserMessageReceipt? ReadReceipt(Guid sessionId, Guid submissionId, SqliteTransaction? transaction) =>
      database.QuerySingleOrDefault(transaction, """
        SELECT expected_history_revision, original_text_utf8, decision, run_id, refusal_reason, llm_selection_json,
            superseded, image_ids_json
        FROM input_decisions
        WHERE session_id = $session AND input_id = $input;
        """, row => {
        if (row.Bool("superseded"))
          throw new UserMessageConflictException(submissionId);
        LlmSelection selection = LlmSelectionJson.Read(row.String("llm_selection_json"))
            ?? throw new InvalidOperationException("The retained receipt has no LLM selection.");
        var original = new UserMessageSubmission(submissionId, row.Long("expected_history_revision"),
            StorageText.StrictUtf8.GetString((byte[])row["original_text_utf8"]), selection,
            UserImageIds.Read(row.NullableString("image_ids_json")));
        return new UserMessageReceipt(sessionId, original, (MessageAcceptance)row.Int("decision"),
            (MessageRefusalReason?)row.NullableInt("refusal_reason"), row.NullableGuid("run_id"));
      }, ("$session", sessionId), ("$input", submissionId));

  private long ReadRevision(SqliteTransaction transaction, Guid sessionId) =>
      database.Scalar<long?>(transaction, "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId))
          ?? throw new KeyNotFoundException($"Session {sessionId} does not exist.");

  private MessageRefusalReason? DecideRefusal(SqliteTransaction transaction, long actualRevision, long historyRevision,
      UserMessageSubmission submission, bool accepting, ActiveRun? active, Guid? ownedRun, InputProvenance provenance) {
    if (!accepting || active?.Stopping == true)
      return MessageRefusalReason.Stopping;
    if (actualRevision != historyRevision || submission.ExpectedHistoryRevision != actualRevision)
      return MessageRefusalReason.StaleRevision;
    if (active is null)
      return null;
    if (active.Id != ownedRun || (active.IsCompaction && provenance != InputProvenance.Human))
      return MessageRefusalReason.Capacity;
    if (active.DeliveryId is { } deliveryRun && database.Scalar<long>(transaction,
        "SELECT COUNT(*) FROM execution_inputs WHERE run_id = $run AND outcome IS NULL;", ("$run", deliveryRun)) >= 8)
      return MessageRefusalReason.Capacity;
    return LlmSelectionJson.SameOperation(LlmSelectionJson.Read(active.Selection), submission.LlmSelection)
        ? null : MessageRefusalReason.SelectionLocked;
  }

  private sealed record ActiveRun(Guid Id, bool IsCompaction, bool Stopping, string? Selection, Guid? QueuedId) {
    internal Guid? DeliveryId => IsCompaction ? QueuedId : Id;
  }

  private void InsertReceipt(SqliteTransaction transaction, UserMessageReceipt receipt) {
    // Length-bearing UTF-8 bytes preserve embedded NUL as well as exact Unicode.
    if (database.Execute(transaction, """
      INSERT INTO input_decisions (
          session_id, input_id, expected_history_revision, original_text_utf8, decision, run_id, refusal_reason,
          llm_selection_json, image_ids_json)
      SELECT session_id, $input, $revision, $text, $decision, $run, $reason, $selection, $images
      FROM sessions WHERE session_id = $session;
      """,
        ("$session", receipt.SessionId),
        ("$input", receipt.Submission.SubmissionId),
        ("$revision", receipt.Submission.ExpectedHistoryRevision),
        ("$text", StorageText.StrictUtf8.GetBytes(receipt.Submission.Text)),
        ("$decision", (int)receipt.Acceptance),
        ("$run", receipt.ExecutionId),
        ("$reason", receipt.RefusalReason is { } reason ? (int)reason : null),
        ("$selection", LlmSelectionJson.Write(receipt.Submission.LlmSelection)),
        ("$images", UserImageIds.Write(receipt.Submission.ImageIds))) == 0)
      throw new KeyNotFoundException($"Session {receipt.SessionId} does not exist.");
  }

  private static void EnsureSameSubmission(UserMessageReceipt retained, UserMessageSubmission submission) {
    UserMessageSubmission original = retained.Submission;
    if (original with { ImageIds = null } != submission with { ImageIds = null }
        || !(original.ImageIds ?? []).SequenceEqual(submission.ImageIds ?? []))
      throw new UserMessageConflictException(submission.SubmissionId);
  }

  internal void ValidateImages(SqliteTransaction transaction, Guid sessionId,
      IReadOnlyList<Guid>? imageIds, LlmSelection selection) {
    if (imageIds is not { Count: > 0 })
      return;
    if (!selection.SupportsImages)
      throw new ArgumentException("The selected model does not support images.", nameof(selection));
    if (imageIds.Count > MaxImagesPerMessage)
      throw new ArgumentException($"A message can include at most {MaxImagesPerMessage} images.", nameof(imageIds));
    if (imageIds.Distinct().Count() != imageIds.Count)
      throw new ArgumentException("An image ID may only appear once per message.", nameof(imageIds));

    foreach (Guid imageId in imageIds) {
      if (imageId == Guid.Empty || !database.Scalar<bool>(transaction, """
          SELECT EXISTS (SELECT 1 FROM user_images WHERE session_id = $session AND image_id = $image);
          """, ("$session", sessionId), ("$image", imageId)))
        throw new ArgumentException("An image does not belong to this session.", nameof(imageIds));
    }
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
