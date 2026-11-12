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
        || string.IsNullOrWhiteSpace(submission.Text) || submission.LlmSelection is null)
      throw new ArgumentException("Editing requires an operation ID, user message, text and model selection.", nameof(submission));
    submission.LlmSelection.Validate();
    _ = StorageText.StrictUtf8.GetByteCount(submission.Text);
  }

  internal MessageEditReceipt? ReadReceipt(Guid sessionId, Guid operationId) =>
      database.Read(transaction => ReadReceipt(transaction, sessionId, operationId, null));

  // Check retained operations before target identity: a committed retry necessarily carries an old revision.
  internal MessageEditReceipt? Check(Guid sessionId, MessageEditSubmission submission) => database.Read(transaction => {
    MessageEditReceipt? receipt = ReadReceipt(transaction, sessionId, submission.OperationId, Hash(submission));
    if (receipt is null)
      ReadTarget(transaction, sessionId, submission.Message);
    return receipt;
  });

  internal MessageEditReceipt Replace(Guid sessionId, MessageEditSubmission submission) => database.Write(transaction => {
    string hash = Hash(submission);
    MessageEditReceipt? retained = ReadReceipt(transaction, sessionId, submission.OperationId, hash);
    if (retained is not null)
      return retained;
    var target = ReadTarget(transaction, sessionId, submission.Message);
    using (SqliteCommand active = database.Command(transaction,
        "SELECT EXISTS(SELECT 1 FROM execution_runs WHERE session_id = $session AND outcome IS NULL);", ("$session", sessionId))) {
      if ((long)active.ExecuteScalar()! != 0)
        throw new InvalidOperationException("Editing must await execution settlement.");
    }

    RemoveSuffix(transaction, sessionId, target);
    long revision = checked(submission.Message.HistoryRevision + 1);
    RebuildPositions(transaction, sessionId, revision);
    var input = new UserMessageSubmission(submission.OperationId, revision, submission.Text, submission.LlmSelection);
    var admitted = messages.Admit(transaction, sessionId, revision, input, accepting: true, Guid.NewGuid());
    if (!admitted.NewlyAccepted || admitted.Receipt.Acceptance != MessageAcceptance.Accepted)
      throw new UserMessageConflictException(submission.OperationId);
    Guid executionId = admitted.Receipt.ExecutionId!.Value;
    using SqliteCommand save = database.Command(transaction, """
      INSERT INTO message_edits (session_id, operation_id, submission_hash, history_revision, run_id)
      VALUES ($session, $operation, $hash, $revision, $run);
      """, ("$session", sessionId), ("$operation", submission.OperationId), ("$hash", hash),
        ("$revision", revision), ("$run", executionId));
    save.ExecuteNonQuery();
    return new MessageEditReceipt(sessionId, submission.OperationId, revision, executionId);
  });

  private MessageEditReceipt? ReadReceipt(SqliteTransaction transaction, Guid sessionId, Guid operationId, string? hash) {
    using SqliteCommand command = database.Command(transaction, """
      SELECT submission_hash, history_revision, run_id FROM message_edits
      WHERE session_id = $session AND operation_id = $operation;
      """, ("$session", sessionId), ("$operation", operationId));
    using SqliteDataReader reader = command.ExecuteReader();
    if (!reader.Read())
      return null;
    if (hash is not null && hash != reader.GetString(0))
      throw new UserMessageConflictException(operationId);
    return new(sessionId, operationId, reader.GetInt64(1), reader.GetGuid(2));
  }

  private (long EntryOrdinal, long RunOrdinal, int RequestIndex, long InputOrdinal, long MessageIndex) ReadTarget(
      SqliteTransaction transaction, Guid sessionId, MessageRef message) {
    using SqliteCommand revision = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
    if (revision.ExecuteScalar() is not long actual)
      throw new KeyNotFoundException("The session does not exist.");
    if (actual != message.HistoryRevision)
      throw new HistoryRevisionConflictException(message.HistoryRevision, actual);
    using SqliteCommand target = database.Command(transaction, """
      SELECT m.ordinal, r.ordinal, q.request_index, i.ordinal,
          (SELECT count(*) FROM execution_inputs previous WHERE previous.request_id = m.request_id AND previous.ordinal < i.ordinal)
          + (SELECT count(*) FROM execution_tool_completions completed WHERE completed.claimed_request_id = m.request_id)
      FROM execution_messages m
      JOIN execution_runs r ON r.run_id = m.run_id
      JOIN execution_requests q ON q.request_id = m.request_id
      JOIN execution_inputs i ON i.session_id = m.session_id AND i.input_id = m.input_id
      WHERE m.session_id = $session AND m.history_revision = $revision
          AND m.run_id = $run AND m.message_id = $message AND m.role = 0;
      """, ("$session", sessionId), ("$revision", actual), ("$run", message.ExecutionId), ("$message", message.MessageId));
    using SqliteDataReader reader = target.ExecuteReader();
    if (!reader.Read())
      throw new KeyNotFoundException("The user message is no longer in this conversation.");
    return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4));
  }

  private void RemoveSuffix(SqliteTransaction transaction, Guid sessionId,
      (long EntryOrdinal, long RunOrdinal, int RequestIndex, long InputOrdinal, long MessageIndex) target) {
    // Automatic compaction precedes user placement. Its earlier marker can still
    // summarize an edited input in a multi-input request; compare source positions too.
    using SqliteCommand summaries = database.Command(transaction, $"""
      UPDATE execution_requests SET compaction_id = NULL WHERE compaction_id IN ({ObsoleteSummaries});
      DELETE FROM session_compactions WHERE compaction_id IN ({ObsoleteSummaries});
      DELETE FROM execution_messages WHERE session_id = $session AND (ordinal >= $entry
          OR (role = 4 AND NOT EXISTS (SELECT 1 FROM session_compactions c
              WHERE c.compaction_id = execution_messages.message_id)));
      UPDATE input_decisions SET original_text_utf8 = X'', superseded = 1
      WHERE session_id = $session AND input_id IN (
          SELECT input_id FROM execution_inputs WHERE session_id = $session AND ordinal >= $input);
      DELETE FROM execution_inputs WHERE session_id = $session AND ordinal >= $input;
      UPDATE execution_runs SET error = NULL, compaction_focus = NULL
      WHERE session_id = $session AND ordinal >= $runOrdinal;
      """, ("$session", sessionId), ("$entry", target.EntryOrdinal),
        ("$input", target.InputOrdinal), ("$runOrdinal", target.RunOrdinal),
        ("$requestIndex", target.RequestIndex), ("$messageIndex", target.MessageIndex));
    summaries.ExecuteNonQuery();

    using SqliteCommand responses = database.Command(transaction, $"""
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
    responses.ExecuteNonQuery();
  }

  private void RebuildPositions(SqliteTransaction transaction, Guid sessionId, long revision) {
    // Preserve message IDs in the kept prefix, but invalidate every old cursor and layout index.
    using SqliteCommand command = database.Command(transaction, """
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
    command.ExecuteNonQuery();
  }

  private static string Hash(MessageEditSubmission submission) =>
      Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(submission)));
}
