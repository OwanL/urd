using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable OPENAI001, MEAI001 // Pinned official native streaming adapters.

namespace Urd.Core.Providers;

// One streaming exchange. The manager owns credentials, cancellation and the borrowed transport.
internal sealed class ProviderModelClient(HttpClient httpClient) {
  internal async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(ProviderState state,
      IEnumerable<ChatMessage> messages, ChatOptions options,
      [EnumeratorCancellation] CancellationToken cancellationToken) {
    IChatClient client;
    IReadOnlyList<ChatMessage> history;
    ChatOptions wireOptions;
    LlmProviderScope scope;
    try {
      cancellationToken.ThrowIfCancellationRequested();
      wireOptions = ProviderModelOptions.Prepare(state.Connection, options);
      scope = new(state.Connection.Id, state.Generation, state.Connection.Api, options.ModelId!);
      history = LlmProviderContext.Prepare(messages, scope);
      client = CreateClient(state, options.ModelId!);
    } catch (Exception error) when (CanClassify(error, cancellationToken)) {
      throw ProviderOperationException.FromProviderError(error);
    }

    using (client) {
      var guard = new ProviderStreamState(scope, options.Tools, state.Connection.Route == ProviderRoute.ChatGpt);
      IAsyncEnumerator<ChatResponseUpdate> stream;
      try {
        stream = client.GetStreamingResponseAsync(history, wireOptions, cancellationToken).GetAsyncEnumerator(cancellationToken);
      } catch (Exception error) when (CanClassify(error, cancellationToken)) {
        cancellationToken.ThrowIfCancellationRequested();
        throw ProviderOperationException.FromProviderError(error);
      }
      try {
        while (await MoveNextAsync(stream, cancellationToken).ConfigureAwait(false)) {
          ChatResponseUpdate? update;
          try {
            update = guard.Accept(stream.Current);
          } catch (Exception error) when (CanClassify(error, cancellationToken)) {
            throw ProviderOperationException.FromProviderError(error);
          }
          if (update is not null) yield return update;
        }
      } finally {
        await DisposeAsync(stream, cancellationToken).ConfigureAwait(false);
      }
      cancellationToken.ThrowIfCancellationRequested();
      ChatResponseUpdate final;
      try {
        final = guard.Complete();
      } catch (Exception error) when (CanClassify(error, cancellationToken)) {
        throw ProviderOperationException.FromProviderError(error);
      }
      // Calls and the positive finish are published only after native iteration AND cleanup.
      yield return final;
    }
  }

  private IChatClient CreateClient(ProviderState state, string model) {
    var connection = state.Connection;
    var authentication = ProviderSdkAuthentication.Create(connection, state.Credentials);
    return connection.Api switch {
      LlmApi.OpenAIResponses => new ResponsesClient(authentication, new ResponsesClientOptions {
        Endpoint = connection.Endpoint,
        Transport = new HttpClientPipelineTransport(httpClient),
        RetryPolicy = new ClientRetryPolicy(0),
        NetworkTimeout = Timeout.InfiniteTimeSpan
      }).AsIChatClient(),
      LlmApi.OpenAIChatCompletions => new ChatClient(model, authentication, new OpenAIClientOptions {
        Endpoint = connection.Endpoint,
        Transport = new HttpClientPipelineTransport(httpClient),
        RetryPolicy = new ClientRetryPolicy(0),
        NetworkTimeout = Timeout.InfiniteTimeSpan
      }).AsIChatClient(),
      _ => throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse)
    };
  }

  private static async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<ChatResponseUpdate> stream, CancellationToken ct) {
    try {
      return await stream.MoveNextAsync().ConfigureAwait(false);
    } catch (Exception error) when (CanClassify(error, ct)) {
      ct.ThrowIfCancellationRequested();
      throw ProviderOperationException.FromProviderError(error);
    }
  }

  private static async ValueTask DisposeAsync(IAsyncEnumerator<ChatResponseUpdate> stream, CancellationToken ct) {
    try {
      await stream.DisposeAsync().ConfigureAwait(false);
    } catch (Exception error) when (CanClassify(error, ct)) {
      ct.ThrowIfCancellationRequested();
      throw ProviderOperationException.FromProviderError(error);
    }
  }

  private static bool CanClassify(Exception error, CancellationToken ct) => error is not ProviderOperationException &&
      !(error is OperationCanceledException && ct.IsCancellationRequested) &&
      error is ClientResultException or HttpRequestException or IOException or OperationCanceledException or
          JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or NotSupportedException;
}

#pragma warning restore OPENAI001, MEAI001
