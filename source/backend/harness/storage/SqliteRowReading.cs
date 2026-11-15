using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Reads stored values by column name without exposing SQLite column positions to callers.
internal static class SqliteRowReading {
  internal static Guid Guid(this SqliteDataReader row, string column) => row.GetGuid(row.GetOrdinal(column));
  internal static Guid? NullableGuid(this SqliteDataReader row, string column) =>
      row.IsDBNull(row.GetOrdinal(column)) ? null : row.Guid(column);

  internal static long Long(this SqliteDataReader row, string column) => row.GetInt64(row.GetOrdinal(column));
  internal static long? NullableLong(this SqliteDataReader row, string column) =>
      row.IsDBNull(row.GetOrdinal(column)) ? null : row.Long(column);

  internal static int Int(this SqliteDataReader row, string column) => row.GetInt32(row.GetOrdinal(column));
  internal static int? NullableInt(this SqliteDataReader row, string column) =>
      row.IsDBNull(row.GetOrdinal(column)) ? null : row.Int(column);

  internal static string String(this SqliteDataReader row, string column) => row.GetString(row.GetOrdinal(column));
  internal static string? NullableString(this SqliteDataReader row, string column) =>
      row.IsDBNull(row.GetOrdinal(column)) ? null : row.String(column);

  internal static bool Bool(this SqliteDataReader row, string column) => row.GetBoolean(row.GetOrdinal(column));
  internal static DateTimeOffset Timestamp(this SqliteDataReader row, string column) =>
      DateTimeOffset.Parse(row.String(column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
  internal static DateTimeOffset? NullableTimestamp(this SqliteDataReader row, string column) =>
      row.IsDBNull(row.GetOrdinal(column)) ? null : row.Timestamp(column);
}
