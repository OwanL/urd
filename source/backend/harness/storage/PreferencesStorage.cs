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
        database.Execute(transaction, """
          INSERT INTO preferences (singleton, working_directory, compaction_trigger_token_count)
          VALUES (1, $directory, $compactionTriggerTokenCount)
          ON CONFLICT(singleton) DO UPDATE SET
              working_directory = excluded.working_directory,
              compaction_trigger_token_count = excluded.compaction_trigger_token_count;
          """, ("$directory", updated.WorkingDirectory),
            ("$compactionTriggerTokenCount", updated.CompactionTriggerTokenCount));
        return true;
      });
      cachedPreferences = updated;
      return updated;
    }
  }

  private UserPreferences ReadLocked() {
    if (cachedPreferences is not null)
      return cachedPreferences;

    UserPreferences persisted = database.Read(transaction => database.QuerySingleOrDefault(transaction, """
        SELECT working_directory, compaction_trigger_token_count
        FROM preferences WHERE singleton = 1;
        """, row => new UserPreferences(row.NullableString("working_directory"),
            row.NullableInt("compaction_trigger_token_count"))) ?? new UserPreferences(null, null));
    cachedPreferences = persisted;
    return persisted;
  }
}
