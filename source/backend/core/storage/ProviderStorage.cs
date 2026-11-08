using System.Text.Json;
using Microsoft.Data.Sqlite;
using Urd.Core.Providers;

namespace Urd.Core.Storage;

// Keeps configuration, private credentials and asynchronous-result fences in one transaction.
internal sealed class ProviderStorage(Database database) {
  private const string Columns = """
    connection_id, name, route, api, endpoint, authentication, generation, credential_version,
    registration_json, credentials_json, disconnecting, catalog_json
    """;

  internal IReadOnlyList<ProviderConnection> List() => database.Read(transaction => {
    using SqliteCommand command = database.Command(transaction,
        "SELECT connection_id, name, route, api, endpoint, authentication FROM provider_connections ORDER BY rowid;");
    using SqliteDataReader reader = command.ExecuteReader();
    var connections = new List<ProviderConnection>();
    while (reader.Read())
      connections.Add(ReadConnection(reader));
    return connections;
  });

  internal ProviderState Get(Guid id) => database.Read(transaction => Require(transaction, id));

  internal ProviderState Add(ProviderConnection connection) {
    if (connection.Id == Guid.Empty)
      throw new ArgumentException("Connection ID cannot be empty.", nameof(connection));

    return database.Write(transaction => {
      using SqliteCommand command = database.Command(transaction, """
        INSERT INTO provider_connections
            (connection_id, name, route, api, endpoint, authentication, generation, credential_version, disconnecting)
        VALUES ($id, $name, $route, $api, $endpoint, $authentication, 0, 0, 0);
        """, ("$id", connection.Id), ("$name", connection.Name), ("$route", (int)connection.Route),
          ("$api", (int)connection.Api), ("$endpoint", connection.Endpoint.AbsoluteUri),
          ("$authentication", (int)connection.Authentication));
      command.ExecuteNonQuery();
      return Require(transaction, connection.Id);
    });
  }

  internal ProviderState Rename(Guid id, string name) => Change(id, state => state with {
    Connection = state.Connection with { Name = name }
  });

  internal ProviderState UpdateCustom(Guid id, CustomProviderSettings settings) {
    if (settings.Authentication is not (AuthenticationMethod.None or AuthenticationMethod.ApiKey))
      throw new ArgumentException("Custom connections support API keys or no authentication.", nameof(settings));

    return Change(id, state => {
      if (state.Connection.Route != ProviderRoute.Custom)
        throw new ArgumentException("Only custom connections can be updated.");

      ProviderConnection connection = state.Connection with {
        Name = settings.Name,
        Api = settings.Api,
        Endpoint = settings.Endpoint,
        Authentication = settings.Authentication
      };
      bool bindingChanged = connection.Api != state.Connection.Api
          || connection.Endpoint != state.Connection.Endpoint
          || connection.Authentication != state.Connection.Authentication;
      if (!bindingChanged)
        return state with { Connection = connection };

      return state with {
        Connection = connection,
        Generation = checked(state.Generation + 1),
        CredentialVersion = checked(state.CredentialVersion + 1),
        Registration = null,
        Credentials = null,
        Catalog = null,
        Disconnecting = false
      };
    });
  }

  internal void Remove(Guid id) => database.Write(transaction => {
    using SqliteCommand command = database.Command(transaction,
        "DELETE FROM provider_connections WHERE connection_id = $id;", ("$id", id));
    if (command.ExecuteNonQuery() == 0)
      throw new KeyNotFoundException("Provider connection was not found.");
    return true;
  });

  internal string GetOrCreateHostId() => database.Write(transaction => {
    using SqliteCommand read = database.Command(transaction, "SELECT host_id FROM provider_host WHERE singleton = 1;");
    if (read.ExecuteScalar() is string hostId)
      return hostId;

    hostId = $"urn:uuid:{Guid.NewGuid():D}";
    using SqliteCommand insert = database.Command(transaction,
        "INSERT INTO provider_host (singleton, host_id) VALUES (1, $host);", ("$host", hostId));
    insert.ExecuteNonQuery();
    return hostId;
  });

  internal ProviderState SetCredentials(Guid id, long expectedGeneration,
      ProviderRegistration? registration, ProviderCredentials credentials) => Change(id, state => {
    RequireGeneration(state, expectedGeneration);
    if (state.Disconnecting)
      throw new ProviderOperationException(ProviderFailure.Disconnecting);

    return state with {
      Generation = checked(state.Generation + 1),
      CredentialVersion = checked(state.CredentialVersion + 1),
      Registration = registration,
      Credentials = credentials,
      Catalog = null
    };
  }, missingIsChanged: true);

