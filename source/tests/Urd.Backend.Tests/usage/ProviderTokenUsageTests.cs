using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Xunit;
using static Urd.Backend.Tests.Support.OpenAIStreams;

#pragma warning disable OPENAI001, MEAI001 // Exercise the pinned SDK's native models and streaming adapter.

namespace Urd.Backend.Tests.Usage;

public sealed class ProviderTokenUsageTests {
  [Fact]
  public void ResponsesPreserveExclusiveCacheWriteBucketsAndReasoning() {
    UsageDetails usage = Assert.IsType<UsageDetails>(ProviderTokenUsage.Response(Read<ResponseTokenUsage>("""
        {"input_tokens":12000,"output_tokens":13,"total_tokens":12013,
         "input_tokens_details":{"cached_tokens":4000,"cache_write_tokens":7000,
           "cache_write_tokens_details":{"ttl_30m_tokens":3000,"ttl_12h_tokens":4000}},
         "output_tokens_details":{"reasoning_tokens":5}}
        """)));

    Assert.Equal<long?>(12000, usage.InputTokenCount);
    Assert.Equal<long?>(13, usage.OutputTokenCount);
    Assert.Equal<long?>(12013, usage.TotalTokenCount);
    Assert.Equal<long?>(4000, usage.CachedInputTokenCount);
    Assert.Equal<long?>(5, usage.ReasoningTokenCount);
    Assert.NotNull(usage.AdditionalCounts);
    Assert.Equal(3000, usage.AdditionalCounts["cache_write_30m_tokens"]);
    Assert.Equal(4000, usage.AdditionalCounts["cache_write_12h_tokens"]);
    Assert.Equal(1000L, usage.InputTokenCount!.Value - usage.CachedInputTokenCount!.Value -
        usage.AdditionalCounts["cache_write_30m_tokens"] - usage.AdditionalCounts["cache_write_12h_tokens"]);
  }

  [Fact]
  public void ChatUsagePreservesOptionalCountsAndDoesNotAddReasoningToOutput() {
    UsageDetails usage = Assert.IsType<UsageDetails>(ProviderTokenUsage.Chat(Read<ChatTokenUsage>("""
        {"prompt_tokens":20,"completion_tokens":10,"total_tokens":30,
         "prompt_tokens_details":{"cached_tokens":4,"audio_tokens":2,"cache_write_tokens":3,
           "cache_write_tokens_details":{"ttl_30m_tokens":1,"ttl_12h_tokens":2}},
         "completion_tokens_details":{"reasoning_tokens":8,"audio_tokens":1}}
        """)));

    Assert.Equal<long?>(20, usage.InputTokenCount);
    Assert.Equal<long?>(10, usage.OutputTokenCount);
    Assert.Equal<long?>(30, usage.TotalTokenCount);
    Assert.Equal<long?>(4, usage.CachedInputTokenCount);
    Assert.Equal<long?>(8, usage.ReasoningTokenCount);
    Assert.Equal<long?>(2, usage.InputAudioTokenCount);
    Assert.Equal<long?>(1, usage.OutputAudioTokenCount);
    Assert.NotNull(usage.AdditionalCounts);
    Assert.Equal(1, usage.AdditionalCounts["unsupported_pricing_tokens"]);
  }

  [Fact]
  public void DiagnosticProjectionPreservesUnknownAndExplicitZeroCounts() {
    ProviderUsage response = Assert.IsType<ProviderUsage>(ProviderTokenUsage.ToProviderUsage(
        ProviderTokenUsage.Response(Read<ResponseTokenUsage>("""
        {"input_tokens":0,"output_tokens":4,"total_tokens":4,"input_tokens_details":{"cached_tokens":0}}
        """))));
    ProviderUsage chat = Assert.IsType<ProviderUsage>(ProviderTokenUsage.ToProviderUsage(
        ProviderTokenUsage.Chat(Read<ChatTokenUsage>("""
        {"prompt_tokens":5,"completion_tokens":0}
        """))));

    Assert.Equal<long?>(0, response.InputTokens);
    Assert.Equal<long?>(4, response.OutputTokens);
    Assert.Equal<long?>(4, response.TotalTokens);
    Assert.Equal<long?>(0, response.CachedInputTokens);
    Assert.Equal<long?>(5, chat.InputTokens);
    Assert.Equal<long?>(0, chat.OutputTokens);
    Assert.Null(chat.TotalTokens);
    Assert.Null(chat.CachedInputTokens);
  }

