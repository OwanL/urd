using Microsoft.Data.Sqlite;
using Urd.Core.Preferences;

namespace Urd.Core.Storage;

// Stores and caches the shared application preferences.
internal sealed class PreferencesStorage(Database database) {
  private readonly object gate = new();
  private UserPreferences? cachedPreferences;

  internal UserPreferences Read() {
    lock (gate)
      return ReadLocked();
  }

  internal UserPreferences Update(Func<UserPreferences, UserPreferences> update) {
    ArgumentNullException.ThrowIfNull(update);
    lock (gate) {
      UserPreferences updated = update(ReadLocked());
      database.Write(transaction => {
        using SqliteCommand command = database.Command(transaction, """
          INSERT INTO preferences (singleton, working_directory, compaction_trigger_token_count)
          VALUES (1, $directory, $compactionTriggerTokenCount)
          ON CONFLICT(singleton) DO UPDATE SET
              working_directory = excluded.working_directory,
              compaction_trigger_token_count = excluded.compaction_trigger_token_count;
          """, ("$directory", updated.WorkingDirectory),
            ("$compactionTriggerTokenCount", updated.CompactionTriggerTokenCount));
        command.ExecuteNonQuery();
        return true;
      });
      cachedPreferences = updated;
      return updated;
    }
  }

  private UserPreferences ReadLocked() {
    if (cachedPreferences is not null)
      return cachedPreferences;

    UserPreferences persisted = database.Read(transaction => {
      using SqliteCommand command = database.Command(transaction, """
        SELECT working_directory, compaction_trigger_token_count
        FROM preferences WHERE singleton = 1;
        """);
      using SqliteDataReader reader = command.ExecuteReader();
      if (!reader.Read())
        return new UserPreferences(null, null);

      return new UserPreferences(reader.IsDBNull(0) ? null : reader.GetString(0),
          reader.IsDBNull(1) ? null : reader.GetInt32(1));
    });
    cachedPreferences = persisted;
    return persisted;
  }
}
