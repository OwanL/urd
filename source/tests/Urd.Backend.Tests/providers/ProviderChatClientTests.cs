using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

public sealed class ProviderChatClientTests {
  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task BorrowedClientsShareSavedCredentialsWithoutCreatingSessions(LlmApi api) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ScriptedOpenAIHandler(Stream(api));
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(api, AuthenticationMethod.ApiKey));
      using IChatClient first = manager.CreateChatClient(connection);
      Assert.Empty(handler.Requests);
      Assert.Null(first.GetService(typeof(HttpClient)));
      Assert.Same(first, first.GetService(typeof(IChatClient)));
      var missing = await Assert.ThrowsAsync<ProviderOperationException>(() => first.GetResponseAsync(Messages(), Options()));
      Assert.Equal(ProviderFailure.AuthenticationRequired, missing.Failure);
      Assert.Empty(handler.Requests);
      await manager.SetApiKeyAsync(connection, "synthetic-managed-key");
      ChatResponse response = await first.GetResponseAsync(Messages(), Options()).WaitAsync(TestTimeout);
      Assert.Equal("hello", response.Text);
      Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
      first.Dispose();
      using IChatClient second = manager.CreateChatClient(connection);
      Assert.Equal("hello", (await second.GetResponseAsync(Messages(), Options()).WaitAsync(TestTimeout)).Text);
      Assert.Equal(2, handler.Requests.Count);
      Assert.All(handler.Requests, request => Assert.Equal("Bearer synthetic-managed-key", request.Authorization));
      Assert.True(handler.StreamDisposals >= 2);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  [Fact]
  public async Task EndpointChangesRejectTheOldBindingButRenamesDoNot() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ScriptedOpenAIHandler(Stream(LlmApi.OpenAIResponses));
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      using IChatClient client = manager.CreateChatClient(connection);
      await manager.RenameAsync(connection.Id, "renamed");
      Assert.Equal("hello", (await client.GetResponseAsync(Messages(), Options())).Text);
      await manager.UpdateCustomAsync(connection.Id, Settings() with { Endpoint = new Uri("https://changed.invalid/v1/") });
      var failure = await Assert.ThrowsAsync<ProviderOperationException>(() => client.GetResponseAsync(Messages(), Options()));
      Assert.Equal(ProviderFailure.ConnectionChanged, failure.Failure);
      Assert.Single(handler.Requests);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task DisconnectAndShutdownJoinStreamingCleanup(bool disconnect) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ScriptedOpenAIHandler(Stream(LlmApi.OpenAIResponses)) { HoldAtEnd = true };
    var manager = Manager(store, handler);
    Task<ChatResponse>? response = null;
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      using IChatClient client = manager.CreateChatClient(connection);
      response = client.GetResponseAsync(Messages(), Options());
      await handler.ReachedEnd.Task.WaitAsync(TestTimeout);
      Assert.False(response.IsCompleted);
      if (disconnect) {
        ProviderDisconnectResult result = await manager.DisconnectAsync(connection.Id).WaitAsync(TestTimeout);
        Assert.True(result.LocalCredentialsCleared);
      } else {
        await manager.ShutdownAsync().WaitAsync(TestTimeout);
      }
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response.WaitAsync(TestTimeout));
      Assert.True(handler.StreamDisposals > 0);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      if (response is not null) {
        try {
          await response.WaitAsync(TestTimeout);
        } catch (OperationCanceledException) {
          // Observe owned cancellation before disposing the manager and store.
        }
      }
      manager.Dispose();
    }
  }

  [Fact]
  public async Task DisposingPartialEnumerationReleasesRequestWithoutStoppingTheManager() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ScriptedOpenAIHandler(Stream(LlmApi.OpenAIResponses));
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      using IChatClient client = manager.CreateChatClient(connection);
      await using (var stream = client.GetStreamingResponseAsync(Messages(), Options()).GetAsyncEnumerator()) {
        Assert.True(await stream.MoveNextAsync());
        Assert.Null(stream.Current.FinishReason);
      }
      Assert.True(handler.StreamDisposals > 0);
      Assert.Equal("hello", (await client.GetResponseAsync(Messages(), Options()).WaitAsync(TestTimeout)).Text);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  [Fact]
  public async Task CallerCancellationDoesNotDisposeTheBorrowedClient() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ScriptedOpenAIHandler(Stream(LlmApi.OpenAIResponses));
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      using IChatClient client = manager.CreateChatClient(connection);
      using var cancelled = new CancellationTokenSource();
      await cancelled.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync(Messages(), Options(), cancelled.Token));
      Assert.Empty(handler.Requests);
      Assert.Equal("hello", (await client.GetResponseAsync(Messages(), Options()).WaitAsync(TestTimeout)).Text);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task TerminalUpdateAlreadyHasManagerAcceptanceAndCleanup(LlmApi api) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ScriptedOpenAIHandler(Stream(api));
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(api, AuthenticationMethod.ApiKey));
      await manager.SetApiKeyAsync(connection, "synthetic-first-key");
      using IChatClient client = manager.CreateChatClient(connection);
      await using var stream = client.GetStreamingResponseAsync(Messages(), Options()).GetAsyncEnumerator();
      ChatResponseUpdate? final = null;
      while (await stream.MoveNextAsync()) {
        if (stream.Current.FinishReason is null) continue;
        final = stream.Current;
        break;
      }
      Assert.NotNull(final);
      Assert.NotNull(LlmProviderContext.GetResponse(final));
      // A consumer may stop at the terminal update: there must be no unaccepted request left to join.
      await manager.SetApiKeyAsync(connection, "synthetic-second-key").WaitAsync(TestTimeout);
      Assert.False(await stream.MoveNextAsync());
      Assert.True(handler.StreamDisposals > 0);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  [Fact]
  public async Task ChangedGrantCannotPublishATerminalUpdateAfterPartialText() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var handler = new ScriptedOpenAIHandler(Stream(LlmApi.OpenAIResponses));
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(authentication: AuthenticationMethod.ApiKey));
      await manager.SetApiKeyAsync(connection, "synthetic-first-key");
      using IChatClient client = manager.CreateChatClient(connection);
      await using var stream = client.GetStreamingResponseAsync(Messages(), Options()).GetAsyncEnumerator();
      Assert.True(await stream.MoveNextAsync());
      Assert.Null(stream.Current.FinishReason);
      ProviderState current = store.Providers.Get(connection.Id);
      store.Providers.SetCredentials(connection.Id, current.Generation, null,
          new("synthetic-second-key", null, null, null, null, []));
      var failure = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
        while (await stream.MoveNextAsync()) Assert.Null(stream.Current.FinishReason);
      });
      Assert.Equal(ProviderFailure.ConnectionChanged, failure.Failure);
      Assert.True(handler.StreamDisposals > 0);
    } finally {
      await manager.ShutdownAsync().WaitAsync(TestTimeout);
      manager.Dispose();
    }
  }

  private static ProviderManager Manager(UrdStore store, HttpMessageHandler handler) =>
      new(store, () => new Uri("http://127.0.0.1:1234/auth/callback"), handler);

  private static CustomProviderSettings Settings(LlmApi api = LlmApi.OpenAIResponses,
      AuthenticationMethod authentication = AuthenticationMethod.None) =>
      new("managed", api, new Uri("https://fixture.invalid/v1/"), authentication);

  private static ChatOptions Options() => new() { ModelId = "fixture-model" };
  private static ChatMessage[] Messages() => [new(ChatRole.User, "hello")];
  private static string Stream(LlmApi api) => api == LlmApi.OpenAIResponses
      ? ProviderModelStreams.Responses() : OpenAIStreams.ChatStream("stop");
}
