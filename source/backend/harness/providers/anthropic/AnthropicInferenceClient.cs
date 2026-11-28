using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using static Urd.Core.Providers.AnthropicJson;

namespace Urd.Core.Providers;

// The manager owns request authorization, credentials and the borrowed HTTP transport.
internal sealed class AnthropicInferenceClient(HttpClient httpClient, TimeSpan? readTimeout = null) {
  private static readonly TimeSpan defaultReadTimeout = TimeSpan.FromMinutes(2);

  internal async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(ProviderState state,
      IEnumerable<ChatMessage> messages, ChatOptions options, [EnumeratorCancellation] CancellationToken cancellationToken) {
    await using var exchange = ExchangeAsync(state, messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
    while (true) {
      try {
        if (!await exchange.MoveNextAsync().ConfigureAwait(false)) break;
      } catch (Exception error) when (error is HttpRequestException or OperationCanceledException or
          IOException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or
          FormatException or NotSupportedException) {
        cancellationToken.ThrowIfCancellationRequested();
        throw ProviderOperationException.FromProviderError(error, credentials: state.Credentials);
      }
      yield return exchange.Current;
    }
  }

  private async IAsyncEnumerable<ChatResponseUpdate> ExchangeAsync(ProviderState state, IEnumerable<ChatMessage> messages,
      ChatOptions options, [EnumeratorCancellation] CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    var scope = new LlmProviderScope(state.Connection.Id, state.Generation, state.Connection.Api, options.ModelId ?? "");
    IReadOnlyList<ChatMessage> original = messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
    IReadOnlyList<ChatMessage> history = LlmMessageProjection.Prepare(original, scope);
    RequestContextEstimate? estimate = ReadOption<RequestContextEstimate>(options, RequestContextEstimate.PropertyName);
    ToolDraftBuffer? drafts = ReadOption<ToolDraftBuffer>(options, ToolDraftBuffer.PropertyName);
    var guard = new AnthropicStreamState(scope, options, state.Credentials, drafts, estimate is not null);
    using var readStop = new ProviderCancellation(Timeout.InfiniteTimeSpan, cancellationToken);
    TimeSpan timeout = readTimeout ?? defaultReadTimeout;
    try {
      using var request = AnthropicRequest.Create(state, original, history, options, cancellationToken);
      estimate?.Prepare(history, options);
      using var response = await ReadAsync<HttpResponseMessage>(
          ct => new(httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)),
          "provider response headers").ConfigureAwait(false);
      CheckRead("provider response headers");
      if (!response.IsSuccessStatusCode) {
        var failure = await ReadAsync(ct => AnthropicErrors.FromHttpAsync(response, state.Credentials, ct),
            "the provider error body").ConfigureAwait(false);
        CheckRead("the provider error body");
        throw failure;
      }
      if (response.StatusCode != HttpStatusCode.OK || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        throw Invalid("Anthropic response is not an event stream");

      // HttpResponseMessage owns and disposes this stream. The bounded view does not own it.
      Stream body = await ReadAsync<Stream>(ct => new(response.Content.ReadAsStreamAsync(ct)), "the provider stream").ConfigureAwait(false);
      CheckRead("the provider stream");
      using var input = new AnthropicSseInput(body);
      var parser = SseParser.Create(input, static (_, data) => {
        if (data.Length > AnthropicSseInput.MaximumEventBytes) throw Invalid("Anthropic SSE event exceeds the size limit");
        return JsonSerializer.Deserialize<JsonElement>(data, Strict);
      });
      await using var stream = parser.EnumerateAsync(readStop.Token).GetAsyncEnumerator(readStop.Token);
      string stage = "the first provider stream event";
      while (true) {
        input.BeginEvent();
        bool next = await ReadAsync(_ => stream.MoveNextAsync(), stage).ConfigureAwait(false);
        CheckRead(stage);
        if (!next) break;
        stage = "the next provider stream event";
        if (guard.Accept(stream.Current) is { } update) yield return update;
      }
    } finally {
      await readStop.CancelAsync().ConfigureAwait(false);
    }
    cancellationToken.ThrowIfCancellationRequested();
    // A complete event sequence is necessary, but never sufficient before the transport closes cleanly.
    yield return guard.Complete();

    async ValueTask<T> ReadAsync<T>(Func<CancellationToken, ValueTask<T>> read, string stage) {
      readStop.SetDeadline(timeout);
      try {
        return await read(readStop.Token).ConfigureAwait(false);
      } catch (Exception error) when (readStop.IsCancellationRequested &&
          error is OperationCanceledException or IOException or HttpRequestException) {
        CheckRead(stage);
        throw;
      } finally {
        readStop.SetDeadline(Timeout.InfiniteTimeSpan);
      }
    }

    void CheckRead(string stage) {
      cancellationToken.ThrowIfCancellationRequested();
      if (readStop.IsCancellationRequested)
        throw new ProviderOperationException(ProviderFailure.ProviderUnavailable, $"Timed out waiting for {stage}.");
    }
  }

  private static T? ReadOption<T>(ChatOptions options, string key) where T : class =>
      options.AdditionalProperties?.TryGetValue(key, out object? value) == true ? value as T : null;
}
