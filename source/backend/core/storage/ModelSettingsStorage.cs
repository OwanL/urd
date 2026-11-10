using Microsoft.Data.Sqlite;
using Urd.Core.Providers;

namespace Urd.Core.Storage;

// Saved model capabilities survive catalog and credential changes, but not connection removal.
internal sealed class ModelSettingsStorage(Database database) {
  internal IReadOnlyList<ModelSettings> List(Guid connectionId) {
    ValidateConnectionId(connectionId);
    return database.Read(transaction => {
      RequireConnection(transaction, connectionId);
      using SqliteCommand command = database.Command(transaction, """
        SELECT model_id, family, supports_images FROM model_settings
        WHERE connection_id = $id ORDER BY model_id;
        """, ("$id", connectionId));
      using SqliteDataReader reader = command.ExecuteReader();
      var settings = new List<ModelSettings>();
      while (reader.Read())
        settings.Add(new(connectionId, reader.GetString(0), (ModelFamily)reader.GetInt32(1), reader.GetBoolean(2)));
      return settings;
    });
  }

  internal ModelSettings Save(ModelSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    ValidateConnectionId(settings.ConnectionId);
    if (string.IsNullOrWhiteSpace(settings.ModelId) || settings.ModelId.Length > 256 || settings.ModelId.Any(char.IsControl))
      throw new ArgumentException("A model ID must be nonempty and at most 256 characters without control characters.", nameof(settings));
    if (!Enum.IsDefined(settings.Family))
      throw new ArgumentException("Select a supported model family.", nameof(settings));
    return database.Write(transaction => {
      RequireConnection(transaction, settings.ConnectionId);
      using SqliteCommand command = database.Command(transaction, """
        INSERT INTO model_settings (connection_id, model_id, family, supports_images)
        VALUES ($id, $model, $family, $images)
        ON CONFLICT (connection_id, model_id) DO UPDATE
        SET family = excluded.family, supports_images = excluded.supports_images;
        """, ("$id", settings.ConnectionId), ("$model", settings.ModelId),
          ("$family", (int)settings.Family), ("$images", settings.SupportsImages ? 1 : 0));
      command.ExecuteNonQuery();
      return settings;
    });
  }

  private void RequireConnection(SqliteTransaction transaction, Guid connectionId) {
    using SqliteCommand command = database.Command(transaction,
      "SELECT 1 FROM provider_connections WHERE connection_id = $id;", ("$id", connectionId));
    if (command.ExecuteScalar() is null)
      throw new KeyNotFoundException("Provider connection was not found.");
  }

  private static void ValidateConnectionId(Guid connectionId) {
    if (connectionId == Guid.Empty)
      throw new ArgumentException("Connection ID cannot be empty.", nameof(connectionId));
  }
}
