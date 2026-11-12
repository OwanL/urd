using Microsoft.Data.Sqlite;

namespace Urd.Core.Storage;

// Stores the single shared working-directory preference.
internal sealed class PreferencesStorage(Database database) {
  internal string? ReadWorkingDirectory() => database.Read(transaction => {
    using SqliteCommand command = database.Command(transaction,
        "SELECT working_directory FROM preferences WHERE singleton = 1;");
    object? value = command.ExecuteScalar();
    return value is string path ? path : null;
  });

  internal void SetWorkingDirectory(string? path) => database.Write(transaction => {
    using SqliteCommand command = database.Command(transaction, """
      INSERT INTO preferences (singleton, working_directory) VALUES (1, $directory)
      ON CONFLICT(singleton) DO UPDATE SET working_directory = excluded.working_directory;
      """, ("$directory", path));
    command.ExecuteNonQuery();
    return true;
  });
}
