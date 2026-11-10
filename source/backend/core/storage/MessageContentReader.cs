using System.Buffers;
using System.Text;
using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Reads bounded message content without splitting UTF-8 scalars.
internal sealed class MessageContentReader {
  private readonly Database database;

  internal MessageContentReader(Database database) {
    this.database = database;
  }

  internal MessageContentPage Read(Guid sessionId, MessageContentCursor cursor) {
    return database.Read(transaction => {
      ValidateCursorAndLimit(sessionId, cursor, int.MaxValue);
      CheckHistoryRevision(transaction, sessionId, cursor.Message.HistoryRevision);
      return ReadValidated(transaction, sessionId, cursor, int.MaxValue);
    });
  }

  // Called under the session gate. Resolve only fixed metadata, never source bytes.
  internal MessageContentExtent CaptureClosedSourceExtent(Guid sessionId, MessageRef message) {
    var cursor = new MessageContentCursor(message, 0, 0);
    ValidateCursorAndLimit(sessionId, cursor, int.MaxValue);
    return database.Read(transaction => {
      CheckHistoryRevision(transaction, sessionId, message.HistoryRevision);
      MessageMetadata metadata = ReadMetadata(transaction, sessionId, message);
      if (!metadata.IsClosed)
        throw new InvalidOperationException("Display source requires closed message content.");

      return new MessageContentExtent(sessionId, NormalizeCursor(cursor, metadata.FirstChunkIndex),
          new(message, metadata.EndChunkIndex, 0), metadata.IsClosed, metadata.FinalOutputCommitted,
          metadata.SubmissionId, metadata.RequestId);
    });
  }

  // The captured end, not a fresh request tail, bounds every read. Only a bounded
  // BLOB prefix is copied under the database lock; decoding happens in the caller.
  internal MessageContentSegment ReadSourceSegment(MessageContentExtent extent, MessageContentCursor cursor) {
    ValidateCursorAndLimit(extent.SessionId, cursor, int.MaxValue);
    if (cursor.Message != extent.From.Message)
      throw new ArgumentException("The cursor does not belong to the captured message.", nameof(cursor));
    ValidateCommittedTail(cursor, extent.From.SegmentIndex, extent.AvailableThrough.SegmentIndex);
    if (cursor.SegmentIndex == extent.AvailableThrough.SegmentIndex)
      throw new ArgumentException("The cursor is at the captured message end.", nameof(cursor));

    return database.Read(transaction => {
      var metadata = new MessageMetadata(extent.SubmissionId, extent.RequestId, extent.From.SegmentIndex,
          extent.AvailableThrough.SegmentIndex, extent.IsClosed, extent.FinalOutputCommitted);
      var segment = ReadSegment(transaction, extent.SessionId, cursor, metadata,
          cursor.SegmentIndex, cursor.Utf8Offset, StorageText.MaximumBytes + 3);
      return new MessageContentSegment(segment.ByteLength, segment.Prefix);
    });
  }

  // Called under the session gate before publishing the assembled source. Later
  // appends may extend the request, but cannot change the captured identity/bounds.
  internal void ValidateSourceExtent(MessageContentExtent extent) {
    database.Read(transaction => {
      CheckHistoryRevision(transaction, extent.SessionId, extent.From.Message.HistoryRevision);
      MessageMetadata metadata = ReadMetadata(transaction, extent.SessionId, extent.From.Message);
      if (metadata.SubmissionId != extent.SubmissionId || metadata.RequestId != extent.RequestId
          || metadata.FirstChunkIndex != extent.From.SegmentIndex
          || metadata.EndChunkIndex < extent.AvailableThrough.SegmentIndex || !metadata.IsClosed)
        throw new InvalidOperationException("The captured message source is no longer available.");
      return true;
    });
  }

  // SnapshotReader already selected messages from the current revision in this
  // transaction. Do not query that same revision again for every preview.
  internal MessageContentPage ReadPreview(SqliteTransaction transaction, Guid sessionId, MessageRef message,
      int maximumUtf16Units) {
    var cursor = new MessageContentCursor(message, 0, 0);
    ValidateCursorAndLimit(sessionId, cursor, maximumUtf16Units);
    return ReadValidated(transaction, sessionId, cursor, maximumUtf16Units);
  }

  private MessageContentPage ReadValidated(SqliteTransaction transaction, Guid sessionId, MessageContentCursor cursor,
      int maximumUtf16Units) {
    MessageMetadata metadata = ReadMetadata(transaction, sessionId, cursor.Message);
    cursor = NormalizeCursor(cursor, metadata.FirstChunkIndex);
    ValidateCommittedTail(cursor, metadata.FirstChunkIndex, metadata.EndChunkIndex);
    return ReadSegments(transaction, sessionId, cursor, metadata, maximumUtf16Units);
  }

