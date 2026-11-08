using System.Net;
using System.Text;
using System.Text.Json;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

// Manager lifecycle tests use the real SDK with synthetic, socket-free provider responses.
public sealed class ProviderManagerTests {
  [Fact]
  public async Task ConfigurationCrudIsOfflineAndNamesDoNotOwnCredentials() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = CatalogHandler();
    var manager = Manager(store, handler);
    try {
      Assert.Empty(await manager.ListAsync());
      ProviderConnection official = await manager.AddOpenAIAsync("  same name  ");
      ProviderConnection custom = await manager.AddCustomAsync(Settings(AuthenticationMethod.ApiKey) with { Name = "same name" });
      Assert.NotEqual(official.Id, custom.Id);
      Assert.Equal("same name", official.Name);
      Assert.Equal(ProviderRoute.OpenAI, official.Route);
      Assert.Equal(new Uri("https://api.openai.com/v1/"), official.Endpoint);
      Assert.Equal(new Uri("https://fixture.invalid/prefix/v1/"), custom.Endpoint);
      Assert.Equal(new[] { official, custom }, await manager.ListAsync());
      Assert.Equal(custom, await manager.GetAsync(custom.Id));
      Assert.False((await manager.GetCredentialStatusAsync(custom.Id)).HasCredentials);
      await manager.SetApiKeyAsync(custom, "synthetic-key");
      Assert.False((await manager.GetCredentialStatusAsync(official.Id)).HasCredentials);
      ProviderConnection renamed = await manager.RenameAsync(custom.Id, " renamed ");
      Assert.Equal("renamed", renamed.Name);
      // A caller's pre-rename view is still a valid key binding.
      ProviderCredentialStatus status = await manager.SetApiKeyAsync(custom, "synthetic-replacement");
      Assert.True(status.HasCredentials);
      Assert.False(status.RequiresSignIn);
      Assert.Null(status.PlanAccessGranted);
      Assert.Null(status.AccountLabel);
      await manager.RemoveAsync(official.Id);
      Assert.Equal(renamed, Assert.Single(await manager.ListAsync()));
      await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.GetAsync(official.Id));
      await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.RemoveAsync(official.Id));
      Assert.Empty(handler.Requests);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Theory]
  [InlineData(AuthenticationMethod.None)]
  [InlineData(AuthenticationMethod.ApiKey)]
  public async Task ExplicitCatalogUsesSavedAuthenticationAndOfflineReadsPreserveTheSnapshot(AuthenticationMethod authentication) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = CatalogHandler();
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(authentication));
      ProviderModelCatalog empty = await manager.GetModelCatalogAsync(connection.Id);
      Assert.Null(empty.RefreshedAt);
      Assert.Empty(empty.Models);
      if (authentication == AuthenticationMethod.ApiKey) {
        await AssertFailureAsync(ProviderFailure.AuthenticationRequired,
            () => manager.RefreshModelCatalogAsync(connection.Id));
        Assert.Empty(handler.Requests);
        await manager.SetApiKeyAsync(connection, "synthetic-key");
      }
      Assert.Empty(handler.Requests);
      ProviderModelCatalog fetched = await manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      Assert.Equal(connection.Id, fetched.ConnectionId);
      Assert.NotNull(fetched.RefreshedAt);
      Assert.Equal(new[] { new ProviderModel("z-model", "z-model"), new ProviderModel("a-model", "a-model") }, fetched.Models);
      ProviderModelCatalog saved = await manager.GetModelCatalogAsync(connection.Id);
      Assert.Equal(fetched.RefreshedAt, saved.RefreshedAt);
      Assert.Equal(fetched.Models, saved.Models);
      ScriptedOpenAIRequest request = Assert.Single(handler.Requests);
      Assert.Equal(HttpMethod.Get, request.Method);
      Assert.Equal("/prefix/v1/models", request.Uri.AbsolutePath);
      Assert.Equal(authentication == AuthenticationMethod.None ? null : "Bearer synthetic-key", request.Authorization);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task CredentialsAndTimestampedCatalogSurviveReopenWithoutAutomaticNetworkWork() {
    using var database = new TemporaryDatabase();
    ProviderConnection connection;
    ProviderModelCatalog fetched;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var handler = CatalogHandler();
      var manager = Manager(store, handler);
      try {
        connection = await manager.AddCustomAsync(Settings(AuthenticationMethod.ApiKey));
        await manager.SetApiKeyAsync(connection, "synthetic-persisted-key");
        fetched = await manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      } finally {
        await CloseAsync(manager);
      }
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    var reopenedHandler = CatalogHandler();
    var reopenedManager = Manager(reopened, reopenedHandler);
    try {
      Assert.Equal(connection, Assert.Single(await reopenedManager.ListAsync()));
      Assert.Equal(connection, await reopenedManager.GetAsync(connection.Id));
      ProviderCredentialStatus status = await reopenedManager.GetCredentialStatusAsync(connection.Id);
      Assert.True(status.HasCredentials);
      Assert.False(status.RequiresSignIn);
      Assert.False(status.IsDisconnecting);
      ProviderModelCatalog saved = await reopenedManager.GetModelCatalogAsync(connection.Id);
      Assert.Equal(fetched.RefreshedAt, saved.RefreshedAt);
      Assert.Equal(fetched.Models, saved.Models);
      Assert.Empty(reopenedHandler.Requests);
      await reopenedManager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      Assert.Equal("Bearer synthetic-persisted-key", Assert.Single(reopenedHandler.Requests).Authorization);
    } finally {
      await CloseAsync(reopenedManager);
    }
  }

  [Fact]
  public async Task ChangedEndpointRejectsTheOldKeyBindingAndClearsCredentialsAndCatalog() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = CatalogHandler();
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(AuthenticationMethod.ApiKey));
      await manager.SetApiKeyAsync(connection, "synthetic-original-key");
      await manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      ProviderConnection updated = await manager.UpdateCustomAsync(connection.Id,
          Settings(AuthenticationMethod.ApiKey) with { Endpoint = new Uri("https://other.invalid/new-prefix") });
      await AssertFailureAsync(ProviderFailure.ConnectionChanged,
          () => manager.SetApiKeyAsync(connection, "synthetic-stale-key"));
      Assert.False((await manager.GetCredentialStatusAsync(connection.Id)).HasCredentials);
      Assert.Null((await manager.GetModelCatalogAsync(connection.Id)).RefreshedAt);
      await AssertFailureAsync(ProviderFailure.AuthenticationRequired,
          () => manager.RefreshModelCatalogAsync(connection.Id));
      Assert.Single(handler.Requests);
      await manager.SetApiKeyAsync(updated, "synthetic-current-key");
      await manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      ScriptedOpenAIRequest request = handler.Requests[1];
      Assert.Equal(new Uri("https://other.invalid/new-prefix/models"), request.Uri);
      Assert.Equal("Bearer synthetic-current-key", request.Authorization);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Theory]
  [InlineData("relative/path", LlmApi.OpenAIResponses, AuthenticationMethod.None)]
  [InlineData("ftp://fixture.invalid/", LlmApi.OpenAIResponses, AuthenticationMethod.None)]
  [InlineData("https://user:password@fixture.invalid/", LlmApi.OpenAIResponses, AuthenticationMethod.None)]
  [InlineData("https://fixture.invalid/?query=value", LlmApi.OpenAIResponses, AuthenticationMethod.None)]
  [InlineData("https://fixture.invalid/#fragment", LlmApi.OpenAIResponses, AuthenticationMethod.None)]
  [InlineData("https://fixture.invalid/", (LlmApi)999, AuthenticationMethod.None)]
  [InlineData("https://fixture.invalid/", LlmApi.OpenAIResponses, AuthenticationMethod.OAuth)]
  [InlineData("https://fixture.invalid/", LlmApi.OpenAIResponses, (AuthenticationMethod)999)]
  public async Task InvalidCustomSettingsCannotInsertOrReplaceAConnection(string endpoint, LlmApi api, AuthenticationMethod authentication) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = CatalogHandler();
    var manager = Manager(store, handler);
    try {
      ProviderConnection original = await manager.AddCustomAsync(Settings());
      var invalid = new CustomProviderSettings("invalid", api, new Uri(endpoint, UriKind.RelativeOrAbsolute), authentication);
      await Assert.ThrowsAsync<ArgumentException>(() => manager.AddCustomAsync(invalid));
      await Assert.ThrowsAsync<ArgumentException>(() => manager.UpdateCustomAsync(original.Id, invalid));
      Assert.Equal(original, Assert.Single(await manager.ListAsync()));
      ProviderConnection official = await manager.AddOpenAIAsync("official");
      await Assert.ThrowsAsync<ArgumentException>(() => manager.UpdateCustomAsync(official.Id, Settings()));
      await Assert.ThrowsAsync<ArgumentException>(() => manager.SetApiKeyAsync(original, "synthetic-key"));
      Assert.Empty(handler.Requests);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Theory]
  [InlineData("")]
  [InlineData(" ")]
  [InlineData("bad\nname")]
  public async Task InvalidNamesAndKeysLeaveTheSavedConnectionUnchanged(string invalid) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = CatalogHandler();
    var manager = Manager(store, handler);
    try {
      ProviderConnection original = await manager.AddCustomAsync(Settings(AuthenticationMethod.ApiKey));
      await Assert.ThrowsAsync<ArgumentException>(() => manager.AddOpenAIAsync(invalid));
      await Assert.ThrowsAsync<ArgumentException>(() => manager.RenameAsync(original.Id, invalid));
      await Assert.ThrowsAsync<ArgumentException>(() => manager.SetApiKeyAsync(original, invalid));
      Assert.Equal(original, await manager.GetAsync(original.Id));
      Assert.False((await manager.GetCredentialStatusAsync(original.Id)).HasCredentials);
      Assert.Empty(handler.Requests);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task CatalogFailurePreservesTheLastSuccessfulSnapshot() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    int calls = 0;
    var handler = new ControlledProviderHandler((_, _) => Task.FromResult(
        Interlocked.Increment(ref calls) == 1 ? CatalogResponse() : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) {
          Content = new StringContent("""{"error":{"message":"synthetic-private-error"}}""", Encoding.UTF8, "application/json")
        }));
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      ProviderModelCatalog fetched = await manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      await AssertFailureAsync(ProviderFailure.ProviderUnavailable, () => manager.RefreshModelCatalogAsync(connection.Id));
      ProviderModelCatalog retained = await manager.GetModelCatalogAsync(connection.Id);
      Assert.Equal(fetched.RefreshedAt, retained.RefreshedAt);
      Assert.Equal(fetched.Models, retained.Models);
      Assert.Equal(2, handler.Requests.Count);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Theory]
  [InlineData("credential")]
  [InlineData("update")]
  [InlineData("remove")]
  public async Task BindingMutationCancelsAndJoinsCatalogWorkAndALateResponseCannotRepopulateIt(string mutation) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var cancelled = Signal();
    var release = Signal();
    int calls = 0;
    var handler = new ControlledProviderHandler(async (_, token) => {
      if (Interlocked.Increment(ref calls) == 1)
        return CatalogResponse();
      using var registration = token.Register(() => cancelled.TrySetResult());
      entered.TrySetResult();
      // Deliberately return a genuine successful catalog even after cancellation.
      await release.Task;
      return CatalogResponse("late-model");
    });
    var manager = Manager(store, handler);
    Task? pending = null, change = null;
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(AuthenticationMethod.ApiKey));
      await manager.SetApiKeyAsync(connection, "synthetic-original-key");
      await manager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout);
      pending = manager.RefreshModelCatalogAsync(connection.Id);
      await entered.Task.WaitAsync(TestTimeout);
      change = mutation switch {
        "credential" => manager.SetApiKeyAsync(connection, "synthetic-new-key"),
        "update" => manager.UpdateCustomAsync(connection.Id, Settings(AuthenticationMethod.ApiKey) with {
          Endpoint = new Uri("https://updated.invalid/v1/")
        }),
        _ => manager.RemoveAsync(connection.Id)
      };
      await cancelled.Task.WaitAsync(TestTimeout);
      Assert.False(change.IsCompleted);
      if (mutation != "remove")
        Assert.Null((await manager.GetModelCatalogAsync(connection.Id)).RefreshedAt);
      release.TrySetResult();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout));
      await change.WaitAsync(TestTimeout);
      if (mutation == "remove") {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.GetModelCatalogAsync(connection.Id));
        Assert.Empty(await manager.ListAsync());
      } else {
        ProviderModelCatalog empty = await manager.GetModelCatalogAsync(connection.Id);
        Assert.Null(empty.RefreshedAt);
        Assert.Empty(empty.Models);
        Assert.Equal(mutation == "credential", (await manager.GetCredentialStatusAsync(connection.Id)).HasCredentials);
      }
      Assert.Equal(2, handler.Requests.Count);
    } finally {
      release.TrySetResult();
      await CloseAsync(manager, pending, change);
    }
  }

  [Fact]
  public async Task DisplayOnlyChangesDoNotCancelAnAdmittedCatalog() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var release = Signal();
    CancellationToken providerToken = default;
    var handler = new ControlledProviderHandler(async (_, token) => {
      providerToken = token;
      entered.TrySetResult();
      await release.Task;
      return CatalogResponse();
    });
    var manager = Manager(store, handler);
    Task<ProviderModelCatalog>? pending = null;
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      pending = manager.RefreshModelCatalogAsync(connection.Id);
      await entered.Task.WaitAsync(TestTimeout);
      await manager.RenameAsync(connection.Id, "renamed").WaitAsync(TestTimeout);
      await manager.UpdateCustomAsync(connection.Id, Settings() with { Name = "display only" }).WaitAsync(TestTimeout);
      Assert.False(providerToken.IsCancellationRequested);
      Assert.False(pending.IsCompleted);
      release.TrySetResult();
      ProviderModelCatalog catalog = await pending.WaitAsync(TestTimeout);
      Assert.Equal(catalog.RefreshedAt, (await manager.GetModelCatalogAsync(connection.Id)).RefreshedAt);
      Assert.Equal("display only", (await manager.GetAsync(connection.Id)).Name);
    } finally {
      release.TrySetResult();
      await CloseAsync(manager, pending);
    }
  }

  [Fact]
  public async Task CallerCancellationJoinsOnlyItsDiagnosticAndDoesNotCancelCatalogOrConfiguration() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    using var caller = new CancellationTokenSource();
    var catalogEntered = Signal();
    var diagnosticEntered = Signal();
    var cleanupEntered = Signal();
    var releaseCleanup = Signal();
    var releaseCatalog = Signal();
    CancellationToken catalogToken = default;
    var handler = new ControlledProviderHandler(async (request, token) => {
      if (request.Method == HttpMethod.Get) {
        catalogToken = token;
        catalogEntered.TrySetResult();
        await releaseCatalog.Task;
        return CatalogResponse();
      }
      diagnosticEntered.TrySetResult();
      try {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("A held diagnostic must be cancelled.");
      } finally {
        cleanupEntered.TrySetResult();
        await releaseCleanup.Task;
      }
    });
    var manager = Manager(store, handler);
    Task? diagnostic = null;
    Task<ProviderModelCatalog>? catalog = null;
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      catalog = manager.RefreshModelCatalogAsync(connection.Id);
      await catalogEntered.Task.WaitAsync(TestTimeout);
      diagnostic = manager.TestModelAsync(connection.Id, "fixture-model", caller.Token);
      await diagnosticEntered.Task.WaitAsync(TestTimeout);
      await caller.CancelAsync();
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Assert.False(diagnostic.IsCompleted);
      Assert.False(catalogToken.IsCancellationRequested);
      Assert.Equal("still configured", (await manager.RenameAsync(connection.Id, "still configured").WaitAsync(TestTimeout)).Name);
      releaseCleanup.TrySetResult();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => diagnostic.WaitAsync(TestTimeout));
      Assert.False(catalog.IsCompleted);
      releaseCatalog.TrySetResult();
      Assert.NotNull((await catalog.WaitAsync(TestTimeout)).RefreshedAt);
      Assert.Equal(2, handler.Requests.Count);
    } finally {
      await caller.CancelAsync();
      releaseCleanup.TrySetResult();
      releaseCatalog.TrySetResult();
      await CloseAsync(manager, diagnostic, catalog);
    }
  }

  [Fact]
  public async Task ShutdownJoinsHeldRequestCleanupBeforeOwnedHandlerCanBeDisposed() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var entered = Signal();
    var cleanupEntered = Signal();
    var release = Signal();
    var cleanupFinished = Signal();
    var handler = new ControlledProviderHandler(async (_, token) => {
      entered.TrySetResult();
      try {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("Shutdown must cancel the held request.");
      } finally {
        cleanupEntered.TrySetResult();
        await release.Task;
        cleanupFinished.TrySetResult();
      }
    });
    var manager = Manager(store, handler);
    Task? pending = null;
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      pending = manager.RefreshModelCatalogAsync(connection.Id);
      await entered.Task.WaitAsync(TestTimeout);
      Task shutdown = manager.ShutdownAsync();
      await cleanupEntered.Task.WaitAsync(TestTimeout);
      Task repeated = manager.ShutdownAsync();
      Assert.False(shutdown.IsCompleted);
      Assert.False(repeated.IsCompleted);
      Assert.False(cleanupFinished.Task.IsCompleted);
      Assert.Equal(0, handler.DisposeCalls);
      Assert.Throws<InvalidOperationException>(() => manager.Dispose());
      await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AddOpenAIAsync("too late"));
      release.TrySetResult();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TestTimeout));
      await Task.WhenAll(shutdown, repeated).WaitAsync(TestTimeout);
      Assert.True(cleanupFinished.Task.IsCompletedSuccessfully);
      Assert.Equal(0, handler.DisposeCalls);
      manager.Dispose();
      manager.Dispose();
      Assert.Equal(1, handler.DisposeCalls);
      // The manager borrows storage rather than disposing it.
      Assert.Equal(connection, store.Providers.Get(connection.Id).Connection);
    } finally {
      release.TrySetResult();
      await CloseAsync(manager, pending);
    }
  }

  [Theory]
  [InlineData(AuthenticationMethod.None)]
  [InlineData(AuthenticationMethod.ApiKey)]
  public async Task InterruptedDisconnectStaysFencedAfterReopenAndCanBeResolvedWithoutOAuth(AuthenticationMethod authentication) {
    using var database = new TemporaryDatabase();
    ProviderConnection connection;
    using (UrdStore store = UrdStore.Open(database.Path)) {
      var manager = Manager(store, CatalogHandler());
      try {
        connection = await manager.AddCustomAsync(Settings(authentication));
        if (authentication == AuthenticationMethod.ApiKey)
          await manager.SetApiKeyAsync(connection, "synthetic-before-disconnect");
      } finally {
        await CloseAsync(manager);
      }
      // Simulate a process exit after the durable fence and before disconnect completion.
      store.Providers.BeginDisconnect(connection.Id);
    }

    using UrdStore reopened = UrdStore.Open(database.Path);
    var handler = CatalogHandler();
    var reopenedManager = Manager(reopened, handler);
    try {
      ProviderCredentialStatus fenced = await reopenedManager.GetCredentialStatusAsync(connection.Id);
      Assert.True(fenced.IsDisconnecting);
      Assert.Equal(authentication == AuthenticationMethod.ApiKey, fenced.HasCredentials);
      await AssertFailureAsync(ProviderFailure.Disconnecting, () => reopenedManager.RefreshModelCatalogAsync(connection.Id));
      await AssertFailureAsync(ProviderFailure.Disconnecting,
          () => reopenedManager.UpdateCustomAsync(connection.Id, Settings(authentication)));
      if (authentication == AuthenticationMethod.ApiKey)
        await AssertFailureAsync(ProviderFailure.Disconnecting,
            () => reopenedManager.SetApiKeyAsync(connection, "synthetic-not-yet-allowed"));
      Assert.Empty(handler.Requests);
      ProviderDisconnectResult result = await reopenedManager.DisconnectAsync(connection.Id).WaitAsync(TestTimeout);
      Assert.True(result.LocalCredentialsCleared);
      Assert.Null(result.RemoteRevocationConfirmed);
      ProviderCredentialStatus disconnected = await reopenedManager.GetCredentialStatusAsync(connection.Id);
      Assert.False(disconnected.IsDisconnecting);
      Assert.False(disconnected.HasCredentials);
      Assert.Null((await reopenedManager.GetModelCatalogAsync(connection.Id)).RefreshedAt);
      Assert.Empty(handler.Requests);
      if (authentication == AuthenticationMethod.ApiKey)
        await reopenedManager.SetApiKeyAsync(connection, "synthetic-after-disconnect");
      Assert.NotNull((await reopenedManager.RefreshModelCatalogAsync(connection.Id).WaitAsync(TestTimeout)).RefreshedAt);
      Assert.Single(handler.Requests);
    } finally {
      await CloseAsync(reopenedManager);
    }
  }

  private static CustomProviderSettings Settings(AuthenticationMethod authentication = AuthenticationMethod.None)
      => new("Fixture", LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/prefix/v1"), authentication);

  private static ProviderManager Manager(UrdStore store, HttpMessageHandler handler)
      => new(store, () => new Uri("http://localhost:54321/api/providers/callback"), handler);

  private static ControlledProviderHandler CatalogHandler()
      => new((_, _) => Task.FromResult(CatalogResponse()));

  private static HttpResponseMessage CatalogResponse(params string[] ids) => new(HttpStatusCode.OK) {
    Content = new StringContent(JsonSerializer.Serialize(new {
      @object = "list",
      data = (ids.Length == 0 ? new[] { "z-model", "a-model" } : ids).Select(id => new {
        id, @object = "model", created = 1700000000, owned_by = "fixture"
      })
    }), Encoding.UTF8, "application/json")
  };

  private static async Task AssertFailureAsync(ProviderFailure expected, Func<Task> operation) {
    ProviderOperationException error = await Assert.ThrowsAsync<ProviderOperationException>(() => operation().WaitAsync(TestTimeout));
    Assert.Equal(expected, error.Failure);
    Assert.Equal(expected.ToString(), error.Message);
    Assert.Null(error.InnerException);
  }

  private static async Task CloseAsync(ProviderManager manager, params Task?[] operations) {
    try {
      await Task.WhenAll(operations.OfType<Task>()).WaitAsync(TestTimeout);
    } catch (Exception) {
      // Observe owned results without hiding a body assertion; shutdown must still join cleanup.
    }
    await manager.ShutdownAsync().WaitAsync(TestTimeout);
    manager.Dispose();
  }
}
