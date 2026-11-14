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
      using var usage = new UsageChatClient(manager.CreateChatClient(connection), store.Usage, selection,
          Guid.NewGuid(), Guid.NewGuid(), "conversation");
      await Assert.ThrowsAsync<ProviderOperationException>(() => usage.GetResponseAsync(
          [new ChatMessage(ChatRole.User, "hello")]));

      UsageRequest captured = Assert.Single(Recent(store));
      Assert.Equal<long?>(18, captured.Tokens!.InputTokens);
      Assert.Equal<long?>(2, captured.Tokens.OutputTokens);
      Assert.Equal<long?>(5, captured.Tokens.CachedInputTokens);
      Assert.Null(captured.CostUsd); // Custom routes are deliberately unpriced.
      Assert.False(captured.IsEstimate);
    } finally {
      await CloseAsync(manager);
    }
  }

  [Fact]
  public async Task ModelDiagnosticCapturesTerminalNativeUsage() {
    string wire = IncompleteStream();
    using var database = new TemporaryDatabase("urd-provider-test-usage");
    using UrdStore store = UrdStore.Open(database.Path);
    using var handler = new ScriptedOpenAIHandler(wire);
    var manager = Manager(store, handler);
    try {
      ProviderConnection connection = await manager.AddCustomAsync(Settings());
      await Assert.ThrowsAsync<ProviderOperationException>(() => manager.TestModelAsync(connection.Id, "requested-model"));

      UsageRequest captured = Assert.Single(Recent(store));
      Assert.Equal("provider test", captured.Purpose);
      Assert.Equal("fixture-model", captured.Model);
      Assert.Equal<long?>(18, captured.Tokens!.InputTokens);
      Assert.Equal<long?>(2, captured.Tokens.OutputTokens);
      Assert.Equal<long?>(5, captured.Tokens.CachedInputTokens);
      Assert.False(captured.IsEstimate);
    } finally {
      await CloseAsync(manager);
    }
  }

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
