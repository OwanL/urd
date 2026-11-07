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
        ValidateCommittedTail(cursor, metadata.SegmentCount);
        return ReadSegments(transaction, sessionId, cursor, metadata, maximumUtf16Units);
    }

    private static void ValidateCursorAndLimit(Guid sessionId, MessageContentCursor cursor, int maximumUtf16Units) {
        // Check public cursors and stored preview identities before reading content.
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(cursor.Message);
        MessageRef message = cursor.Message;
        if (sessionId == Guid.Empty || message.RunId == Guid.Empty || message.MessageId == Guid.Empty
            || !Enum.IsDefined(message.Role)) {
            throw new ArgumentException("Content requires a session and a valid message reference.", nameof(cursor));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(message.HistoryRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor.SegmentIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor.Utf8Offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cursor.Utf8Offset, StorageText.MaximumBytes);
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
        using SqliteCommand metadata = database.Command(transaction, """
            SELECT r.history_revision, r.input_id, r.stopping, r.outcome,
                q.request_id, q.output_id, q.next_batch_index, q.output_final, q.outcome
            FROM execution_runs r LEFT JOIN execution_requests q ON q.run_id = r.run_id
            WHERE r.session_id = $session AND r.run_id = $run;
            """,
            ("$session", sessionId),
            ("$run", message.RunId));
        using SqliteDataReader reader = metadata.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != message.HistoryRevision)
            throw new KeyNotFoundException("The message does not belong to this session history.");

        // Run columns name the SELECT in order. Request columns are NULL until a
        // request exists, so the assistant branch reads the rest only when present.
        Guid inputId = reader.GetGuid(1);
        bool stopRequested = reader.GetInt32(2) != 0;
        bool runSettled = !reader.IsDBNull(3);
        Guid? requestId = reader.IsDBNull(4) ? null : reader.GetGuid(4);

        if (message.Role == MessageRole.User) {
            if (inputId != message.MessageId)
                throw new KeyNotFoundException("The input message does not belong to this run.");

            return new(inputId, requestId, SegmentCount: 1, IsClosed: true, FinalOutputCommitted: false);
        }

        Guid? outputId = reader.IsDBNull(5) ? null : reader.GetGuid(5);
        if (outputId != message.MessageId)
            throw new KeyNotFoundException("The output message does not belong to this run.");

        long segmentCount = reader.GetInt64(6);
        bool finalOutputCommitted = reader.GetInt32(7) != 0;
        bool requestEnded = !reader.IsDBNull(8);
        if (requestId is null || segmentCount <= 0)
            throw new InvalidDataException("An output message has no committed segments.");

        bool isClosed = finalOutputCommitted || stopRequested || runSettled || requestEnded;
        return new(inputId, requestId, segmentCount, isClosed, finalOutputCommitted);
    }

    private static void ValidateCommittedTail(MessageContentCursor cursor, long segmentCount) {
        if (cursor.SegmentIndex > segmentCount || (cursor.SegmentIndex == segmentCount && cursor.Utf8Offset != 0))
            throw new ArgumentException("The cursor is beyond the committed message tail.", nameof(cursor));
    }

    private MessageContentPage ReadSegments(SqliteTransaction transaction, Guid sessionId, MessageContentCursor cursor,
        MessageMetadata metadata, int maximumUtf16Units) {
        var text = new StringBuilder();
        long segmentIndex = cursor.SegmentIndex;
        int utf8Offset = cursor.Utf8Offset;
        int utf8Bytes = 0;
        for (int examined = 0; segmentIndex < metadata.SegmentCount && examined < StorageText.MaximumSegments; examined++) {
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
        var availableThrough = new MessageContentCursor(cursor.Message, metadata.SegmentCount, 0);
        return new(cursor, text.ToString(), next, availableThrough, segmentIndex < metadata.SegmentCount,
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
                ("$input", metadata.InputId),
                ("$offset", utf8Offset),
                ("$count", prefixBytes))
            : database.Command(transaction, """
                SELECT length(text_utf8), coalesce(substr(text_utf8, $offset + 1, $count), X'')
                FROM execution_output WHERE request_id = $request AND batch_index = $segment;
                """,
                ("$request", metadata.RequestId),
                ("$segment", segmentIndex),
                ("$offset", utf8Offset),
                ("$count", prefixBytes));
        using SqliteDataReader reader = segmentRead.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException("A committed message segment is missing.");

        long segmentByteLength = reader.GetInt64(0);
        if (segmentByteLength > StorageText.MaximumBytes)
            throw new InvalidDataException("A message segment exceeds the storage limit.");

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
        Guid InputId, Guid? RequestId, long SegmentCount, bool IsClosed, bool FinalOutputCommitted);
}
