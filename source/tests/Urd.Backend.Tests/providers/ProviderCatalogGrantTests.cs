using System.Net;
using System.Text;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

// A deterministic concurrent refresh at the manager's catalog-result boundary, without OAuth I/O.
public sealed class ProviderCatalogGrantTests {
  [Theory]
  [InlineData(ProviderFailure.PlanAccessDenied)]
  [InlineData(ProviderFailure.AuthenticationRequired)]
  [InlineData(null)]
  public async Task InFlightCatalogChecksCurrentGrantWithoutRejectingSameGrantTokenRotation(ProviderFailure? expectedFailure) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var release = Signal();
    var handler = new ControlledProviderHandler(async (_, _) => {
      entered.TrySetResult();
      await release.Task;
      return new HttpResponseMessage(HttpStatusCode.OK) {
        Content = new StringContent("""
          {"models":[{"slug":"fixture-model","display_name":"Fixture model","visibility":"list"}]}
          """, Encoding.UTF8, "application/json")
      };
    });
    var manager = new ProviderManager(store, () => new Uri("http://localhost:54321/api/providers/callback"), handler);
    Task<ProviderModelCatalog>? pending = null;
    try {
      ProviderConnection connection = await manager.AddChatGptAsync("Synthetic subscription");
      var registration = new ProviderRegistration("synthetic-client", "synthetic-subject", null);
      var credentials = new ProviderCredentials(null, "synthetic-original-access", "synthetic-original-refresh",
          "synthetic-id", DateTimeOffset.MaxValue, ["openid", "resource.invoke", "chatgpt.tokens.use.direct"]);
      ProviderState initial = store.Providers.Get(connection.Id);
      initial = store.Providers.SetCredentials(connection.Id, initial.Generation, registration, credentials);
      var previousCatalog = new ProviderModelCatalog(connection.Id,
          new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), [new ProviderModel("previous-model", "Previous model")]);
      Assert.True(store.Providers.SaveCatalog(connection.Id, initial.Generation, previousCatalog));
      StoredProviderRow before = Assert.Single(StoredProviders.Read(database));

      pending = manager.RefreshModelCatalogAsync(connection.Id);
      await entered.Task.WaitAsync(TestTimeout);
      ScriptedOpenAIRequest request = Assert.Single(handler.Requests);
      Assert.Equal(HttpMethod.Get, request.Method);
      Assert.EndsWith("/models", request.Uri.AbsolutePath);
      Assert.Equal("Bearer synthetic-original-access", request.Authorization);

      // Model a concurrent completed refresh without exercising the authentication adapter.
      ProviderCredentials rotated = credentials with {
        AccessToken = "synthetic-rotated-access",
        RefreshToken = "synthetic-rotated-refresh",
        Scopes = expectedFailure == ProviderFailure.PlanAccessDenied
            ? ["openid", "resource.invoke"] : credentials.Scopes.Reverse().ToArray(),
        RequiresSignIn = expectedFailure == ProviderFailure.AuthenticationRequired
      };
      Assert.True(store.Providers.SaveRefresh(connection.Id, initial.Generation, initial.CredentialVersion, rotated));
      StoredProviderRow refreshed = Assert.Single(StoredProviders.Read(database));
      Assert.Equal(initial.Generation, refreshed.Generation);
      Assert.Equal(initial.CredentialVersion + 1, refreshed.CredentialVersion);
      if (expectedFailure is null)
        Assert.Equal(before.Catalog, refreshed.Catalog);
      else
        Assert.Null(refreshed.Catalog);

      release.TrySetResult();
      if (expectedFailure is { } failure) {
        ProviderOperationException error = await Assert.ThrowsAsync<ProviderOperationException>(() => pending.WaitAsync(TestTimeout));
        Assert.Equal(failure, error.Failure);
        ProviderModelCatalog catalog = await manager.GetModelCatalogAsync(connection.Id);
        Assert.Null(catalog.RefreshedAt);
        Assert.Empty(catalog.Models);
        Assert.Null(Assert.Single(StoredProviders.Read(database)).Catalog);
      } else {
        ProviderModelCatalog catalog = await pending.WaitAsync(TestTimeout);
        Assert.Equal(new ProviderModel("fixture-model", "Fixture model"), Assert.Single(catalog.Models));
        Assert.NotNull(catalog.RefreshedAt);
        ProviderModelCatalog persisted = await manager.GetModelCatalogAsync(connection.Id);
        Assert.Equal(catalog.RefreshedAt, persisted.RefreshedAt);
        Assert.Equal(catalog.Models, persisted.Models);
        Assert.NotEqual(before.Catalog, Assert.Single(StoredProviders.Read(database)).Catalog);
      }
      ProviderState current = store.Providers.Get(connection.Id);
      Assert.Equal(initial.Generation, current.Generation);
      Assert.Equal(initial.CredentialVersion + 1, current.CredentialVersion);
      Assert.Equal("synthetic-rotated-access", current.Credentials!.AccessToken);
      Assert.Single(handler.Requests);
    } finally {
      release.TrySetResult();
      if (pending is not null) {
        try {
          await pending.WaitAsync(TestTimeout);
        } catch (Exception) {
          // The body asserts the result; always proceed to owned-operation shutdown.
        }
      }
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }
}
