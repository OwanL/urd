using System.Globalization;
using Microsoft.Data.Sqlite;
using Urd.Core.Sessions;

namespace Urd.Core.Storage;

// The partial index skips sent history, including old runs, without scanning it.
internal sealed class NotSentInputReader(Database database) {
  internal const string PageSql = """
    SELECT i.input_id, i.run_id, i.ordinal, coalesce(substr(d.original_text_utf8, 1, 771), X'')
    FROM execution_inputs i
    JOIN input_decisions d ON d.session_id = i.session_id AND d.input_id = i.input_id
    WHERE i.session_id = $session AND i.not_sent = 1 AND i.ordinal < $cursor
    ORDER BY i.ordinal DESC LIMIT 61;
    """;

  internal NotSentPage Read(Guid sessionId, string? cursor) {
    long boundary = long.MaxValue;
    if (cursor is not null && (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out boundary)
        || boundary <= 0))
      throw new ArgumentException("Not-sent cursor must be a positive Int64 decimal string.", nameof(cursor));

    return database.Read(transaction => {
      using SqliteCommand command = database.Command(transaction, PageSql,
          ("$session", sessionId), ("$cursor", boundary));
      using SqliteDataReader reader = command.ExecuteReader();
      var inputs = new List<NotSentInput>(60);
      long lastOrdinal = 0;
      while (inputs.Count < 60 && reader.Read()) {
        inputs.Add(new(reader.GetGuid(0), reader.GetGuid(1), SnapshotReader.ReadPreview((byte[])reader.GetValue(3))));
        lastOrdinal = reader.GetInt64(2);
      }

      string? nextCursor = reader.Read() ? lastOrdinal.ToString(CultureInfo.InvariantCulture) : null;
      return new NotSentPage(inputs, nextCursor);
    });
  }
}
