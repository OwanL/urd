using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Core.Usage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;
using static Urd.Backend.Tests.Support.OpenAIStreams;

#pragma warning disable OPENAI001, MEAI001 // Exercise the pinned SDK's provider adapters.

namespace Urd.Backend.Tests.Usage;

public sealed class UsageProviderCaptureTests {
  [Fact]
  public async Task NonStreamingIncompleteResponseKeepsReportedUsage() {
    string response = IncompleteStream();
    using var database = new TemporaryDatabase("urd-provider-usage-response");
    using UrdStore store = UrdStore.Open(database.Path);
    using var handler = new ScriptedOpenAIHandler(response);
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      var selection = TestLlmSelection.Create(connection);
      using var usage = manager.CreateChatClient(connection);
      var options = new ChatOptions { ModelId = selection.ModelId };
      AddContext(options, new(Guid.NewGuid(), Guid.NewGuid(), "conversation"));
      await Assert.ThrowsAsync<ProviderOperationException>(() => usage.GetResponseAsync(
          [new ChatMessage(ChatRole.User, "hello")], options));

      UsageRequest captured = Assert.Single(Recent(store));
      Assert.Equal<long?>(18, captured.Tokens!.InputTokens);
      Assert.Equal<long?>(2, captured.Tokens.OutputTokens);
      Assert.Equal<long?>(5, captured.Tokens.CachedInputTokens);
      Assert.Null(captured.CostUsd); // The fixture model has no verified reference rate.
      Assert.False(captured.IsEstimate);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task FactoryAccountsDirectAndExplicitPurposesWithoutSessionAttribution() {
    using var database = new TemporaryDatabase("urd-provider-usage-context");
    using UrdStore store = UrdStore.Open(database.Path);
    using var handler = new ScriptedOpenAIHandler(ProviderModelStreams.Responses(usage: new {
      input_tokens = 18, output_tokens = 2, total_tokens = 20,
      input_tokens_details = new { cached_tokens = 5 }
    }));
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      using var client = manager.CreateChatClient(connection);
      ChatMessage[] messages = [new(ChatRole.User, "hello")];
      await client.GetResponseAsync(messages, new ChatOptions { ModelId = "fixture-model" });

      var title = new ChatOptions { ModelId = "fixture-model" };
      AddContext(title, new(null, null, "title"));
      await client.GetResponseAsync(messages, title);

      var background = new ChatOptions { ModelId = "fixture-model" };
      AddContext(background, new(null, null, "background"));
      await client.GetResponseAsync(messages, background);

      UsageRequest[] captured = Recent(store).ToArray();
      Assert.Equal(3, handler.Requests.Count);
      Assert.All(handler.Requests, request => Assert.DoesNotContain(UsageContext.PropertyName, request.Body));
      Assert.Equal("title", Assert.IsType<UsageContext>(title.AdditionalProperties![UsageContext.PropertyName]).Purpose);
      Assert.Equal(new[] { "background", "model", "title" },
          captured.Select(request => request.Purpose).OrderBy(purpose => purpose, StringComparer.Ordinal).ToArray());
      Assert.All(captured, request => {
        Assert.Null(request.SessionId);
        Assert.Null(request.ExecutionId);
        Assert.Equal("fixture-model", request.Model);
        Assert.Equal<long?>(18, request.Tokens!.InputTokens);
        Assert.Equal<long?>(2, request.Tokens.OutputTokens);
        Assert.False(request.IsEstimate);
      });
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task ModelDiagnosticCapturesTerminalNativeUsage() {
    string wire = IncompleteStream().Replace("fixture-model", "GPT-6.2-Sol", StringComparison.Ordinal);
    using var database = new TemporaryDatabase("urd-provider-test-usage");
    using UrdStore store = UrdStore.Open(database.Path);
    using var handler = new ScriptedOpenAIHandler(wire);
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      await Assert.ThrowsAsync<ProviderOperationException>(() => manager.TestModelAsync(connection.Id, "requested-model"));

      UsageRequest captured = Assert.Single(Recent(store));
      Assert.Equal("provider test", captured.Purpose);
      Assert.Equal("GPT-6.2-Sol", captured.Model);
      Assert.Equal("openai/gpt-6.2-sol", captured.Rates?.Model);
      Assert.Equal(0.0000465m, captured.CostUsd);
      Assert.Equal<long?>(18, captured.Tokens!.InputTokens);
      Assert.Equal<long?>(2, captured.Tokens.OutputTokens);
      Assert.Equal<long?>(5, captured.Tokens.CachedInputTokens);
      Assert.False(captured.IsEstimate);
    } finally {
      await CloseAsync(manager);
    }
  }

  private static void AddContext(ChatOptions options, UsageContext context) =>
      options.AdditionalProperties = new() { [UsageContext.PropertyName] = context };

  private static IReadOnlyList<UsageRequest> Recent(UrdStore store) => store.ReadUsage(null,
      DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)).Recent;

  private static string IncompleteStream() {
    string wire = ResponseStream("response.incomplete");
    int terminal = wire.LastIndexOf("event: response.incomplete", StringComparison.Ordinal);
    return wire[..terminal] + wire[terminal..].Replace("\"usage\":null",
        "\"usage\":{\"input_tokens\":18,\"output_tokens\":2,\"total_tokens\":20,\"input_tokens_details\":{\"cached_tokens\":5}}",
        StringComparison.Ordinal);
  }

  private static ProviderManager Manager(UrdStore store, HttpMessageHandler handler) =>
      new(store, () => new Uri("http://localhost:54321/api/providers/callback"), handler);

  private static CustomProviderSettings Settings() =>
      new("Fixture", LlmApi.OpenAIResponses, new Uri("https://fixture.invalid/v1"), AuthenticationMethod.None);

  private static async Task CloseAsync(ProviderManager manager) {
    await manager.ShutdownAsync().WaitAsync(TestTimeout);
    manager.Dispose();
  }
}

#pragma warning restore OPENAI001, MEAI001
