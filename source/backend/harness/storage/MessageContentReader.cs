using System.Buffers;
using System.Text;
using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// Reads bounded message content without splitting UTF-8 scalars.
internal sealed class MessageContentReader
{
    private readonly Database _database;

    internal MessageContentReader(Database database)
    {
        _database = database;
    }

    internal MessageContentPage Read(Guid sessionId, MessageContentCursor cursor)
        => _database.Read(transaction => Read(transaction, sessionId, cursor,
            StorageText.MaximumBytes, int.MaxValue, StorageText.MaximumSegments));

    // Snapshots use their existing read transaction and smaller preview budget.
    internal MessageContentPage Read(SqliteTransaction transaction, Guid sessionId, MessageContentCursor cursor,
        int maximumUtf8Bytes, int maximumUtf16Units, int maximumSegments)
    {
        ValidateCursorAndLimits(sessionId, cursor, maximumUtf8Bytes, maximumUtf16Units, maximumSegments);
        CheckHistoryRevision(transaction, sessionId, cursor.Message.HistoryRevision);
        MessageMetadata metadata = ReadMetadata(transaction, sessionId, cursor.Message);
        ValidateCommittedTail(cursor, metadata.SegmentCount);
        return ReadSegments(transaction, sessionId, cursor, metadata,
            maximumUtf8Bytes, maximumUtf16Units, maximumSegments);
    }

