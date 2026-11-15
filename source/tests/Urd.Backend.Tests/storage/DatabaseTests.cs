using System.Globalization;
using Microsoft.Data.Sqlite;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

// Exercises database helpers and named-column conversions against SQLite.
public sealed class DatabaseTests {
  [Fact]
  public void ExecuteRunsBatchesWithGuidAndNullParameters() {
    using Database database = OpenDatabase();
    Guid id = Guid.NewGuid();

    int affected = database.Write(transaction => database.Execute(transaction, """
        CREATE TABLE entries (id TEXT, note TEXT);
        INSERT INTO entries VALUES ($id, $note);
        INSERT INTO entries VALUES ($id, 'retained');
        """, ("$id", id), ("$note", null)));

    Assert.Equal(2, affected);
    Assert.Equal(2L, database.Scalar<long>(null, "SELECT COUNT(*) FROM entries WHERE id = $id;", ("$id", id)));
    Assert.Equal(id.ToString("D"), database.Scalar<string>(null, "SELECT id FROM entries LIMIT 1;"));
    Assert.Equal(1L, database.Scalar<long>(null, "SELECT COUNT(*) FROM entries WHERE note IS NULL;"));
    Assert.Equal("retained", database.Scalar<string>(null, "SELECT note FROM entries WHERE note IS NOT NULL;"));
  }

  [Fact]
  public void ScalarConvertsValuesAndReturnsDefaultsForNullOrEmptyRows() {
    using Database database = OpenDatabase();

    Assert.True(database.Scalar<bool>(null, "SELECT 1;"));
    Assert.False(database.Scalar<bool>(null, "SELECT 0;"));
    Assert.Equal(4_294_967_296L, database.Scalar<long>(null, "SELECT $value;", ("$value", 4_294_967_296L)));
    Assert.Equal("session", database.Scalar<string>(null, "SELECT $value;", ("$value", "session")));
    Assert.Equal(42, database.Scalar<int?>(null, "SELECT 42;"));
    Assert.Equal(42L, database.Scalar<long?>(null, "SELECT 42;"));
    Assert.True(database.Scalar<bool?>(null, "SELECT 1;"));
    Assert.Null(database.Scalar<long?>(null, "SELECT NULL;"));
    Assert.Null(database.Scalar<string?>(null, "SELECT NULL;"));
    Assert.False(database.Scalar<bool>(null, "SELECT 1 WHERE 0;"));
    Assert.Equal(0L, database.Scalar<long>(null, "SELECT 1 WHERE 0;"));
    Assert.Null(database.Scalar<long?>(null, "SELECT 1 WHERE 0;"));
    Assert.Null(database.Scalar<string?>(null, "SELECT 'session' WHERE 0;"));
  }

  [Fact]
  public void QueryProjectsNamedColumnsDirectlyIntoValueTuples() {
    using Database database = OpenDatabase();
    Guid id = Guid.NewGuid();
    var createdAt = new DateTimeOffset(2026, 4, 5, 6, 7, 8, TimeSpan.FromHours(5.5)).AddTicks(1234);

    var sessions = database.Query(null, """
        SELECT $created AS created_at, 'session' AS title, 1 AS active,
            2147483647 AS item_count, $id AS session_id, 4294967296 AS history_revision
        UNION ALL
        SELECT $created, 'other', 0, 0, $id, 7;
        """, row => (
          Id: row.Guid("session_id"), Revision: row.Long("history_revision"),
          Count: row.Int("item_count"), Title: row.String("title"),
          Active: row.Bool("active"), CreatedAt: row.Timestamp("created_at")),
        ("$created", createdAt.ToString("O", CultureInfo.InvariantCulture)), ("$id", id));

    Assert.Equal(2, sessions.Count);
    Assert.Equal((id, 4_294_967_296L, int.MaxValue, "session", true, createdAt), sessions[0]);
    Assert.Equal((id, 7L, 0, "other", false, createdAt), sessions[1]);
    Assert.Equal(createdAt.Offset, sessions[0].CreatedAt.Offset);
    Assert.Empty(database.Query(null, "SELECT 1 AS value WHERE 0;", row => row.Int("value")));
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void QueryReadsNullableColumnsWithAndWithoutValues(bool hasValues) {
    using Database database = OpenDatabase();
    Guid? relatedId = hasValues ? Guid.NewGuid() : null;
    long? revision = hasValues ? 4_294_967_296L : null;
    int? count = hasValues ? int.MaxValue : null;
    string? title = hasValues ? "related" : null;
    DateTimeOffset? completedAt = hasValues
        ? new DateTimeOffset(2026, 4, 5, 6, 7, 8, TimeSpan.FromHours(-3)).AddTicks(5678)
        : null;

    var related = Assert.Single(database.Query(null, """
        SELECT $completed AS completed_at, $title AS title, $count AS item_count,
            $revision AS history_revision, $id AS related_id;
        """, row => (
          Id: row.NullableGuid("related_id"), Revision: row.NullableLong("history_revision"),
          Count: row.NullableInt("item_count"), Title: row.NullableString("title"),
          CompletedAt: row.NullableTimestamp("completed_at")),
        ("$completed", completedAt?.ToString("O", CultureInfo.InvariantCulture)), ("$title", title),
        ("$count", count), ("$revision", revision), ("$id", relatedId)));

    Assert.Equal((relatedId, revision, count, title, completedAt), related);
    Assert.Equal(completedAt?.Offset, related.CompletedAt?.Offset);
  }

  [Fact]
  public void QuerySingleOrDefaultReturnsNullForEmptyNullableValueAndReferenceResults() {
    using Database database = OpenDatabase();

    Assert.Null(database.QuerySingleOrDefault<int?>(null,
        "SELECT 7 AS value WHERE 0;", row => row.Int("value")));
    Assert.Null(database.QuerySingleOrDefault<string>(null,
        "SELECT 'session' AS title WHERE 0;", row => row.String("title")));
  }

  [Fact]
  public void QuerySingleOrDefaultProjectsOnlyTheFirstRow() {
    using Database database = OpenDatabase();
    int reads = 0;

    int? first = database.QuerySingleOrDefault<int?>(null, """
        SELECT 7 AS value UNION ALL SELECT 9 ORDER BY value;
        """, row => {
          reads++;
          return row.Int("value");
        });

    Assert.Equal(7, first);
    Assert.Equal(1, reads);
    Assert.Equal("first", database.QuerySingleOrDefault(null, """
        SELECT 'first' AS title UNION ALL SELECT 'second' ORDER BY title;
        """, row => row.String("title")));
  }

  private static Database OpenDatabase() {
    var connection = new SqliteConnection("Data Source=:memory:");
    connection.Open();
    return new Database(connection);
  }
}