  [Fact]
  public void MissingCountsStayUnknownAndAbsentCacheWritesAreZero() {
    UsageDetails usage = Assert.IsType<UsageDetails>(ProviderTokenUsage.Response(Read<ResponseTokenUsage>("""
        {"input_tokens_details":{"audio_tokens":0},"output_tokens_details":{"reasoning_tokens":7}}
        """)));

    Assert.Null(usage.InputTokenCount);
    Assert.Null(usage.OutputTokenCount);
    Assert.Null(usage.TotalTokenCount);
    Assert.Null(usage.CachedInputTokenCount);
    Assert.Equal<long?>(7, usage.ReasoningTokenCount);
    Assert.NotNull(usage.AdditionalCounts);
    Assert.Equal(0, usage.AdditionalCounts["cache_write_30m_tokens"]);
    Assert.Equal(0, usage.AdditionalCounts["cache_write_12h_tokens"]);
  }

  [Theory]
  [InlineData("{\"input_tokens\":20,\"input_tokens_details\":{\"cached_tokens\":2,\"cache_write_tokens\":3}}")]
  [InlineData("{\"input_tokens\":20,\"input_tokens_details\":{\"cached_tokens\":2,\"cache_write_tokens\":3,\"cache_write_tokens_details\":{\"ttl_30m_tokens\":1,\"ttl_12h_tokens\":1}}}")]
  [InlineData("{\"input_tokens\":10,\"input_tokens_details\":{\"cached_tokens\":8,\"cache_write_tokens\":3,\"cache_write_tokens_details\":{\"ttl_30m_tokens\":1,\"ttl_12h_tokens\":2}}}")]
  public void MissingOrInconsistentCacheWriteBreakdownIsUnknown(string json) {
    UsageDetails usage = Assert.IsType<UsageDetails>(ProviderTokenUsage.Response(Read<ResponseTokenUsage>(json)));

    Assert.NotNull(usage.AdditionalCounts);
    Assert.False(usage.AdditionalCounts.ContainsKey("cache_write_30m_tokens"));
    Assert.False(usage.AdditionalCounts.ContainsKey("cache_write_12h_tokens"));
    Assert.Equal(1, usage.AdditionalCounts[ProviderTokenUsage.InvalidCacheWrites]);
  }

  [Fact]
  public async Task IncompleteNativeResponseUsageIsEmittedBeforeValidation() {
    string wire = ResponseStream("response.incomplete");
    int terminalIndex = wire.LastIndexOf("event: response.incomplete", StringComparison.Ordinal);
    Assert.NotEqual(-1, terminalIndex);
    wire = wire[..terminalIndex] + wire[terminalIndex..].Replace("\"usage\":null",
        "\"usage\":{\"input_tokens\":10,\"output_tokens\":2,\"total_tokens\":12,\"input_tokens_details\":{\"cached_tokens\":3}}",
        StringComparison.Ordinal);
    using var handler = new ScriptedOpenAIHandler(wire);
    using var http = new HttpClient(handler);
    var state = new ProviderState(new(Guid.NewGuid(), "Fixture", ProviderRoute.Custom, LlmApi.OpenAIResponses,
        new Uri("https://fixture.invalid/v1"), AuthenticationMethod.None), 1, 1, null, null, false, null);
    var options = new ChatOptions { ModelId = "fixture-model" };
    List<ChatResponseUpdate> observed = [];

    await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (ChatResponseUpdate update in new OpenAIInferenceClient(http).GetStreamingResponseAsync(
          state, [new ChatMessage(ChatRole.User, "hello")], options, default))
        observed.Add(update);
    });

    ChatResponseUpdate usageUpdate = Assert.Single(observed, update => update.Contents.Any(content => content is UsageContent));
    Assert.Equal("fixture-model", usageUpdate.ModelId);
    UsageContent content = Assert.IsType<UsageContent>(Assert.Single(usageUpdate.Contents));
    Assert.Equal<long?>(3, content.Details.CachedInputTokenCount);
  }

  private static T Read<T>(string json) where T : class => ModelReaderWriter.Read<T>(BinaryData.FromString(json))!;
}

#pragma warning restore OPENAI001, MEAI001
