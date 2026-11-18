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
internal sealed class OpenAIInferenceClient(HttpClient httpClient, TimeSpan? readTimeout = null) {
  private static readonly TimeSpan defaultReadTimeout = TimeSpan.FromMinutes(2);

  internal async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(ProviderState state,
      IEnumerable<ChatMessage> messages, ChatOptions options,
      [EnumeratorCancellation] CancellationToken cancellationToken) {
    using var readStop = new ProviderCancellation(Timeout.InfiniteTimeSpan, cancellationToken);
    var progress = new InferenceStreamProgress(readStop, readTimeout ?? defaultReadTimeout);
    IChatClient client;
    IReadOnlyList<ChatMessage> history;
    ChatOptions wireOptions;
    LlmProviderScope scope;
    try {
      cancellationToken.ThrowIfCancellationRequested();
      wireOptions = ProviderModelOptions.Prepare(state.Connection, options);
      scope = new(state.Connection.Id, state.Generation, state.Connection.Api, options.ModelId!);
      history = LlmMessageProjection.Prepare(messages, scope);
      // ChatGPT's Responses endpoint rejects system messages, including saved starting context.
      if (state.Connection.Route == ProviderRoute.ChatGpt)
        history = history.Select(ForChatGpt).ToArray();
      client = CreateClient(state, options.ModelId!, progress);
    } catch (Exception error) when (CanClassify(error, cancellationToken)) {
      throw ProviderOperationException.FromProviderError(error, contextWindow: true, credentials: state.Credentials);
    }

    using (client) {
      var guard = new ProviderStreamState(scope, options.Tools, state.Connection.Route == ProviderRoute.ChatGpt,
          state.Credentials);
      IAsyncEnumerator<ChatResponseUpdate> stream;
      try {
        stream = client.GetStreamingResponseAsync(history, wireOptions, progress.Token).GetAsyncEnumerator(progress.Token);
      } catch (Exception error) when (CanClassify(error, cancellationToken)) {
        cancellationToken.ThrowIfCancellationRequested();
        throw ProviderOperationException.FromProviderError(error, contextWindow: true, credentials: state.Credentials);
      }
      try {
        while (await MoveNextAsync(stream, progress, state.Credentials, cancellationToken).ConfigureAwait(false)) {
          ChatResponseUpdate nativeUpdate = stream.Current;
          if (nativeUpdate.RawRepresentation is not null)
            progress.RecordUpdate();
          UsageDetails? usage = ProviderTokenUsage.Read(nativeUpdate.RawRepresentation);
          if (usage is not null) {
            nativeUpdate.Contents = nativeUpdate.Contents.Where(content => content is not UsageContent).ToList();
            yield return new ChatResponseUpdate(nativeUpdate.Role, [new UsageContent(usage)]) {
              ResponseId = nativeUpdate.ResponseId,
              MessageId = nativeUpdate.MessageId,
              ModelId = nativeUpdate.ModelId,
              CreatedAt = nativeUpdate.CreatedAt
            };
          }

          ChatResponseUpdate? update;
          try {
            update = guard.Accept(nativeUpdate);
          } catch (Exception error) when (CanClassify(error, cancellationToken)) {
            throw ProviderOperationException.FromProviderError(error, contextWindow: true, credentials: state.Credentials);
          }
          if (update is not null) yield return update;
        }
      } finally {
        try {
          await DisposeAsync(stream, progress, state.Credentials, cancellationToken).ConfigureAwait(false);
        } finally {
          // A timeout starts its own cancellation callbacks; join them before releasing the stream.
          await readStop.CancelAsync().ConfigureAwait(false);
        }
      }
      cancellationToken.ThrowIfCancellationRequested();
      ChatResponseUpdate final;
      try {
        final = guard.Complete();
      } catch (Exception error) when (CanClassify(error, cancellationToken)) {
        throw ProviderOperationException.FromProviderError(error, contextWindow: true, credentials: state.Credentials);
      }
      // Calls and the positive finish are published only after native iteration AND cleanup.
      yield return final;
    }
  }

  private static ChatMessage ForChatGpt(ChatMessage message) {
    if (message.Role != ChatRole.System)
      return message;

    ChatMessage instruction = message.Clone();
    instruction.Role = new ChatRole("developer");
    return instruction;
  }

  private IChatClient CreateClient(ProviderState state, string model, InferenceStreamProgress progress) {
    var connection = state.Connection;
    var authentication = ProviderSdkAuthentication.Create(connection, state.Credentials);
    return connection.Api switch {
      LlmApi.OpenAIResponses => new ResponsesClient(authentication,
          Configure(new ResponsesClientOptions { Endpoint = connection.Endpoint })).AsIChatClient(),
      LlmApi.OpenAIChatCompletions => new ChatClient(model, authentication,
          Configure(new OpenAIClientOptions { Endpoint = connection.Endpoint })).AsIChatClient(),
      _ => throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse)
    };

    T Configure<T>(T options) where T : ClientPipelineOptions {
      options.Transport = new HttpClientPipelineTransport(httpClient);
      options.RetryPolicy = new ClientRetryPolicy(0);
      options.NetworkTimeout = Timeout.InfiniteTimeSpan;
      options.AddPolicy(progress, PipelinePosition.BeforeTransport);
      return options;
    }
  }

  private static async ValueTask<bool> MoveNextAsync(IAsyncEnumerator<ChatResponseUpdate> stream,
      InferenceStreamProgress progress, ProviderCredentials? credentials, CancellationToken ct) {
    progress.BeginRead();
    try {
      bool next;
      try {
        next = await stream.MoveNextAsync().ConfigureAwait(false);
      } finally {
        // Consumer work (durable output writes, for example) is not provider silence.
        progress.EndRead();
      }
      progress.Token.ThrowIfCancellationRequested();
      return next;
    } catch (Exception error) when (CanClassify(error, ct)) {
      ct.ThrowIfCancellationRequested();
      if (progress.Token.IsCancellationRequested)
        throw progress.TimeoutFailure();
      throw ProviderOperationException.FromProviderError(error, contextWindow: true, credentials: credentials);
    }
  }

  private static async ValueTask DisposeAsync(IAsyncEnumerator<ChatResponseUpdate> stream,
      InferenceStreamProgress progress, ProviderCredentials? credentials, CancellationToken ct) {
    try {
      await stream.DisposeAsync().ConfigureAwait(false);
    } catch (Exception error) when (CanClassify(error, ct)) {
      ct.ThrowIfCancellationRequested();
      if (progress.Token.IsCancellationRequested)
        throw progress.TimeoutFailure(cleanupFailed: true);
      throw ProviderOperationException.FromProviderError(error, contextWindow: true, credentials: credentials);
    }
  }

  private static bool CanClassify(Exception error, CancellationToken ct) => error is not ProviderOperationException &&
      !(error is OperationCanceledException && ct.IsCancellationRequested) &&
      error is ClientResultException or HttpRequestException or IOException or OperationCanceledException or
          JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or NotSupportedException;
}

#pragma warning restore OPENAI001, MEAI001
