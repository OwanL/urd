using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Replaces visible history and model context together. Retained command hashes and usage
// are not a deleted-conversation archive; removed input and provider/tool bodies are erased.
internal sealed class MessageEditStorage(Database database, UserMessageStorage messages) {
  private const string SuffixRequests = """
    SELECT q.request_id FROM execution_requests q JOIN execution_runs r ON r.run_id = q.run_id
    WHERE r.session_id = $session AND (r.ordinal > $runOrdinal
        OR (r.ordinal = $runOrdinal AND q.request_index >= $requestIndex))
    """;

  private const string ObsoleteSummaries = """
    SELECT c.compaction_id FROM session_compactions c WHERE c.session_id = $session AND (
        NOT EXISTS (SELECT 1 FROM execution_messages m WHERE m.session_id = $session
            AND m.role = 4 AND m.message_id = c.compaction_id AND m.ordinal < $entry)
        OR c.first_kept_run_ordinal > $runOrdinal
        OR (c.first_kept_run_ordinal = $runOrdinal AND (c.first_kept_request_index > $requestIndex
            OR (c.first_kept_request_index = $requestIndex AND c.first_kept_message_index > $messageIndex))))
    """;

  internal static void Validate(MessageEditSubmission submission) {
    ArgumentNullException.ThrowIfNull(submission);
    if (submission.OperationId == Guid.Empty || submission.Message is not { Role: MessageRole.User } message
        || message.ExecutionId == Guid.Empty || message.MessageId == Guid.Empty || message.HistoryRevision < 0
        || submission.Text is null || submission.LlmSelection is null)
      throw new ArgumentException("Editing requires an operation ID, user message and model selection.", nameof(submission));
    submission.LlmSelection.Validate();
    _ = StorageText.StrictUtf8.GetByteCount(submission.Text);
  }

  internal MessageEditReceipt? ReadReceipt(Guid sessionId, Guid operationId) =>
      database.Read(transaction => ReadReceipt(transaction, sessionId, operationId, null));

  // Check retained operations before target identity: a committed retry necessarily carries an old revision.
  internal MessageEditReceipt? ReadReceiptOrValidate(Guid sessionId, MessageEditSubmission submission, string hash) => database.Read(transaction => {
    MessageEditReceipt? receipt = ReadReceipt(transaction, sessionId, submission.OperationId, hash);
    if (receipt is null) {
      var target = ReadTarget(transaction, sessionId, submission.Message);
      ValidateReplacement(transaction, sessionId, submission, submission.ImageIds ?? target.ImageIds);
    }
    return receipt;
  });

  internal MessageEditReceipt Replace(Guid sessionId, MessageEditSubmission submission, string? hash = null) => database.Write(transaction => {
    hash ??= Hash(submission);
    MessageEditReceipt? retained = ReadReceipt(transaction, sessionId, submission.OperationId, hash);
    if (retained is not null)
      return retained;
    var target = ReadTarget(transaction, sessionId, submission.Message);
    IReadOnlyList<Guid>? imageIds = submission.ImageIds ?? target.ImageIds;
    ValidateReplacement(transaction, sessionId, submission, imageIds);
    if (database.Scalar<bool>(transaction,
        """
        SELECT EXISTS(SELECT 1 FROM execution_runs
            WHERE session_id = $session AND outcome IS NULL AND queued_after_compaction IS NULL);
        """, ("$session", sessionId)))
      throw new InvalidOperationException("Editing must await execution settlement.");

    RemoveSuffix(transaction, sessionId, target);
    long revision = checked(submission.Message.HistoryRevision + 1);
    RebuildPositions(transaction, sessionId, revision);
    var input = new UserMessageSubmission(submission.OperationId, revision, submission.Text, submission.LlmSelection, imageIds);
    var admitted = messages.Submit(transaction, sessionId, revision, input, accepting: true, Guid.NewGuid());
    if (!admitted.NewlyAccepted || admitted.Receipt.Acceptance != MessageAcceptance.Accepted)
      throw new UserMessageConflictException(submission.OperationId);
    Guid executionId = admitted.Receipt.ExecutionId!.Value;
    database.Execute(transaction, """
      INSERT INTO message_edits (session_id, operation_id, submission_hash, history_revision, run_id)
      VALUES ($session, $operation, $hash, $revision, $run);
      """, ("$session", sessionId), ("$operation", submission.OperationId), ("$hash", hash),
        ("$revision", revision), ("$run", executionId));
    return new MessageEditReceipt(SessionId: sessionId, OperationId: submission.OperationId,
        HistoryRevision: revision, ExecutionId: executionId);
  });

