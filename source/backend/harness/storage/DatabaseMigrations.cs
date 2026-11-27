using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Applies consecutive, database-only upgrades as one atomic change.
internal static class DatabaseMigrations {
  internal const int FirstSupportedVersion = 36;

  internal static void Upgrade(SqliteConnection connection, int targetVersion,
      IReadOnlyDictionary<int, string[]> migrations) {
    using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    // Read under the write lock in case another connection upgraded while we waited.
    command.CommandText = "PRAGMA user_version;";
    int version = (int)(long)command.ExecuteScalar()!;
    if (version < FirstSupportedVersion || version > targetVersion)
      throw UnsupportedVersion(version, targetVersion);
    if (version == targetVersion)
      return;

    for (int source = version; source < targetVersion; source++) {
      if (!migrations.ContainsKey(source))
        throw new InvalidOperationException($"Missing session database migration from schema {source} to {source + 1}.");
    }
    for (int source = version; source < targetVersion; source++) {
      foreach (string sql in migrations[source]) {
        command.CommandText = sql;
        command.ExecuteNonQuery();
      }
    }
    command.CommandText = string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {targetVersion};");
    command.ExecuteNonQuery();
    transaction.Commit();
  }

  internal static NotSupportedException UnsupportedVersion(long version, int targetVersion) => new(
      version > targetVersion
          ? $"Session database schema {version} is newer than this Urd build (schema {targetVersion}). Use a newer Urd build."
          : $"Session database schema {version} predates the supported baseline (schema {FirstSupportedVersion}). "
              + "Use the Urd build that created it, or use a different data folder to start fresh.");
}