    private static void ValidateCursorAndLimits(Guid sessionId, MessageContentCursor cursor,
        int maximumUtf8Bytes, int maximumUtf16Units, int maximumSegments)
    {
        // Cursors come from public session APIs.
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(cursor.Message);
        MessageRef message = cursor.Message;
        if (sessionId == Guid.Empty || message.RunId == Guid.Empty || message.MessageId == Guid.Empty
            || !Enum.IsDefined(message.Role))
        {
            throw new ArgumentException("Content requires a session and a valid message reference.", nameof(cursor));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(message.HistoryRevision);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor.SegmentIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(cursor.Utf8Offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cursor.Utf8Offset, StorageText.MaximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumUtf8Bytes, StorageText.MaximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumUtf16Units);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegments);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumSegments, StorageText.MaximumSegments);
    }

    private void CheckHistoryRevision(SqliteTransaction transaction, Guid sessionId, long historyRevision)
    {
        using SqliteCommand revision = _database.Command(transaction,
            "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId));
        object? value = revision.ExecuteScalar();
        if (value is null)
        {
            throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
        }

        if ((long)value != historyRevision)
        {
            throw new HistoryRevisionConflictException(historyRevision, (long)value);
        }
    }

    private MessageMetadata ReadMetadata(SqliteTransaction transaction, Guid sessionId, MessageRef message)
    {
        using SqliteCommand metadata = _database.Command(transaction, """
            SELECT r.history_revision, r.input_id, r.stopping, r.outcome,
                q.request_id, q.output_id, q.next_batch_index, q.output_final, q.outcome
            FROM execution_runs r LEFT JOIN execution_requests q ON q.run_id = r.run_id
            WHERE r.session_id = $session AND r.run_id = $run;
            """,
            ("$session", sessionId),
            ("$run", message.RunId));
        using SqliteDataReader reader = metadata.ExecuteReader();
        if (!reader.Read() || reader.GetInt64(0) != message.HistoryRevision)
        {
            throw new KeyNotFoundException("The message does not belong to this session history.");
        }

        Guid inputId = reader.GetGuid(1);
        Guid? requestId = reader.IsDBNull(4) ? null : reader.GetGuid(4);
        return message.Role == MessageRole.User
            ? InputMetadata(message, inputId, requestId)
            : OutputMetadata(reader, message, inputId, requestId);
    }

    private static MessageMetadata InputMetadata(MessageRef message, Guid inputId, Guid? requestId)
    {
        if (inputId != message.MessageId)
        {
            throw new KeyNotFoundException("The input message does not belong to this run.");
        }

        return new(inputId, requestId, 1, true, false);
    }

    private static MessageMetadata OutputMetadata(SqliteDataReader reader, MessageRef message,
        Guid inputId, Guid? requestId)
    {
        if (reader.IsDBNull(5) || reader.GetGuid(5) != message.MessageId)
        {
            throw new KeyNotFoundException("The output message does not belong to this run.");
        }

        long segmentCount = reader.GetInt64(6);
        bool finalOutputCommitted = reader.GetInt32(7) != 0;
        bool isClosed = finalOutputCommitted || reader.GetInt32(2) != 0 || !reader.IsDBNull(3) || !reader.IsDBNull(8);
        if (requestId is null || segmentCount <= 0)
        {
            throw new InvalidDataException("An output message has no committed segments.");
        }

        return new(inputId, requestId, segmentCount, isClosed, finalOutputCommitted);
    }

    private static void ValidateCommittedTail(MessageContentCursor cursor, long segmentCount)
    {
        if (cursor.SegmentIndex > segmentCount || (cursor.SegmentIndex == segmentCount && cursor.Utf8Offset != 0))
        {
            throw new ArgumentException("The cursor is beyond the committed message tail.", nameof(cursor));
        }
    }

    private MessageContentPage ReadSegments(SqliteTransaction transaction, Guid sessionId, MessageContentCursor cursor,
        MessageMetadata metadata, int maximumUtf8Bytes, int maximumUtf16Units, int maximumSegments)
    {
        var text = new StringBuilder();
        long segment = cursor.SegmentIndex;
        int offset = cursor.Utf8Offset, utf8Bytes = 0;
        for (int examined = 0; segment < metadata.SegmentCount && examined < maximumSegments; examined++)
        {
            // Exhausted text budgets still permit length probes to normalize empty final segments.
            // These probes share the same segment budget.
            bool textBudgetExhausted = utf8Bytes == maximumUtf8Bytes || text.Length == maximumUtf16Units;
            int prefixBytes = textBudgetExhausted ? 0 : (int)Math.Min(StorageText.MaximumBytes,
                Math.Min(maximumUtf8Bytes - utf8Bytes, (long)(maximumUtf16Units - text.Length) * 3) + 3);
            (long length, byte[] prefix) = ReadSegment(transaction, sessionId, cursor, metadata,
                segment, offset, prefixBytes);
            offset += AppendPrefix(prefix, text, ref utf8Bytes, maximumUtf8Bytes, maximumUtf16Units);
            if (offset != length)
            {
                break; // A byte/scalar/UTF-16 budget ended within this immutable segment.
            }

            segment++;
            offset = 0;
        }

        var next = new MessageContentCursor(cursor.Message, segment, offset);
        var availableThrough = new MessageContentCursor(cursor.Message, metadata.SegmentCount, 0);
        return new(cursor, text.ToString(), next, availableThrough, segment < metadata.SegmentCount,
            metadata.IsClosed, metadata.FinalOutputCommitted);
    }

    private (long Length, byte[] Prefix) ReadSegment(SqliteTransaction transaction, Guid sessionId,
        MessageContentCursor cursor, MessageMetadata metadata, long segment, int offset, int prefixBytes)
    {
        // SQLite substr of an empty BLOB returns NULL; retain its empty-byte meaning.
        using SqliteCommand segmentRead = cursor.Message.Role == MessageRole.User
            ? _database.Command(transaction, """
                SELECT length(original_text_utf8), coalesce(substr(original_text_utf8, $offset + 1, $count), X'')
                FROM input_decisions WHERE session_id = $session AND input_id = $input AND decision = 1;
                """,
                ("$session", sessionId),
                ("$input", metadata.InputId),
                ("$offset", offset),
                ("$count", prefixBytes))
            : _database.Command(transaction, """
                SELECT length(text_utf8), coalesce(substr(text_utf8, $offset + 1, $count), X'')
                FROM execution_output WHERE request_id = $request AND batch_index = $segment;
                """,
                ("$request", metadata.RequestId),
                ("$segment", segment),
                ("$offset", offset),
                ("$count", prefixBytes));
        using SqliteDataReader reader = segmentRead.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("A committed message segment is missing.");
        }

        long length = reader.GetInt64(0);
        if (length > StorageText.MaximumBytes)
        {
            throw new InvalidDataException("A message segment exceeds the storage limit.");
        }

        if (offset > length)
        {
            throw new ArgumentException("The cursor offset is beyond its segment.", nameof(cursor));
        }

        byte[] prefix = (byte[])reader.GetValue(1);
        if (offset != 0 && prefix.Length != 0 && (prefix[0] & 0xc0) == 0x80)
        {
            throw new ArgumentException("The cursor must begin at a UTF-8 scalar boundary.", nameof(cursor));
        }

        return (length, prefix);
    }

    private static int AppendPrefix(byte[] prefix, StringBuilder text, ref int utf8Bytes,
        int maximumUtf8Bytes, int maximumUtf16Units)
    {
        int consumed = 0;
        Span<char> scalar = stackalloc char[2];
        while (consumed < prefix.Length && text.Length < maximumUtf16Units && utf8Bytes < maximumUtf8Bytes)
        {
            if (Rune.DecodeFromUtf8(prefix.AsSpan(consumed), out Rune rune, out int size) != OperationStatus.Done)
            {
                throw new InvalidDataException("A message segment contains invalid UTF-8.");
            }

            if (size > maximumUtf8Bytes - utf8Bytes || rune.Utf16SequenceLength > maximumUtf16Units - text.Length)
            {
                break;
            }

            int units = rune.EncodeToUtf16(scalar);
            text.Append(scalar[..units]);
            utf8Bytes += size;
            consumed += size;
        }

        return consumed;
    }

    // Describes committed message segments and their completion state.
    private sealed record MessageMetadata(
        Guid InputId, Guid? RequestId, long SegmentCount, bool IsClosed, bool FinalOutputCommitted);
}