  private void ValidateReplacement(SqliteTransaction transaction, Guid sessionId, MessageEditSubmission submission,
      IReadOnlyList<Guid>? imageIds) {
    if (string.IsNullOrWhiteSpace(submission.Text) && imageIds is not { Count: > 0 })
      throw new ArgumentException("A message requires text or an image.", nameof(submission));
    messages.ValidateImages(transaction, sessionId, imageIds, submission.LlmSelection);
  }

  private MessageEditReceipt? ReadReceipt(SqliteTransaction transaction, Guid sessionId, Guid operationId, string? hash) =>
      database.QuerySingleOrDefault(transaction, """
        SELECT submission_hash, history_revision, run_id FROM message_edits
        WHERE session_id = $session AND operation_id = $operation;
        """, row => {
        if (hash is not null && hash != row.String("submission_hash"))
          throw new UserMessageConflictException(operationId);
        return new MessageEditReceipt(SessionId: sessionId, OperationId: operationId,
            HistoryRevision: row.Long("history_revision"), ExecutionId: row.Guid("run_id"));
      }, ("$session", sessionId), ("$operation", operationId));

  private EditTarget ReadTarget(
      SqliteTransaction transaction, Guid sessionId, MessageRef message) {
    long actual = database.Scalar<long?>(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId))
        ?? throw new KeyNotFoundException("The session does not exist.");
    if (actual != message.HistoryRevision)
      throw new HistoryRevisionConflictException(message.HistoryRevision, actual);
    return database.QuerySingleOrDefault(transaction, """
      SELECT m.ordinal AS entry_ordinal, r.ordinal AS run_ordinal, q.request_index, i.ordinal AS input_ordinal,
          d.image_ids_json,
          (SELECT count(*) FROM execution_inputs previous WHERE previous.request_id = m.request_id AND previous.ordinal < i.ordinal)
          + (SELECT count(*) FROM execution_tool_completions completed WHERE completed.claimed_request_id = m.request_id) AS message_index
      FROM execution_messages m
      JOIN execution_runs r ON r.run_id = m.run_id
      JOIN execution_requests q ON q.request_id = m.request_id
      JOIN execution_inputs i ON i.session_id = m.session_id AND i.input_id = m.input_id
      JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
      WHERE m.session_id = $session AND m.history_revision = $revision
          AND m.run_id = $run AND m.message_id = $message AND m.role = 0;
      """, row => new EditTarget(row.Long("entry_ordinal"), row.Long("run_ordinal"), row.Int("request_index"),
          row.Long("input_ordinal"), row.Long("message_index"), UserImageIds.Read(row.NullableString("image_ids_json"))),
          ("$session", sessionId), ("$revision", actual), ("$run", message.ExecutionId), ("$message", message.MessageId))
        ?? throw new KeyNotFoundException("The user message is no longer in this conversation.");
  }

  private void RemoveSuffix(SqliteTransaction transaction, Guid sessionId, EditTarget target) {
    // Automatic compaction precedes user placement. Its earlier marker can still
    // summarize an edited input in a multi-input request; compare source positions too.
    database.Execute(transaction, $"""
      UPDATE execution_requests SET compaction_id = NULL WHERE compaction_id IN ({ObsoleteSummaries});
      DELETE FROM session_compactions WHERE compaction_id IN ({ObsoleteSummaries});
      DELETE FROM execution_messages WHERE session_id = $session AND (ordinal >= $entry
          OR (role = 4 AND NOT EXISTS (SELECT 1 FROM session_compactions c
              WHERE c.compaction_id = execution_messages.message_id)));
      UPDATE input_decisions SET original_text_utf8 = X'', image_ids_json = NULL, superseded = 1
      WHERE session_id = $session AND input_id IN (
          SELECT input_id FROM execution_inputs WHERE session_id = $session AND ordinal >= $input);
      DELETE FROM execution_inputs WHERE session_id = $session AND ordinal >= $input;
      UPDATE execution_runs SET error = NULL, compaction_focus = NULL
      WHERE session_id = $session AND ordinal >= $runOrdinal;
      """, ("$session", sessionId), ("$entry", target.EntryOrdinal),
        ("$input", target.InputOrdinal), ("$runOrdinal", target.RunOrdinal),
        ("$requestIndex", target.RequestIndex), ("$messageIndex", target.MessageIndex));

    database.Execute(transaction, $"""
      DELETE FROM execution_tool_completions WHERE session_id = $session
          AND (request_id IN ({SuffixRequests}) OR (claimed_request_id IN ({SuffixRequests})
              AND NOT EXISTS (SELECT 1 FROM execution_messages m WHERE m.session_id = $session
                  AND m.role = 3 AND m.message_id = execution_tool_completions.completion_id AND m.ordinal < $entry)));
      DELETE FROM execution_tools WHERE request_id IN ({SuffixRequests});
      DELETE FROM execution_provider_context WHERE request_id IN ({SuffixRequests});
      DELETE FROM execution_output WHERE request_id IN ({SuffixRequests});
      UPDATE execution_requests SET response_id = NULL, next_chunk_index = 0, response_final = 0,
          outcome = 1, error = NULL WHERE request_id IN ({SuffixRequests});
      """, ("$session", sessionId), ("$runOrdinal", target.RunOrdinal), ("$requestIndex", target.RequestIndex),
        ("$entry", target.EntryOrdinal));
  }

  private void RebuildPositions(SqliteTransaction transaction, Guid sessionId, long revision) {
    // Preserve message IDs in the kept prefix, but invalidate every old cursor and layout index.
    database.Execute(transaction, """
      UPDATE sessions SET history_revision = $revision WHERE session_id = $session;
      UPDATE execution_runs SET history_revision = $revision WHERE session_id = $session;
      UPDATE execution_messages SET history_revision = $revision WHERE session_id = $session;
      UPDATE session_compactions SET history_revision = $revision WHERE session_id = $session;
      UPDATE execution_tool_completions SET history_revision = $revision WHERE session_id = $session;
      DELETE FROM transcript_positions WHERE session_id = $session;
      DELETE FROM transcript_position_totals WHERE session_id = $session;
      INSERT INTO transcript_positions (entry_ordinal, session_id, history_revision, rank, kind, weight, prefix_weight)
      SELECT ordinal, session_id, $revision, row_number() OVER (ORDER BY ordinal) - 1, role, layout_weight,
          coalesce(sum(layout_weight) OVER (ORDER BY ordinal ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0)
      FROM execution_messages WHERE session_id = $session;
      INSERT INTO transcript_position_totals (session_id, history_revision, entry_count, total_weight)
      SELECT $session, $revision, count(*), coalesce(sum(layout_weight), 0)
      FROM execution_messages WHERE session_id = $session;
      """, ("$session", sessionId), ("$revision", revision));
  }

  // Null is omitted so operations journaled before attachments keep their original hash.
  internal static string Hash(MessageEditSubmission submission) =>
      Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(submission)));

  private sealed record EditTarget(long EntryOrdinal, long RunOrdinal, int RequestIndex, long InputOrdinal,
      long MessageIndex, IReadOnlyList<Guid>? ImageIds);
}
