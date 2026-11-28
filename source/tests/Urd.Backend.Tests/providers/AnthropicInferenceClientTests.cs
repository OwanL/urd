using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Providers;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

public sealed class AnthropicInferenceClientTests {
  private const string Model = "claude-fixture";
  private const string Bearer = "synthetic-copilot-bearer";
  private static readonly byte[] png = [1, 2, 3];
  private static readonly string messageStart = Event("message_start", """
      {"message":{"type":"message","id":"msg_round","model":"resolved-alias","role":"assistant","content":[],"stop_reason":null,
        "usage":{"input_tokens":14,"cache_read_input_tokens":3,"cache_creation_input_tokens":0,"output_tokens":1}}}
      """);
  private static readonly string textBlock = Event("content_block_start", """{"index":0,"content_block":{"type":"text","text":""}}""") +
      Event("content_block_delta", """{"index":0,"delta":{"type":"text_delta","text":"Found it."}}""") + BlockStop(0);
  private static readonly string thinkingBlocks = Event("content_block_start", """{"index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""") +
      Event("content_block_delta", """{"index":0,"delta":{"type":"thinking_delta","thinking":"private reasoning"}}""") +
      Event("content_block_delta", """{"index":0,"delta":{"type":"signature_delta","signature":"signed-for-original-scope"}}""") + BlockStop(0) +
      Event("content_block_start", """{"index":1,"content_block":{"type":"redacted_thinking","data":"opaque-reasoning"}}""") + BlockStop(1);
  private static readonly string messageStop = Event("message_stop", "{}");

