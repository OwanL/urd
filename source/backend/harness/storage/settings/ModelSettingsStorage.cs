using Microsoft.Data.Sqlite;
using Urd.Core.Providers;

namespace Urd.Core.Storage;

// Saved model capabilities survive catalog and credential changes, but not connection removal.
internal sealed class ModelSettingsStorage(Database database) {
  internal IReadOnlyList<ModelSettings> List(Guid connectionId) {
    ValidateConnectionId(connectionId);
    return database.Read(transaction => {
      RequireConnection(transaction, connectionId);
      return database.Query(transaction, """
        SELECT model_id, family, supports_images, context_window, family_explicit FROM model_settings
        WHERE connection_id = $id ORDER BY model_id;
        """, row => new ModelSettings(connectionId, row.String("model_id"),
            (ModelFamily)row.Int("family"), row.Bool("supports_images"),
            row.NullableInt("context_window"), row.Bool("family_explicit")), ("$id", connectionId));
    });
  }

  internal ModelSettings Save(ModelSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);
    ValidateConnectionId(settings.ConnectionId);
    if (string.IsNullOrWhiteSpace(settings.ModelId) || settings.ModelId.Length > 256 || settings.ModelId.Any(char.IsControl))
      throw new ArgumentException("A model ID must be nonempty and at most 256 characters without control characters.", nameof(settings));
    if (!Enum.IsDefined(settings.Family))
      throw new ArgumentException("Select a supported model family.", nameof(settings));
    ModelContextWindows.Validate(settings.ContextWindow, nameof(settings));
    return database.Write(transaction => {
      RequireConnection(transaction, settings.ConnectionId);
      database.Execute(transaction, """
        INSERT INTO model_settings
            (connection_id, model_id, family, supports_images, context_window, family_explicit)
        VALUES ($id, $model, $family, $images, $contextWindow, $familyExplicit)
        ON CONFLICT (connection_id, model_id) DO UPDATE
        SET family = excluded.family, supports_images = excluded.supports_images,
            context_window = excluded.context_window, family_explicit = excluded.family_explicit;
        """, ("$id", settings.ConnectionId), ("$model", settings.ModelId),
          ("$family", (int)settings.Family), ("$images", settings.SupportsImages ? 1 : 0),
          ("$contextWindow", settings.ContextWindow), ("$familyExplicit", settings.FamilyExplicit ? 1 : 0));
      return settings;
    });
  }

  internal bool ImagesDisabled(Guid connectionId, string modelId) => database.Read(transaction =>
      database.Scalar<bool>(transaction, """
        SELECT EXISTS(SELECT 1 FROM model_settings WHERE connection_id = $id AND model_id = $model AND supports_images = 0);
        """, ("$id", connectionId), ("$model", modelId)));

  // Positive evidence fills an unset capability only; a user's saved setting wins.
  internal void RecordImageRejection(Guid connectionId, string modelId) => database.Write(transaction =>
      database.Execute(transaction, """
        INSERT INTO model_settings
            (connection_id, model_id, family, supports_images, context_window, family_explicit)
        VALUES ($id, $model, $family, 0, NULL, 0)
        ON CONFLICT (connection_id, model_id) DO NOTHING;
        """, ("$id", connectionId), ("$model", modelId), ("$family", (int)ModelFamily.Other)));

  private void RequireConnection(SqliteTransaction transaction, Guid connectionId) {
    if (!database.Scalar<bool>(transaction,
        "SELECT EXISTS(SELECT 1 FROM provider_connections WHERE connection_id = $id);", ("$id", connectionId)))
      throw new KeyNotFoundException("Provider connection was not found.");
  }

  private static void ValidateConnectionId(Guid connectionId) {
    if (connectionId == Guid.Empty)
      throw new ArgumentException("Connection ID cannot be empty.", nameof(connectionId));
  }
}
