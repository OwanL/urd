using Microsoft.Data.Sqlite;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class ProviderStorageTests {
  [Fact]
  public void CredentialMutationRollsBackOnCommitFailure() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var connection = new ProviderConnection(Guid.NewGuid(), "Synthetic", ProviderRoute.ChatGpt,
        LlmApi.OpenAIResponses, new Uri("https://provider.example.invalid/"), AuthenticationMethod.OAuth);
    var registration = new ProviderRegistration("synthetic-client", "synthetic-subject", "test@example.invalid");
    var originalCredentials = new ProviderCredentials(null, "old-access", "old-refresh", "old-id",
        new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero), ["openid"]);
    ProviderState state = store.Providers.Add(connection);
    state = store.Providers.SetCredentials(connection.Id, state.Generation, registration, originalCredentials);
    StoredProviderRow original = Assert.Single(StoredProviders.Read(database));
    ProviderCredentials replacement = originalCredentials with { AccessToken = "new-access" };
    database.Execute("""
      CREATE TABLE test_parent (id INTEGER PRIMARY KEY);
      CREATE TABLE test_child (parent_id INTEGER REFERENCES test_parent(id) DEFERRABLE INITIALLY DEFERRED);
      CREATE TRIGGER fail_provider AFTER UPDATE ON provider_connections
      BEGIN INSERT INTO test_child VALUES (1); END;
      """);

    Assert.Throws<SqliteException>(() => store.Providers.SetCredentials(
        connection.Id, state.Generation, registration, replacement));
    Assert.Equal(original, Assert.Single(StoredProviders.Read(database)));

    database.Execute("DROP TRIGGER fail_provider;");
    ProviderState saved = store.Providers.SetCredentials(
        connection.Id, state.Generation, registration, replacement);
    Assert.Equal("new-access", saved.Credentials!.AccessToken);
    Assert.NotEqual(original, Assert.Single(StoredProviders.Read(database)));
  }
}
