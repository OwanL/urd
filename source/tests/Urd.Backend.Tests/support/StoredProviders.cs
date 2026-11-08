using Microsoft.Data.Sqlite;
using Urd.Core.Providers;

namespace Urd.Backend.Tests.Support;

// One evidence reader for the private provider rows; test values are synthetic only.
internal static class StoredProviders {
  internal static IReadOnlyList<StoredProviderRow> Read(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
      SELECT connection_id, name, route, api, endpoint, authentication, generation, credential_version,
          registration_json, credentials_json, disconnecting, catalog_json
      FROM provider_connections ORDER BY rowid;
      """;
    using SqliteDataReader reader = command.ExecuteReader();
    var rows = new List<StoredProviderRow>();
    while (reader.Read()) {
      var provider = new ProviderConnection(reader.GetGuid(0), reader.GetString(1), (ProviderRoute)reader.GetInt32(2),
          (LlmApi)reader.GetInt32(3), new Uri(reader.GetString(4)), (AuthenticationMethod)reader.GetInt32(5));
      rows.Add(new(provider, reader.GetInt64(6), reader.GetInt64(7), Json(8), Json(9), reader.GetBoolean(10), Json(11)));
    }
    return rows;

    string? Json(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
  }

  internal static IReadOnlyList<string> ReadHostIds(TemporaryDatabase database) {
    using SqliteConnection connection = database.OpenReadOnlyConnection();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT host_id FROM provider_host ORDER BY singleton;";
    using SqliteDataReader reader = command.ExecuteReader();
    var ids = new List<string>();
    while (reader.Read())
      ids.Add(reader.GetString(0));
    return ids;
  }
}

internal sealed record StoredProviderRow(
    ProviderConnection Connection, long Generation, long CredentialVersion, string? Registration,
    string? Credentials, bool Disconnecting, string? Catalog) {
  public override string ToString() => $"Stored provider {Connection.Id}";
}
