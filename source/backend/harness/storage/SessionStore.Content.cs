using System.Buffers;
using System.Text;
using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

public sealed partial class SessionStore
{
    internal MessageContentPage ReadMessageContent(Guid sessionId, MessageContentCursor cursor)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            MessageContentPage page = ReadMessageContent(transaction, sessionId, cursor, MaximumOutputBytes, int.MaxValue, 16);
            transaction.Commit();
            return page;
        }
    }

    // Also used by the snapshot projection inside its existing read transaction.
    private MessageContentPage ReadMessageContent(SqliteTransaction transaction, Guid sessionId, MessageContentCursor cursor,
        int maximumUtf8Bytes, int maximumUtf16Units, int maximumSegments)
    {
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
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cursor.Utf8Offset, MaximumOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumUtf8Bytes, MaximumOutputBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumUtf16Units);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegments);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumSegments, 16);

        using (SqliteCommand revision = CreateCommand(transaction,
            "SELECT history_revision FROM sessions WHERE session_id = $session;", ("$session", sessionId)))
        {
            object? value = revision.ExecuteScalar();
            if (value is null)
            {
                throw new KeyNotFoundException($"Session '{sessionId}' does not exist.");
            }

            if ((long)value != message.HistoryRevision)
            {
                throw new HistoryRevisionConflictException(message.HistoryRevision, (long)value);
            }
        }

        Guid inputId;
        Guid? requestId;
        long segmentCount;
        bool finalOutputCommitted, isClosed;
        using (SqliteCommand metadata = CreateCommand(transaction, """
            SELECT r.history_revision, r.input_id, r.stopping, r.outcome,
                q.request_id, q.output_id, q.next_batch_index, q.output_final, q.outcome
            FROM execution_runs r LEFT JOIN execution_requests q ON q.run_id = r.run_id
            WHERE r.session_id = $session AND r.run_id = $run;
            """, ("$session", sessionId), ("$run", message.RunId)))
        using (SqliteDataReader reader = metadata.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt64(0) != message.HistoryRevision)
            {
                throw new KeyNotFoundException("The message does not belong to this session history.");
            }

            inputId = reader.GetGuid(1);
            requestId = reader.IsDBNull(4) ? null : reader.GetGuid(4);
            if (message.Role == MessageRole.User)
            {
                if (inputId != message.MessageId)
                {
                    throw new KeyNotFoundException("The input message does not belong to this run.");
                }

                segmentCount = 1;
                finalOutputCommitted = false;
                isClosed = true;
            }
            else
            {
                if (reader.IsDBNull(5) || reader.GetGuid(5) != message.MessageId)
                {
                    throw new KeyNotFoundException("The output message does not belong to this run.");
                }

                segmentCount = reader.GetInt64(6);
                finalOutputCommitted = reader.GetInt32(7) != 0;
                isClosed = finalOutputCommitted || reader.GetInt32(2) != 0 || !reader.IsDBNull(3) || !reader.IsDBNull(8);
                if (requestId is null || segmentCount <= 0)
                {
                    throw new InvalidDataException("An output message has no committed segments.");
                }
            }
        }

        if (cursor.SegmentIndex > segmentCount || (cursor.SegmentIndex == segmentCount && cursor.Utf8Offset != 0))
        {
            throw new ArgumentException("The cursor is beyond the committed message tail.", nameof(cursor));
        }

        var text = new StringBuilder();
        long segment = cursor.SegmentIndex;
        int offset = cursor.Utf8Offset, utf8Bytes = 0;
        Span<char> scalar = stackalloc char[2];
        for (int examined = 0; segment < segmentCount && examined < maximumSegments; examined++)
        {
            // At exhausted text budgets, inspect lengths only so empty final segments
            // still normalize the tail. These probes share the same segment budget.
            bool textBudgetExhausted = utf8Bytes == maximumUtf8Bytes || text.Length == maximumUtf16Units;
            int prefixBytes = textBudgetExhausted ? 0 : (int)Math.Min(MaximumOutputBytes,
                Math.Min(maximumUtf8Bytes - utf8Bytes, (long)(maximumUtf16Units - text.Length) * 3) + 3);
            // SQLite substr of an empty BLOB returns NULL; retain its empty-byte meaning.
            using SqliteCommand segmentRead = message.Role == MessageRole.User
                ? CreateCommand(transaction, """
                    SELECT length(original_text_utf8), coalesce(substr(original_text_utf8, $offset + 1, $count), X'')
                    FROM input_decisions WHERE session_id = $session AND input_id = $input AND decision = 1;
                    """, ("$session", sessionId), ("$input", inputId), ("$offset", offset), ("$count", prefixBytes))
                : CreateCommand(transaction, """
                    SELECT length(text_utf8), coalesce(substr(text_utf8, $offset + 1, $count), X'')
                    FROM execution_output WHERE request_id = $request AND batch_index = $segment;
                    """, ("$request", requestId), ("$segment", segment), ("$offset", offset), ("$count", prefixBytes));
            using SqliteDataReader reader = segmentRead.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidDataException("A committed message segment is missing.");
            }

            long length = reader.GetInt64(0);
            if (length > MaximumOutputBytes)
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

            int consumed = 0;
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
                offset += size;
            }

            if (offset != length)
            {
                break; // A byte/scalar/UTF-16 budget ended within this immutable segment.
            }

            segment++;
            offset = 0;
        }

        var next = new MessageContentCursor(message, segment, offset);
        var availableThrough = new MessageContentCursor(message, segmentCount, 0);
        return new(cursor, text.ToString(), next, availableThrough, segment < segmentCount, isClosed, finalOutputCommitted);
    }
}