  private static void ValidateCursorAndLimit(Guid sessionId, MessageContentCursor cursor, int maximumUtf16Units) {
    // Check public cursors and stored preview identities before reading content.
    ArgumentNullException.ThrowIfNull(cursor);
    ArgumentNullException.ThrowIfNull(cursor.Message);
    MessageRef message = cursor.Message;
    if (sessionId == Guid.Empty || message.ExecutionId == Guid.Empty || message.MessageId == Guid.Empty
        || !Enum.IsDefined(message.Role)) {
      throw new ArgumentException("Content requires a session and a valid message reference.", nameof(cursor));
    }

    ArgumentOutOfRangeException.ThrowIfNegative(message.HistoryRevision);
    ArgumentOutOfRangeException.ThrowIfNegative(cursor.SegmentIndex);
    ArgumentOutOfRangeException.ThrowIfNegative(cursor.Utf8Offset);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumUtf16Units);
  }

  private void CheckHistoryRevision(SqliteTransaction transaction, Guid sessionId, long historyRevision) {
    using SqliteCommand revision = database.Command(transaction,
        "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
    object? value = revision.ExecuteScalar();
    if (value is null)
      throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");

    if ((long)value != historyRevision)
      throw new HistoryRevisionConflictException(historyRevision, (long)value);
  }

  private MessageMetadata ReadMetadata(SqliteTransaction transaction, Guid sessionId, MessageRef message) {
    // Resolve the exact indexed identity, never the first request or submission of an execution.
    string sourceSql = message.Role == MessageRole.User ? """
      SELECT i.input_id, m.request_id
      FROM execution_messages m
      JOIN execution_runs r ON r.run_id = m.run_id AND r.session_id = m.session_id
          AND r.history_revision = m.history_revision
      JOIN execution_inputs i ON i.session_id = m.session_id AND i.run_id = m.run_id
          AND i.input_id = m.input_id AND i.input_id = m.message_id
      WHERE m.session_id = $session AND m.run_id = $run AND m.role = $role
          AND m.message_id = $message AND m.history_revision = $revision;
      """ : """
      SELECT q.request_id, m.first_chunk_index, coalesce(m.end_chunk_index, q.next_chunk_index),
          m.end_chunk_index, q.response_final, q.outcome, r.stopping, r.outcome, q.next_chunk_index
      FROM execution_messages m
      JOIN execution_runs r ON r.run_id = m.run_id AND r.session_id = m.session_id
          AND r.history_revision = m.history_revision
      JOIN execution_requests q ON q.request_id = m.request_id AND q.run_id = m.run_id
      WHERE m.session_id = $session AND m.run_id = $run AND m.role = $role
          AND m.message_id = $message AND m.history_revision = $revision;
      """;
    using SqliteCommand metadata = database.Command(transaction, sourceSql,
        ("$session", sessionId),
        ("$run", message.ExecutionId),
        ("$role", (int)message.Role),
        ("$message", message.MessageId),
        ("$revision", message.HistoryRevision));
    using SqliteDataReader reader = metadata.ExecuteReader();
    if (!reader.Read())
      throw new KeyNotFoundException("The message does not belong to this session history.");

    if (message.Role == MessageRole.User) {
      return new(reader.GetGuid(0), reader.GetGuid(1), FirstChunkIndex: 0, EndChunkIndex: 1, IsClosed: true,
          FinalOutputCommitted: false);
    }

    Guid requestId = reader.GetGuid(0);
    long firstChunkIndex = reader.GetInt64(1);
    long endChunkIndex = reader.GetInt64(2);
    bool runClosed = !reader.IsDBNull(3);
    bool finalOutputCommitted = reader.GetInt32(4) != 0
        && endChunkIndex == reader.GetInt64(8);
    bool requestEnded = !reader.IsDBNull(5);
    bool stopRequested = reader.GetInt32(6) != 0;
    bool executionSettled = !reader.IsDBNull(7);
    if (firstChunkIndex < 0 || endChunkIndex <= firstChunkIndex)
      throw new InvalidDataException("An output message has an invalid committed range.");

    bool isClosed = runClosed || finalOutputCommitted || stopRequested || executionSettled || requestEnded;
    return new(Guid.Empty, requestId, firstChunkIndex, endChunkIndex, isClosed, finalOutputCommitted);
  }

  private static MessageContentCursor NormalizeCursor(MessageContentCursor cursor, long firstChunkIndex) =>
      cursor.SegmentIndex == 0 && firstChunkIndex > 0 && cursor.Utf8Offset == 0
          ? cursor with { SegmentIndex = firstChunkIndex }
          : cursor;

  private static void ValidateCommittedTail(MessageContentCursor cursor, long firstChunkIndex, long endChunkIndex) {
    if (cursor.SegmentIndex < firstChunkIndex || cursor.SegmentIndex > endChunkIndex
        || (cursor.SegmentIndex == endChunkIndex && cursor.Utf8Offset != 0))
      throw new ArgumentException("The cursor is outside the committed message range.", nameof(cursor));
  }

  private MessageContentPage ReadSegments(SqliteTransaction transaction, Guid sessionId, MessageContentCursor cursor,
      MessageMetadata metadata, int maximumUtf16Units) {
    var text = new StringBuilder();
    long segmentIndex = cursor.SegmentIndex;
    int utf8Offset = cursor.Utf8Offset;
    int utf8Bytes = 0;
    for (int examined = 0; segmentIndex < metadata.EndChunkIndex && examined < StorageText.MaximumSegments; examined++) {
      // Exhausted text budgets still permit length probes to normalize empty final segments.
      // These probes share the same segment budget.
      int remainingBytes = StorageText.MaximumBytes - utf8Bytes;
      int remainingUtf16Units = maximumUtf16Units - text.Length;
      bool textBudgetExhausted = remainingBytes == 0 || remainingUtf16Units == 0;
      // A UTF-16 unit needs at most three UTF-8 bytes. Three lookahead bytes
      // let the decoder finish a scalar that starts at the budget boundary.
      int prefixBytes = textBudgetExhausted ? 0 : (int)Math.Min(StorageText.MaximumBytes,
          Math.Min(remainingBytes, (long)remainingUtf16Units * 3) + 3);
      (long segmentByteLength, byte[] prefix) = ReadSegment(transaction, sessionId, cursor, metadata,
          segmentIndex, utf8Offset, prefixBytes);
      int appendedBytes = AppendPrefix(prefix, text, remainingBytes, maximumUtf16Units);
      utf8Offset += appendedBytes;
      utf8Bytes += appendedBytes;
      if (utf8Offset != segmentByteLength) {
        break; // A byte/scalar/UTF-16 budget ended within this immutable segment.
      }

      segmentIndex++;
      utf8Offset = 0;
    }

    var next = new MessageContentCursor(cursor.Message, segmentIndex, utf8Offset);
    var availableThrough = new MessageContentCursor(cursor.Message, metadata.EndChunkIndex, 0);
    return new(cursor, text.ToString(), next, availableThrough, segmentIndex < metadata.EndChunkIndex,
        metadata.IsClosed, metadata.FinalOutputCommitted);
  }

  private (long ByteLength, byte[] Prefix) ReadSegment(SqliteTransaction transaction, Guid sessionId,
      MessageContentCursor cursor, MessageMetadata metadata, long segmentIndex, int utf8Offset, int prefixBytes) {
    // SQLite substr of an empty BLOB returns NULL; retain its empty-byte meaning.
    using SqliteCommand segmentRead = cursor.Message.Role == MessageRole.User
        ? database.Command(transaction, """
          SELECT length(original_text_utf8), coalesce(substr(original_text_utf8, $offset + 1, $count), X'')
          FROM input_decisions WHERE session_id = $session AND input_id = $input AND decision = 1;
          """,
            ("$session", sessionId),
            ("$input", metadata.SubmissionId),
            ("$offset", utf8Offset),
            ("$count", prefixBytes))
        : database.Command(transaction, """
          SELECT length(text_utf8), coalesce(substr(text_utf8, $offset + 1, $count), X'')
          FROM execution_output WHERE request_id = $request AND chunk_index = $segment;
          """,
            ("$request", metadata.RequestId),
            ("$segment", segmentIndex),
            ("$offset", utf8Offset),
            ("$count", prefixBytes));
    using SqliteDataReader reader = segmentRead.ExecuteReader();
    if (!reader.Read())
      throw new InvalidDataException("A committed message segment is missing.");

    long segmentByteLength = reader.GetInt64(0);
    if (cursor.Message.Role == MessageRole.Assistant && segmentByteLength > StorageText.MaximumBytes)
      throw new InvalidDataException("An output segment exceeds the storage limit.");

    if (utf8Offset > segmentByteLength)
      throw new ArgumentException("The cursor offset is beyond its segment.", nameof(cursor));

    byte[] prefix = (byte[])reader.GetValue(1);
    if (utf8Offset != 0 && prefix.Length != 0 && (prefix[0] & 0xc0) == 0x80)
      throw new ArgumentException("The cursor must begin at a UTF-8 scalar boundary.", nameof(cursor));

    return (segmentByteLength, prefix);
  }

  private static int AppendPrefix(byte[] prefix, StringBuilder text, int maximumUtf8Bytes, int maximumUtf16Units) {
    int consumed = 0;
    Span<char> scalar = stackalloc char[2];
    while (consumed < prefix.Length && text.Length < maximumUtf16Units && consumed < maximumUtf8Bytes) {
      if (Rune.DecodeFromUtf8(prefix.AsSpan(consumed), out Rune rune, out int scalarBytes) != OperationStatus.Done)
        throw new InvalidDataException("A message segment contains invalid UTF-8.");

      if (scalarBytes > maximumUtf8Bytes - consumed || rune.Utf16SequenceLength > maximumUtf16Units - text.Length)
        break;

      int units = rune.EncodeToUtf16(scalar);
      text.Append(scalar[..units]);
      consumed += scalarBytes;
    }

    return consumed;
  }

  // Describes committed message segments and their completion state.
  private sealed record MessageMetadata(
      Guid SubmissionId, Guid RequestId, long FirstChunkIndex, long EndChunkIndex,
      bool IsClosed, bool FinalOutputCommitted);
}
