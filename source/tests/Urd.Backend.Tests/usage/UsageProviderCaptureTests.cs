using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Execution;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Core.Usage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;
using static Urd.Backend.Tests.Support.OpenAIStreams;

#pragma warning disable OPENAI001, MEAI001 // Exercise the pinned SDK's provider adapters.

namespace Urd.Backend.Tests.Usage;

public sealed class UsageProviderCaptureTests {
  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task NonStreamingIncompleteResponseKeepsReportedUsage(LlmApi api) {
    string response = IncompleteStream(api);
    using var database = new TemporaryDatabase("urd-provider-usage-response");
    using UrdStore store = UrdStore.Open(database.Path);
    using var handler = new ScriptedOpenAIHandler(response);
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(api));
      var selection = TestLlmSelection.Create(connection);
      using var usage = manager.CreateChatClient(connection);
      var options = new ChatOptions { ModelId = selection.ModelId };
      AddContext(options, new(Guid.NewGuid(), Guid.NewGuid(), "conversation"));
      var failure = await Assert.ThrowsAsync<ProviderOperationException>(() => usage.GetResponseAsync(
          [new ChatMessage(ChatRole.User, "hello")], options));
      Assert.Equal(ProviderFailure.IncompleteResponse, failure.Failure);

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

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task ModelDiagnosticCapturesTerminalNativeUsage(LlmApi api) {
    string wire = IncompleteStream(api).Replace("fixture-model", "GPT-6.2-Sol", StringComparison.Ordinal);
    using var database = new TemporaryDatabase("urd-provider-test-usage");
    using UrdStore store = UrdStore.Open(database.Path);
    using var handler = new ScriptedOpenAIHandler(wire);
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(api));
      var failure = await Assert.ThrowsAsync<ProviderOperationException>(() => manager.TestModelAsync(connection.Id, "requested-model"));
      Assert.Equal(ProviderFailure.IncompleteResponse, failure.Failure);
      if (api == LlmApi.OpenAIChatCompletions) {
        using var sent = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.True(sent.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
      }

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

  [Theory]
  [InlineData("{}", null, true, 0.000056)]
  [InlineData("{\"cached_tokens\":0}", 0, false, 0.000056)]
  [InlineData("{\"cached_tokens\":-1}", null, true, null)]
  [InlineData("{\"cached_tokens\":19}", null, true, null)]
  [InlineData("{\"cached_tokens\":0,\"cache_write_tokens\":\"zero\"}", 0, true, null)]
  [InlineData("{\"cached_tokens\":0,\"cache_write_tokens\":null}", 0, true, null)]
  [InlineData("{\"cached_tokens\":0,\"cache_write_tokens\":0,\"cache_write_tokens_details\":{\"ttl_30m_tokens\":0.5}}", 0, true, null)]
  public async Task CacheCountsDistinguishAbsentZeroAndInvalid(string cache, int? cached, bool estimated, double? cost) {
    var details = JsonSerializer.Deserialize<JsonElement>(cache);
    string wire = ChatStream(usage: new { prompt_tokens = 18, completion_tokens = 2, total_tokens = 20, prompt_tokens_details = details })
        .Replace("fixture-model", "GPT-6.2-Sol", StringComparison.Ordinal);
    using var database = new TemporaryDatabase("urd-provider-usage-cache-counts");
    using UrdStore store = UrdStore.Open(database.Path);
    using var handler = new ScriptedOpenAIHandler(wire);
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(LlmApi.OpenAIChatCompletions));
      using var client = manager.CreateChatClient(connection);
      await client.GetResponseAsync([new(ChatRole.User, "hello")], new ChatOptions { ModelId = "GPT-6.2-Sol" });

      var captured = Assert.Single(Recent(store));
      Assert.Equal((decimal?)cost, captured.CostUsd);
      Assert.Equal(estimated, captured.IsEstimate);
      Assert.Equal<long?>(cached, captured.Tokens!.CachedInputTokens);
      Assert.Equal<long?>(18, captured.Tokens.InputTokens);
      Assert.Equal<long?>(2, captured.Tokens.OutputTokens);
      if (cost is null && cached is not null) {
        Assert.Null(captured.Tokens.CacheWrite30mTokens);
        Assert.Null(captured.Tokens.CacheWrite12hTokens);
      }
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task CompatibleChatSamplesTheSerializedPrefixAndExcludesTheColdRequest() {
    const int input = 5000;
    const int cached = 100;
    using var database = new TemporaryDatabase("urd-prompt-cache-wire");
    using UrdStore store = UrdStore.Open(database.Path);
    using var handler = new ScriptedOpenAIHandler(ChatStream(usage: new {
      prompt_tokens = input, completion_tokens = 2, total_tokens = input + 2,
      prompt_tokens_details = new { cached_tokens = cached }
    }));
    var manager = Manager(store, handler);
    var monitor = new PromptCacheMonitor();
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    var stamp = new ContextStamp(0, null, 0);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(LlmApi.OpenAIChatCompletions));
      using var client = manager.CreateChatClient(connection);
      List<ChatMessage> messages = [new(ChatRole.System, "Stable synthetic prefix.")];
      for (int request = 1; request <= 3; request++) {
        messages.Add(new(ChatRole.User, $"Continuation {request}."));
        var options = new ChatOptions { ModelId = "fixture-model" };
        AddContext(options, new(session, Guid.NewGuid(), "conversation") { PromptCache = (monitor, stamp) });
        await client.GetResponseAsync(messages, options);
        if (request <= 2)
          Assert.Null(store.Usage.ReadPromptCache(session, monitor.ReadWarning()).Warning);
        messages.Add(new(ChatRole.Assistant, "Fixture reply."));
      }

      Assert.Equal(3, handler.Requests.Count);
      Assert.All(handler.Requests, request => {
        using JsonDocument wire = JsonDocument.Parse(request.Body);
        Assert.True(wire.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Empty(request.SessionIds!);
      });
      PromptCacheSummary summary = store.Usage.ReadPromptCache(session, monitor.ReadWarning());
      Assert.Equal(new PromptCacheTotals(input * 2, cached * 2, 2, 2), summary.Warning);
      Assert.Equal(new PromptCacheTotals(input * 3, cached * 3, 3, 3), summary.Session);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task CompatibleChatWithoutCacheDetailsDoesNotInventAZero() {
    using var database = new TemporaryDatabase("urd-ollama-cache-unreported");
    using UrdStore store = UrdStore.Open(database.Path);
    using var handler = new ScriptedOpenAIHandler(ChatStream(usage: new {
      prompt_tokens = 5000, completion_tokens = 2, total_tokens = 5002
    }));
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings(LlmApi.OpenAIChatCompletions));
      using var client = manager.CreateChatClient(connection);
      await client.GetResponseAsync([new(ChatRole.User, "hello")], new ChatOptions { ModelId = "fixture-model" });
      UsageRequest captured = Assert.Single(Recent(store));
      Assert.Equal(5000, captured.Tokens!.InputTokens);
      Assert.Null(captured.Tokens.CachedInputTokens);
    } finally {
      await CloseAsync(manager);
    }
  }