  internal bool SaveRefresh(Guid id, long expectedGeneration, long expectedCredentialVersion,
      ProviderCredentials credentials) => database.Write(transaction => {
    ProviderState? state = Read(transaction, id);
    if (state is null || state.Generation != expectedGeneration || state.CredentialVersion != expectedCredentialVersion)
      return false;

    bool sameGrant = state.Credentials is { } previous
        && previous.RequiresSignIn == credentials.RequiresSignIn
        && previous.Scopes.ToHashSet(StringComparer.Ordinal).SetEquals(credentials.Scopes);
    Save(transaction, state with {
      Credentials = credentials,
      CredentialVersion = checked(state.CredentialVersion + 1),
      Catalog = sameGrant ? state.Catalog : null
    });
    return true;
  });

  // Do not change either version: an already-running refresh may still save its rotated token.
  internal ProviderState BeginDisconnect(Guid id) => Change(id, state => state with { Disconnecting = true });

  internal ProviderState CompleteDisconnect(Guid id, long expectedGeneration) => Change(id, state => {
    RequireGeneration(state, expectedGeneration);
    if (!state.Disconnecting)
      throw new ProviderOperationException(ProviderFailure.ConnectionChanged);

    return state with {
      Generation = checked(state.Generation + 1),
      CredentialVersion = checked(state.CredentialVersion + 1),
      Credentials = null,
      Catalog = null,
      Disconnecting = false
    };
  }, missingIsChanged: true);

  internal bool SaveCatalog(Guid id, long expectedGeneration, ProviderModelCatalog catalog) {
    if (catalog.ConnectionId != id)
      throw new ArgumentException("Catalog belongs to a different connection.", nameof(catalog));

    return database.Write(transaction => {
      ProviderState? state = Read(transaction, id);
      if (state is null || state.Generation != expectedGeneration || state.Disconnecting)
        return false;

      Save(transaction, state with { Catalog = catalog });
      return true;
    });
  }

  private ProviderState Change(Guid id, Func<ProviderState, ProviderState> change, bool missingIsChanged = false)
      => database.Write(transaction => {
        ProviderState? state = Read(transaction, id);
        if (state is null && missingIsChanged)
          throw new ProviderOperationException(ProviderFailure.ConnectionChanged);

        ProviderState updated = change(state ?? throw new KeyNotFoundException("Provider connection was not found."));
        Save(transaction, updated);
        return updated;
      });

  private ProviderState Require(SqliteTransaction transaction, Guid id)
      => Read(transaction, id) ?? throw new KeyNotFoundException("Provider connection was not found.");

  private ProviderState? Read(SqliteTransaction transaction, Guid id) {
    using SqliteCommand command = database.Command(transaction,
        $"SELECT {Columns} FROM provider_connections WHERE connection_id = $id;", ("$id", id));
    using SqliteDataReader reader = command.ExecuteReader();
    return reader.Read() ? new ProviderState(ReadConnection(reader), reader.GetInt64(6), reader.GetInt64(7),
        ReadJson<ProviderRegistration>(reader, 8), ReadJson<ProviderCredentials>(reader, 9),
        reader.GetBoolean(10), ReadJson<ProviderModelCatalog>(reader, 11)) : null;
  }

  private void Save(SqliteTransaction transaction, ProviderState state) {
    using SqliteCommand command = database.Command(transaction, """
      UPDATE provider_connections SET name = $name, api = $api, endpoint = $endpoint,
          authentication = $authentication, generation = $generation, credential_version = $version,
          registration_json = $registration, credentials_json = $credentials,
          disconnecting = $disconnecting, catalog_json = $catalog
      WHERE connection_id = $id;
      """, ("$id", state.Connection.Id), ("$name", state.Connection.Name), ("$api", (int)state.Connection.Api),
        ("$endpoint", state.Connection.Endpoint.AbsoluteUri), ("$authentication", (int)state.Connection.Authentication),
        ("$generation", state.Generation), ("$version", state.CredentialVersion),
        ("$registration", WriteJson(state.Registration)), ("$credentials", WriteJson(state.Credentials)),
        ("$disconnecting", state.Disconnecting ? 1 : 0), ("$catalog", WriteJson(state.Catalog)));
    command.ExecuteNonQuery();
  }

  private static ProviderConnection ReadConnection(SqliteDataReader reader) => new(reader.GetGuid(0),
      reader.GetString(1), (ProviderRoute)reader.GetInt32(2), (LlmApi)reader.GetInt32(3),
      new Uri(reader.GetString(4), UriKind.Absolute), (AuthenticationMethod)reader.GetInt32(5));

  private static T? ReadJson<T>(SqliteDataReader reader, int column) where T : class
      => reader.IsDBNull(column) ? null : JsonSerializer.Deserialize<T>(reader.GetString(column));

  private static string? WriteJson<T>(T? value) where T : class
      => value is null ? null : JsonSerializer.Serialize(value);

  private static void RequireGeneration(ProviderState state, long expectedGeneration) {
    if (state.Generation != expectedGeneration)
      throw new ProviderOperationException(ProviderFailure.ConnectionChanged);
  }
}