  [Fact]
  public async Task ToolRoundTripReplaysNativeThinkingOnlyInItsOriginalScope() {
    using var handler = new FakeHandler(Replies(
        new FixtureStream(messageStart + thinkingBlocks + ToolBlock(2) + End("tool_use")),
        new FixtureStream(messageStart + textBlock + End()), new FixtureStream(messageStart + textBlock + End())));
    using var transport = new HttpClient(handler);
    var client = new AnthropicInferenceClient(transport);
    ProviderState state = State(maxOutputTokens: 7200);
    ChatOptions options = Options();
    options.Instructions = "top-level instruction";
    options.AdditionalProperties = new() { [OpenAIInferenceClient.SessionIdProperty] = Guid.NewGuid() };
    ChatMessage[] input = [new(ChatRole.System, "system input"), new(ChatRole.User, [
      new TextContent("look it up"), new DataContent(png, "image/png")
    ])];

    List<ChatResponseUpdate> first = await CollectAsync(client, state, input, options);
    Assert.Equal("private reasoning", Assert.Single(first.SelectMany(update => update.Contents).OfType<TextReasoningContent>()).Text);
    var final = Assert.Single(first, update => update.FinishReason is not null);
    Assert.Same(first[^1], final);
    Assert.Equal(ChatFinishReason.ToolCalls, final.FinishReason);
    FunctionCallContent call = Assert.IsType<FunctionCallContent>(Assert.Single(final.Contents));
    Assert.Equal("toolu_lookup", call.CallId);
    Assert.Equal("lookup", call.Name);
    Assert.Equal("key", Assert.IsType<JsonElement>(call.Arguments!["key"]).GetString());
    Assert.DoesNotContain(first[..^1], update => update.Contents.Any(part => part is FunctionCallContent));

    UsageDetails usage = AddUsage(first);
    Assert.Equal(17, usage.InputTokenCount);
    Assert.Equal(3, usage.CachedInputTokenCount);
    Assert.Equal(7, usage.OutputTokenCount);
    Assert.Equal(24, usage.TotalTokenCount);
    Assert.Equal(0, usage.AdditionalCounts!["cache_write_30m_tokens"]);

    var native = LlmProviderContext.GetResponse(final)!;
    Assert.Equal(Model, native.Scope.ModelId);
    ChatMessage assistant = LlmProviderContext.Restore(native);
    var result = new FunctionResultContent(call.CallId, new AIContent[] {
      new TextContent("tool output"), new DataContent(png, "image/png")
    }) { Exception = new InvalidOperationException("test-only detail") };
    ChatMessage[] continued = [.. input, assistant, new(ChatRole.Tool, [result])];
    List<ChatResponseUpdate> second = await CollectAsync(client, state, continued, options);
    Assert.Equal("Found it.", Assert.Single(second.SelectMany(update => update.Contents).OfType<TextContent>()).Text);
    Assert.Equal(ChatFinishReason.Stop, second[^1].FinishReason);
    await CollectAsync(client, state with { Generation = state.Generation + 1 }, continued, options);

    Assert.Equal(3, handler.Requests.Count);
    var firstRequest = handler.Requests[0];
    Assert.Equal("https://copilot.example.test/v1/messages", firstRequest.Endpoint);
    Assert.Equal($"Bearer {Bearer}", firstRequest.Authorization);
    Assert.Equal("2023-06-01", firstRequest.AnthropicVersion);
    Assert.Equal("true", firstRequest.VisionRequest);
    Assert.Equal("user", firstRequest.Initiator);
    using var firstBody = JsonDocument.Parse(firstRequest.Body);
    JsonElement root = firstBody.RootElement;
    Assert.Equal(Model, root.GetProperty("model").GetString());
    Assert.Equal(7200, root.GetProperty("max_tokens").GetInt32());
    Assert.True(root.GetProperty("stream").GetBoolean());
    Assert.False(root.TryGetProperty("thinking", out _));
    Assert.False(root.TryGetProperty(OpenAIInferenceClient.SessionIdProperty, out _));
    Assert.Equal(["top-level instruction", "system input"], root.GetProperty("system").EnumerateArray()
        .Select(block => block.GetProperty("text").GetString()));
    Assert.Equal("lookup", root.GetProperty("tools")[0].GetProperty("name").GetString());
    Assert.Equal("AQID", root.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("source").GetProperty("data").GetString());

    Assert.Equal("agent", handler.Requests[1].Initiator);
    using var replay = JsonDocument.Parse(handler.Requests[1].Body);
    JsonElement replayMessages = replay.RootElement.GetProperty("messages");
    JsonElement nativeBlocks = replayMessages[1].GetProperty("content");
    Assert.Equal(["thinking", "redacted_thinking", "tool_use"], nativeBlocks.EnumerateArray()
        .Select(block => block.GetProperty("type").GetString()));
    Assert.Equal("signed-for-original-scope", nativeBlocks[0].GetProperty("signature").GetString());
    Assert.Equal("opaque-reasoning", nativeBlocks[1].GetProperty("data").GetString());
    Assert.Equal("user", replayMessages[2].GetProperty("role").GetString());
    JsonElement resultBlock = Assert.Single(replayMessages[2].GetProperty("content").EnumerateArray());
    Assert.Equal("tool_result", resultBlock.GetProperty("type").GetString());
    Assert.Equal(call.CallId, resultBlock.GetProperty("tool_use_id").GetString());
    Assert.True(resultBlock.GetProperty("is_error").GetBoolean());
    Assert.Equal("image/png", resultBlock.GetProperty("content")[1].GetProperty("source").GetProperty("media_type").GetString());
    Assert.DoesNotContain("test-only detail", handler.Requests[1].Body, StringComparison.Ordinal);
    using var switched = JsonDocument.Parse(handler.Requests[2].Body);
    JsonElement portable = Assert.Single(switched.RootElement.GetProperty("messages")[1].GetProperty("content").EnumerateArray());
    Assert.Equal("tool_use", portable.GetProperty("type").GetString());
    Assert.Equal(call.CallId, portable.GetProperty("id").GetString());
    Assert.DoesNotContain("private reasoning", handler.Requests[2].Body, StringComparison.Ordinal);
    Assert.DoesNotContain("signed-for-original-scope", handler.Requests[2].Body, StringComparison.Ordinal);
    Assert.DoesNotContain("opaque-reasoning", handler.Requests[2].Body, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("missing-stop", ProviderFailure.IncompleteResponse)]
  [InlineData("unframed-stop", ProviderFailure.IncompleteResponse)]
  [InlineData("max-tokens", ProviderFailure.IncompleteResponse)]
  [InlineData("wrong-finish", ProviderFailure.InvalidProviderResponse)]
  [InlineData("late-error", ProviderFailure.ProviderUnavailable)]
  [InlineData("unknown-block", ProviderFailure.InvalidProviderResponse)]
  [InlineData("dispose-error", ProviderFailure.ProviderUnavailable)]
  public async Task PartialAndFailedStreamsNeverPublishAnExecutableCall(string variant, ProviderFailure failure) {
    string afterTool = variant switch {
      "missing-stop" => Delta("tool_use"),
      "unframed-stop" => Delta("tool_use") + messageStop.TrimEnd('\n'),
      "max-tokens" => End("max_tokens"),
      "wrong-finish" => End(),
      "late-error" => End("tool_use") + Event("error", """{"error":{"type":"overloaded_error","message":"Service busy."}}"""),
      "unknown-block" => Event("content_block_start", """{"index":1,"content_block":{"type":"server_tool_use"}}"""),
      _ => End("tool_use")
    };
    using var stream = new FixtureStream(messageStart + ToolBlock(0) + afterTool, failDisposal: variant == "dispose-error");
    using var handler = new FakeHandler(Replies(stream));
    using var transport = new HttpClient(handler);
    var observed = new List<ChatResponseUpdate>();

    var error = await Assert.ThrowsAsync<ProviderOperationException>(() => CollectAsync(new(transport), State(), History(), Options(), observed));

    Assert.Equal(failure, error.Failure);
    Assert.All(observed, update => {
      Assert.Null(update.FinishReason);
      Assert.Null(LlmProviderContext.GetResponse(update));
      Assert.DoesNotContain(update.Contents, part => part is FunctionCallContent);
    });
    Assert.Equal(17, AddUsage(observed).InputTokenCount);
    Assert.True(stream.Disposed);
  }

  [Fact]
  public async Task PublishesFinalOnlyAfterCleanEofAndDisposal() {
    using var stream = new FixtureStream(messageStart + ToolBlock(0) + End("tool_use"));
    using var handler = new FakeHandler(Replies(stream));
    using var transport = new HttpClient(handler);
    int finals = 0;
    using var timeout = new CancellationTokenSource(TestTimeout);
    await foreach (ChatResponseUpdate update in new AnthropicInferenceClient(transport).GetStreamingResponseAsync(
        State(), History(), Options(), timeout.Token)) {
      if (update.FinishReason is null) {
        Assert.False(stream.Disposed);
      } else {
        Assert.True(stream.Disposed);
        Assert.Equal(ChatFinishReason.ToolCalls, update.FinishReason);
        finals++;
      }
    }
    Assert.Equal(1, finals);
    using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
    Assert.Equal(4096, body.RootElement.GetProperty("max_tokens").GetInt32());
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task HeldEofAfterPositiveTerminalCanBeCancelledOrTimeOut(bool cancel) {
    using var caller = new CancellationTokenSource(TestTimeout);
    using var stream = new FixtureStream(messageStart + ToolBlock(0) + End("tool_use"), holdEof: true);
    using var handler = new FakeHandler(Replies(stream));
    using var transport = new HttpClient(handler);
    var observed = new List<ChatResponseUpdate>();
    var client = new AnthropicInferenceClient(transport, cancel ? TestTimeout : TimeSpan.FromMilliseconds(200));
    Task reading = CollectAsync(client, State(), History(), Options(), observed, caller.Token);
    await stream.EofReadStarted.Task.WaitAsync(TestTimeout);
    if (cancel) {
      await caller.CancelAsync();
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    } else {
      var error = await Assert.ThrowsAsync<ProviderOperationException>(() => reading);
      Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
      Assert.Equal("Timed out waiting for the next provider stream event.", error.Reason);
    }
    Assert.DoesNotContain(observed, update => update.FinishReason is not null || update.Contents.Any(part => part is FunctionCallContent));
    Assert.True(stream.Disposed);
  }

  [Theory]
  [InlineData("rate_limit_error", ProviderFailure.RateLimited, true)]
  [InlineData("insufficient_quota", ProviderFailure.ProviderUnavailable, false)]
  public async Task HttpRateLimitPreservesStatusDelayAndRedactsCredentials(string code, ProviderFailure failure, bool retryable) {
    using var handler = new FakeHandler([() => new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
      Content = new StringContent(JsonSerializer.Serialize(new { error = new { type = code, message = $"Error for {Bearer}" } })),
      Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2)) }
    }]);
    using var transport = new HttpClient(handler);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() => CollectAsync(new(transport), State(), History(), Options()));
    Assert.Equal(failure, error.Failure);
    Assert.Equal(429, error.DiagnosticCause?.HttpStatus);
    Assert.Equal(retryable, error.IsRetryableRateLimit);
    Assert.Equal(retryable ? TimeSpan.FromSeconds(2) : null, error.RetryAfter);
    Assert.DoesNotContain(Bearer, error.Message, StringComparison.Ordinal);
    Assert.Contains("[redacted]", error.Message, StringComparison.Ordinal);
  }

  private static async Task<List<ChatResponseUpdate>> CollectAsync(AnthropicInferenceClient client, ProviderState state,
      ChatMessage[] history, ChatOptions options, List<ChatResponseUpdate>? updates = null, CancellationToken cancellationToken = default) {
    updates ??= [];
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TestTimeout);
    await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(state, history, options, timeout.Token))
      updates.Add(update);
    return updates;
  }

  private static UsageDetails AddUsage(IEnumerable<ChatResponseUpdate> updates) {
    var total = new UsageDetails();
    foreach (UsageContent usage in updates.SelectMany(update => update.Contents).OfType<UsageContent>())
      total.Add(usage.Details);
    return total;
  }

  private static ChatOptions Options() => new() { ModelId = Model,
    Tools = [AIFunctionFactory.Create((string key) => key, "lookup")] };

  private static ChatMessage[] History() => [new(ChatRole.User, "hello")];

  private static ProviderState State(int? maxOutputTokens = null) {
    var id = Guid.NewGuid();
    return new(new(id, "Copilot", ProviderRoute.GitHubCopilot, LlmApi.AnthropicMessages,
        new("https://copilot.example.test/"), AuthenticationMethod.OAuth), 4, 2, null,
        new(null, Bearer, null, null, null, []), false, maxOutputTokens is null ? null :
          new ProviderModelCatalog(id, DateTimeOffset.UtcNow, [new(Model, "Claude", MaxOutputTokens: maxOutputTokens)]));
  }

  private static string Event(string name, string body) => $"event: {name}\ndata: {{\"type\":\"{name}\"" +
      (body == "{}" ? "}" : "," + body[1..].Replace("\n", "", StringComparison.Ordinal)) + "\n\n";

  private static string BlockStop(int index) => Event("content_block_stop", $$"""{"index":{{index}}}""");

  private static string ToolBlock(int index) => Event("content_block_start", JsonSerializer.Serialize(new {
    index, content_block = new { type = "tool_use", id = "toolu_lookup", name = "lookup", input = new { } }
  })) + ToolDelta(index, "{\"key\":") + ToolDelta(index, "\"key\"}") + BlockStop(index);

  private static string ToolDelta(int index, string fragment) => Event("content_block_delta", JsonSerializer.Serialize(new {
    index, delta = new { type = "input_json_delta", partial_json = fragment }
  }));

  private static string Delta(string reason) => Event("message_delta", JsonSerializer.Serialize(new {
    delta = new { stop_reason = reason }, usage = new { output_tokens = 7 }
  }));

  private static string End(string reason = "end_turn") => Delta(reason) + messageStop;

  private static IEnumerable<Func<HttpResponseMessage>> Replies(params FixtureStream[] streams) => streams.Select<FixtureStream, Func<HttpResponseMessage>>(stream => () =>
      new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) {
        Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") }
      } });

  private sealed record CapturedRequest(string Endpoint, string? Authorization, string? AnthropicVersion,
      string? Initiator, string? VisionRequest, string Body);

  private sealed class FakeHandler(IEnumerable<Func<HttpResponseMessage>> responses) : HttpMessageHandler {
    private readonly Queue<Func<HttpResponseMessage>> replies = new(responses);
    internal List<CapturedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      Requests.Add(new(request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), Header("anthropic-version"),
          Header("x-initiator"), Header("copilot-vision-request"), await request.Content!.ReadAsStringAsync(cancellationToken)));
      return replies.Dequeue()();

      string? Header(string key) => request.Headers.TryGetValues(key, out var values) ? values.Single() : null;
    }
  }

  private sealed class FixtureStream(string sse, bool holdEof = false, bool failDisposal = false) : MemoryStream(Encoding.UTF8.GetBytes(sse)) {
    internal bool Disposed { get; private set; }
    internal TaskCompletionSource EofReadStarted { get; } = Signal();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
      if (holdEof && Position == Length) {
        EofReadStarted.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
      }
      return await base.ReadAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing) {
      base.Dispose(disposing);
      if (Disposed) return;
      Disposed = true;
      if (failDisposal) throw new IOException("test-only disposal detail");
    }
  }
}