  private static void AddContext(ChatOptions options, UsageContext context) =>
      options.AdditionalProperties = new() { [UsageContext.PropertyName] = context };

  private static IReadOnlyList<UsageRequest> Recent(UrdStore store) => store.ReadUsage(null,
      DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)).Recent;

  private static string IncompleteStream(LlmApi api) {
    if (api == LlmApi.OpenAIChatCompletions) return ChatStream("length", new {
      prompt_tokens = 18, completion_tokens = 2, total_tokens = 20, prompt_tokens_details = new { cached_tokens = 5 }
    });
    string wire = ResponseStream("response.incomplete");
    int terminal = wire.LastIndexOf("event: response.incomplete", StringComparison.Ordinal);
    return wire[..terminal] + wire[terminal..].Replace("\"usage\":null",
        "\"usage\":{\"input_tokens\":18,\"output_tokens\":2,\"total_tokens\":20,\"input_tokens_details\":{\"cached_tokens\":5}}",
        StringComparison.Ordinal);
  }

  private static ProviderManager Manager(UrdStore store, HttpMessageHandler handler) =>
      new(store, () => new Uri("http://localhost:54321/api/providers/callback"), handler);

  private static CustomProviderSettings Settings(LlmApi api = LlmApi.OpenAIResponses) =>
      new("Fixture", api, new Uri("https://fixture.invalid/v1"), AuthenticationMethod.None);

  private static async Task CloseAsync(ProviderManager manager) {
    await manager.ShutdownAsync().WaitAsync(TestTimeout);
    manager.Dispose();
  }
}

#pragma warning restore OPENAI001, MEAI001
