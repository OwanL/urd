using System.Net;
using System.Text;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

public sealed class ProviderManagerTests {
  [Theory]
  [InlineData(AuthenticationMethod.None)]
  [InlineData(AuthenticationMethod.ApiKey)]
  public async Task CustomCatalogUsesOnlyItsConfiguredAuthentication(AuthenticationMethod authentication) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var handler = new ScriptedOpenAIHandler(CatalogBody());
    var manager = Manager(store, handler);
    try {
      var settings = Settings(authentication);
      ProviderConnection connection = await manager.AddCustomAsync(settings);
      ProviderModelCatalog empty = await manager.GetModelCatalogAsync(connection.Id);
      Assert.Empty(empty.Models);

      if (authentication == AuthenticationMethod.ApiKey) {
        ProviderOperationException missing = await Assert.ThrowsAsync<ProviderOperationException>(
            () => manager.RefreshModelCatalogAsync(connection.Id));
        Assert.Equal(ProviderFailure.AuthenticationRequired, missing.Failure);
        Assert.Empty(handler.Requests);
        await manager.SetApiKeyAsync(connection, "synthetic-managed-key");
      }

      ProviderModelCatalog catalog = await manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      Assert.Equal(new ProviderModel("fixture-model", "fixture-model"), Assert.Single(catalog.Models));
      ScriptedOpenAIRequest request = Assert.Single(handler.Requests);
      Assert.Equal(new Uri("https://fixture.invalid/prefix/v1/models"), request.Uri);
      Assert.Equal(authentication == AuthenticationMethod.ApiKey ? "Bearer synthetic-managed-key" : null, request.Authorization);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task CredentialChangeJoinsCatalogCleanupAndLateResponseCannotRepopulateIt() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var cancelled = Signal();
    var release = Signal();
    int calls = 0;
    var handler = new ControlledProviderHandler(async (_, token) => {
      if (Interlocked.Increment(ref calls) == 1)
        return CatalogResponse("previous-model");
      using var registration = token.Register(() => cancelled.TrySetResult());
      entered.TrySetResult();
      await release.Task; // Return a valid stale snapshot even though the operation was cancelled.
      return CatalogResponse("late-model");
    });
    var manager = Manager(store, handler);
    Task<ProviderModelCatalog>? pending = null;
    Task? change = null;
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(AuthenticationMethod.ApiKey));
      await manager.SetApiKeyAsync(connection, "synthetic-original-key");
      await manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);

      pending = manager.RefreshModelCatalogAsync(connection.Id);
      await entered.Task.WaitAsync(TestTimeout);
      change = manager.SetApiKeyAsync(connection, "synthetic-replacement-key");
      await cancelled.Task.WaitAsync(TestTimeout);
      Assert.False(change.IsCompleted);
      Assert.Empty((await manager.GetModelCatalogAsync(connection.Id)).Models);

      release.TrySetResult();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout));
      await change.WaitAsync(TestTimeout);
      ProviderModelCatalog cleared = await manager.GetModelCatalogAsync(connection.Id);
      Assert.Null(cleared.RefreshedAt);
      Assert.Empty(cleared.Models);
      Assert.True((await manager.GetCredentialStatusAsync(connection.Id)).HasCredentials);
      Assert.Equal(2, handler.Requests.Count);
    } finally {
      release.TrySetResult();
      await CloseAsync(manager, pending, change);
    }
  }

  private static ProviderManager Manager(UrdStore store, HttpMessageHandler handler) =>
      new(store, () => new Uri("http://localhost:54321/api/providers/callback"), handler);

  private static CustomProviderSettings Settings(AuthenticationMethod authentication) =>
      new("Fixture", LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/prefix/v1"), authentication);

  private static string CatalogBody(string id = "fixture-model") => $$"""
      {"object":"list","data":[{"id":"{{id}}","object":"model","created":1700000000,"owned_by":"fixture"}]}
      """;

  private static HttpResponseMessage CatalogResponse(string id) => new(HttpStatusCode.OK) {
    Content = new StringContent(CatalogBody(id), Encoding.UTF8, "application/json")
  };

  private static async Task CloseAsync(ProviderManager manager, params Task?[] operations) {
    try {
      await Task.WhenAll(operations.OfType<Task>()).WaitAsync(TestTimeout);
    } catch (Exception) {
      // Join owned operations before disposing their provider transport.
    }
    await manager.ShutdownAsync().WaitAsync(TestTimeout);
    manager.Dispose();
  }
}
