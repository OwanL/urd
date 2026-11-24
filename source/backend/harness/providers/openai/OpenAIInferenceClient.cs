using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using Urd.Core.Execution;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable OPENAI001, MEAI001 // Pinned official native streaming adapters.

namespace Urd.Core.Providers;

// One streaming exchange. The manager owns credentials, cancellation and the borrowed transport.
internal sealed class OpenAIInferenceClient(HttpClient httpClient, TimeSpan? readTimeout = null,
    Func<string, Guid, CancellationToken, Task<WebSocket>>? connectSocket = null,
    Action<ProviderDiagnostic>? diagnose = null) {
  internal const string SessionIdProperty = "urd.conversation.session-id";
  private static readonly TimeSpan defaultReadTimeout = TimeSpan.FromMinutes(2);
  private static readonly Uri openAIEndpoint = new("https://api.openai.com/v1/");

  internal async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(ProviderState state,
      IEnumerable<ChatMessage> messages, ChatOptions options,
      [EnumeratorCancellation] CancellationToken cancellationToken) {
    using var readStop = new ProviderCancellation(Timeout.InfiniteTimeSpan, cancellationToken);
    Guid? sessionId = TakeSessionId(ref options);
    ChatGptSocketRun? socketRun = TakeSocketRun(ref options);
    var progress = new InferenceStreamProgress(readStop, readTimeout ?? defaultReadTimeout,
        state.Connection.Route == ProviderRoute.ChatGpt ? sessionId : null) { Transport = state.Connection.Endpoint.Scheme };
    await using var stream = ReadExchangeAsync(state, messages, options, readStop, progress, sessionId, socketRun,
        cancellationToken).GetAsyncEnumerator(cancellationToken);
    while (true) {
      try {
        if (!await stream.MoveNextAsync().ConfigureAwait(false))
          break;
      } catch (ProviderOperationException failure) when (!cancellationToken.IsCancellationRequested) {
        Report(state.Connection, sessionId, progress.Transport, progress.Stage, failure.Failure, failure.DiagnosticCause, progress);
        throw;
      }
      yield return stream.Current;
    }
  }

  private void Report(ProviderConnection connection, Guid? sessionId, string transport, string stage,
      ProviderFailure failure, ProviderDiagnostic.Cause? cause, InferenceStreamProgress? progress = null) =>
      diagnose?.Invoke(new(connection.Route, connection.Id, sessionId, transport, stage, failure,
          cause?.HttpStatus, cause?.ExceptionType, cause?.HttpRequestError, cause?.SocketError,
          cause?.WebSocketError, cause?.WebSocketReceiveFailure) {
        WireTerminal = progress?.WireTerminal,
        SdkTerminal = progress?.SdkTerminal
      });

  private async IAsyncEnumerable<ChatResponseUpdate> ReadExchangeAsync(ProviderState state,
      IEnumerable<ChatMessage> messages, ChatOptions options, ProviderCancellation readStop,
      InferenceStreamProgress progress, Guid? sessionId, ChatGptSocketRun? socketRun,
      [EnumeratorCancellation] CancellationToken cancellationToken) {
    HttpClient? turnClient = null;
    bool accepted = false;
    try {
      IChatClient client;
      IReadOnlyList<ChatMessage> history;
      ChatOptions wireOptions;
      LlmProviderScope scope;
      RequestContextEstimate? estimate;
      try {
        cancellationToken.ThrowIfCancellationRequested();
        estimate = TakeContextEstimate(ref options);
        wireOptions = ProviderModelOptions.Prepare(state.Connection, options);
        scope = new(state.Connection.Id, state.Generation, state.Connection.Api, options.ModelId!);
        IReadOnlyList<ChatMessage>? input = estimate is null ? null : messages as IReadOnlyList<ChatMessage> ?? messages.ToArray();
        history = LlmMessageProjection.Prepare(input ?? messages, scope);
        // ChatGPT's Responses endpoint rejects system messages, including saved starting context.
        if (state.Connection.Route == ProviderRoute.ChatGpt)
          history = history.Select(ForChatGpt).ToArray();
        if (connectSocket is not null && socketRun is not null && sessionId is Guid id && state.Connection.Route == ProviderRoute.ChatGpt
            && state.Connection.Api == LlmApi.OpenAIResponses && state.Connection.Authentication == AuthenticationMethod.OAuth
            && state.Connection.Endpoint == openAIEndpoint) {
          turnClient = await socketRun.OpenTurnAsync(state, id, connectSocket, cancellationToken,
              diagnose is null ? null : error => Report(state.Connection, sessionId, "websocket", "handshake-fallback",
                  ProviderFailure.ProviderUnavailable, ProviderDiagnostic.Describe(error, null)),
              diagnose is null ? null : terminal => progress.WireTerminal = terminal).ConfigureAwait(false);
          if (turnClient is not null)
            progress.Transport = "websocket";
        }
        client = CreateClient(state, options.ModelId!, progress, turnClient ?? httpClient);
        if (estimate is not null && input is not null)
          estimate.Prepare(ContextEstimateInput(history, input, scope), options);
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
          throw ProviderOperationException.FromProviderError(error, contextWindow: true, credentials: state.Credentials,
              socketTransport: progress.Transport == "websocket");
        }
        try {
          while (await MoveNextAsync(stream, progress, state.Credentials, cancellationToken).ConfigureAwait(false)) {
            ChatResponseUpdate nativeUpdate = stream.Current;
            if (diagnose is not null) {
              ProviderStreamTerminalKind? terminal = nativeUpdate.RawRepresentation switch {
                StreamingResponseCompletedUpdate => ProviderStreamTerminalKind.Completed,
                StreamingResponseIncompleteUpdate => ProviderStreamTerminalKind.Incomplete,
                StreamingResponseFailedUpdate => ProviderStreamTerminalKind.ResponseFailed,
                StreamingResponseErrorUpdate => ProviderStreamTerminalKind.StandaloneError,
                _ => null
              };
              if (terminal is not null) progress.SdkTerminal = terminal;
            }
            if (nativeUpdate.RawRepresentation is not null)
              progress.RecordUpdate();
            UsageDetails? usage = ProviderTokenUsage.Read(nativeUpdate.RawRepresentation);
            if (usage is not null) {
              nativeUpdate.Contents = nativeUpdate.Contents.Where(content => content is not UsageContent).ToList();
              yield return UsageUpdate(nativeUpdate, usage);
            }

            BinaryData? toolArgumentDelta = estimate is not null && nativeUpdate.RawRepresentation is StreamingResponseFunctionCallArgumentsDeltaUpdate arguments
                ? arguments.Delta : null;
            ChatResponseUpdate? update;
            try {
              update = guard.Accept(nativeUpdate);
            } catch (Exception error) when (CanClassify(error, cancellationToken)) {
              throw ProviderOperationException.FromProviderError(error, contextWindow: true, credentials: state.Credentials);
            }
            if (guard.ChatHitOutputLimit) {
              if (usage is null && await ChatUsageTrailer.ReadAsync(stream, static update => update.RawRepresentation,
                  readStop, cancellationToken, readTimeout).ConfigureAwait(false) is { } trailer)
                yield return UsageUpdate(stream.Current, trailer);
              break;
            }
            if (toolArgumentDelta is not null) {
              update ??= new(); // No visible output or tool execution yet.
              (update.AdditionalProperties ??= [])[RequestContextEstimate.ToolArgumentDeltaProperty] = toolArgumentDelta;
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
        accepted = true;
        yield return final;
      }
    } finally {
      try {
        turnClient?.Dispose();
      } finally {
        if (turnClient is not null)
          socketRun!.EndTurn(accepted);
      }
    }
  }

  private static ChatResponseUpdate UsageUpdate(ChatResponseUpdate update, UsageDetails usage) =>
      new(update.Role, [new UsageContent(usage)]) {
        ResponseId = update.ResponseId,
        MessageId = update.MessageId,
        ModelId = update.ModelId,
        CreatedAt = update.CreatedAt
      };

  private static Guid? TakeSessionId(ref ChatOptions options) {
    if (options.AdditionalProperties?.TryGetValue(SessionIdProperty, out object? value) != true)
      return null;

    options = options.Clone();
    options.AdditionalProperties = new(options.AdditionalProperties!);
    options.AdditionalProperties.Remove(SessionIdProperty);
    return value is Guid id ? id : null;
  }

  private static ChatGptSocketRun? TakeSocketRun(ref ChatOptions options) {
    if (options.AdditionalProperties?.TryGetValue(ChatGptSocketRun.PropertyName, out object? value) != true)
      return null;

    options = options.Clone();
    options.AdditionalProperties = new(options.AdditionalProperties!);
    options.AdditionalProperties.Remove(ChatGptSocketRun.PropertyName);
    return value as ChatGptSocketRun;
  }

  private static RequestContextEstimate? TakeContextEstimate(ref ChatOptions options) {
    if (options.AdditionalProperties?.TryGetValue(RequestContextEstimate.PropertyName, out object? value) != true)
      return null;

    options = options.Clone();
    options.AdditionalProperties = new(options.AdditionalProperties!);
    options.AdditionalProperties.Remove(RequestContextEstimate.PropertyName);
    return value as RequestContextEstimate;
  }

  // Native Responses replay holds SDK objects. For those items, count the already decoded visible
  // content, not the opaque wire envelope or encrypted reasoning. Projection still owns the send.
  private static IEnumerable<ChatMessage> ContextEstimateInput(IReadOnlyList<ChatMessage> prepared,
      IReadOnlyList<ChatMessage> original, LlmProviderScope scope) => scope.Api == LlmApi.OpenAIResponses
      ? prepared.Select((message, index) => original[index].RawRepresentation is LlmProviderResponse response
          && response.Scope == scope ? original[index] : message)
      : prepared;

  private static ChatMessage ForChatGpt(ChatMessage message) {
    if (message.Role != ChatRole.System)
      return message;

    ChatMessage instruction = message.Clone();
    instruction.Role = new ChatRole("developer");
    return instruction;
  }

  private static IChatClient CreateClient(ProviderState state, string model, InferenceStreamProgress progress,
      HttpClient httpClient) {
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
      throw ProviderOperationException.FromProviderError(error, contextWindow: true, credentials: credentials,
          socketTransport: progress.Transport == "websocket");
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
      throw ProviderOperationException.FromProviderError(error, contextWindow: true, credentials: credentials,
          socketTransport: progress.Transport == "websocket");
    }
  }

  private static bool CanClassify(Exception error, CancellationToken ct) => error is not ProviderOperationException &&
      !(error is OperationCanceledException && ct.IsCancellationRequested) &&
      error is ClientResultException or HttpRequestException or IOException or OperationCanceledException or
          SocketException or WebSocketException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or NotSupportedException;
}

#pragma warning restore OPENAI001, MEAI001
