using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using OpenAI.Responses;
using Urd.Core.Storage;
using Urd.Core.Usage;
using UsageDetails = Microsoft.Extensions.AI.UsageDetails;

#pragma warning disable OPENAI001, MEAI001 // The pinned SDK's native Responses, authentication and usage APIs.

namespace Urd.Core.Providers;

// One-request diagnostics through the official SDK. The caller owns the HTTP client and deadline.
internal sealed class OpenAIModelProbe(HttpClient httpClient, UsageStorage? usageStorage = null, ModelPriceCatalog? prices = null) {
  private const string TestPrompt = "Reply with one short greeting.";
  private const int PreviewLimit = 4096;

  internal async Task<IReadOnlyList<ProviderModel>> GetModelsAsync(ProviderConnection connection,
      ProviderCredentials? credentials, CancellationToken ct) {
    try {
      ct.ThrowIfCancellationRequested();
      var client = new OpenAIModelClient(ProviderSdkAuthentication.Create(connection, credentials), Options(connection));
      if (connection.Route == ProviderRoute.ChatGpt) {
        var result = await client.GetModelsAsync(new RequestOptions { CancellationToken = ct }).ConfigureAwait(false);
        using var response = result.GetRawResponse();
        using var document = JsonDocument.Parse(response.Content);
        return ReadPlanModels(document.RootElement, connection);
      }

      var models = await client.GetModelsAsync(ct).ConfigureAwait(false);
      using var raw = models.GetRawResponse();
      using var envelope = JsonDocument.Parse(raw.Content);
      Dictionary<string, int> reportedContextWindows = ReadReportedContextWindows(RequireArray(envelope.RootElement, "data"));
      return models.Value.Select(model => {
        string id = Required(model?.Id);
        int? reported = reportedContextWindows.TryGetValue(id, out int contextWindow) ? contextWindow : null;
        return new ProviderModel(id, id, ModelContextWindows.Resolve(connection, id, reported));
      }).ToArray();
    } catch (Exception error) when (CanClassify(error, ct)) {
      ct.ThrowIfCancellationRequested();
      throw ProviderOperationException.FromProviderError(error, discovery: true, credentials: credentials);
    }
  }

  internal async Task<ProviderModelTestResult> TestModelAsync(ProviderConnection connection,
      ProviderCredentials? credentials, string modelId, CancellationToken ct) {
    ct.ThrowIfCancellationRequested();
    UsageObservation? observation = usageStorage is null ? null : new(usageStorage,
        connection, modelId, new(null, null, "provider test"),
        [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, TestPrompt)],
        new Microsoft.Extensions.AI.ChatOptions { ModelId = modelId }, prices);
    using var readStop = new ProviderCancellation(Timeout.InfiniteTimeSpan, ct);
    var progress = new InferenceStreamProgress(readStop, Timeout.InfiniteTimeSpan) { Transport = connection.Endpoint.Scheme };
    try {
      ct.ThrowIfCancellationRequested();
      return connection.Api switch {
        LlmApi.OpenAIResponses => await TestResponsesAsync(connection, credentials, modelId, observation, progress, ct).ConfigureAwait(false),
        LlmApi.OpenAIChatCompletions => await TestChatAsync(connection, credentials, modelId, observation, progress, readStop, ct).ConfigureAwait(false),
        _ => throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse)
      };
    } catch (Exception error) when (CanClassify(error, ct)) {
      ct.ThrowIfCancellationRequested();
      throw ProviderOperationException.FromProviderError(error, credentials: credentials,
          preStreamHttpStatus: progress.PreStreamHttpStatus);
    } finally {
      observation?.Finish();
    }
  }

  private async Task<ProviderModelTestResult> TestResponsesAsync(ProviderConnection connection,
      ProviderCredentials? credentials, string modelId, UsageObservation? observation, InferenceStreamProgress progress,
      CancellationToken ct) {
    var clientOptions = new ResponsesClientOptions {
      Endpoint = connection.Endpoint,
      Transport = new HttpClientPipelineTransport(httpClient),
      RetryPolicy = new ClientRetryPolicy(0)
    };
    clientOptions.AddPolicy(progress, PipelinePosition.BeforeTransport);
    var client = new ResponsesClient(ProviderSdkAuthentication.Create(connection, credentials), clientOptions);
    var options = new CreateResponseOptions {
      Model = modelId,
      StreamingEnabled = true,
      StoredOutputEnabled = false,
      InputItems = { ResponseItem.CreateUserMessageItem(TestPrompt) }
    };
    var text = new StringBuilder();
    bool truncated = false;
    int completed = 0;
    ProviderUsage? usage = null;
    await foreach (var update in client.CreateResponseStreamingAsync(options, ct).ConfigureAwait(false)) {
      progress.RecordUpdate();
      ObserveUsage(observation, update, (update as StreamingResponseOutputTextDeltaUpdate)?.Delta,
          (update as StreamingResponseCompletedUpdate)?.Response?.Model);
      switch (update) {
        case StreamingResponseOutputTextDeltaUpdate delta:
          AppendPreview(text, delta.Delta, ref truncated);
          break;
        case StreamingResponseCompletedUpdate terminal:
          if (++completed != 1 || terminal.Response?.Status != ResponseStatus.Completed ||
              terminal.Response.Error is not null || terminal.Response.IncompleteStatusDetails is not null)
            throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);
          usage = ProviderTokenUsage.ToProviderUsage(ProviderTokenUsage.Response(terminal.Response.Usage));
          break;
        case StreamingResponseIncompleteUpdate:
          throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
        case StreamingResponseFailedUpdate failed:
          throw ProviderOperationException.FromProviderStreamError(failed.Response?.Error?.Code.ToString(),
              failed.Response?.Error?.Message, failed.Response?.Error?.Param, credentials);
        case StreamingResponseErrorUpdate error:
          throw ProviderOperationException.FromProviderStreamError(error.Code, error.Message, error.Param, credentials);
      }
    }
    // Iteration disposes the SDK stream before success; partial text and ordinary EOF are not completion.
    ct.ThrowIfCancellationRequested();
    if (completed != 1) throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
    return new(connection.Id, modelId, text.ToString(), truncated, usage);
  }

  private async Task<ProviderModelTestResult> TestChatAsync(ProviderConnection connection,
      ProviderCredentials? credentials, string modelId, UsageObservation? observation, InferenceStreamProgress progress,
      ProviderCancellation readStop, CancellationToken ct) {
    var clientOptions = Options(connection);
    clientOptions.AddPolicy(progress, PipelinePosition.BeforeTransport);
    var client = new ChatClient(modelId, ProviderSdkAuthentication.Create(connection, credentials), clientOptions);
    var options = new ChatCompletionOptions { StoredOutputEnabled = false };
    ProviderModelTestResult result;
    try {
      await using var stream = client.CompleteChatStreamingAsync([new UserChatMessage(TestPrompt)], options, readStop.Token)
          .GetAsyncEnumerator(readStop.Token);
      result = await ReadChatAsync(stream, readStop, connection.Id, modelId, observation, progress, ct).ConfigureAwait(false);
    } finally {
      await readStop.CancelAsync().ConfigureAwait(false);
    }
    ct.ThrowIfCancellationRequested();
    return result;
  }

  private static async Task<ProviderModelTestResult> ReadChatAsync(IAsyncEnumerator<StreamingChatCompletionUpdate> stream,
      ProviderCancellation readStop, Guid connectionId, string modelId, UsageObservation? observation,
      InferenceStreamProgress progress, CancellationToken ct) {
    var text = new StringBuilder();
    bool truncated = false;
    int completed = 0;
    ProviderUsage? usage = null;
    while (await stream.MoveNextAsync().ConfigureAwait(false)) {
      StreamingChatCompletionUpdate update = stream.Current;
      progress.RecordUpdate();
      ObserveUsage(observation, update, string.Concat(update.ContentUpdate.Select(part => part.Text)), update.Model);
      foreach (var part in update.ContentUpdate) AppendPreview(text, part.Text, ref truncated);
      if (update.FinishReason is { } finish) {
        if (finish == ChatFinishReason.Length && observation is not null && update.Usage is null &&
            await ChatUsageTrailer.ReadAsync(stream, static update => update, readStop, ct).ConfigureAwait(false) is { } trailer)
          ObserveUsage(observation, stream.Current, null, stream.Current.Model, trailer);
        if (finish != ChatFinishReason.Stop) {
          ct.ThrowIfCancellationRequested();
          throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
        }
        if (++completed != 1) throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);
      }
      if (update.Usage is not null)
        usage = ProviderTokenUsage.ToProviderUsage(ProviderTokenUsage.Chat(update.Usage));
    }
    ct.ThrowIfCancellationRequested();
    if (completed != 1) throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
    return new(connectionId, modelId, text.ToString(), truncated, usage);
  }

  private static void ObserveUsage(UsageObservation? observation, object update, string? text, string? model,
      UsageDetails? trailer = null) {
    if (observation is null) return;
    model ??= update switch {
      StreamingResponseIncompleteUpdate incomplete => incomplete.Response?.Model,
      StreamingResponseFailedUpdate failed => failed.Response?.Model,
      _ => null
    };
    var observed = new Microsoft.Extensions.AI.ChatResponseUpdate { ModelId = model };
    if (update is StreamingResponseCompletedUpdate or StreamingChatCompletionUpdate { FinishReason: ChatFinishReason.Stop })
      observed.FinishReason = Microsoft.Extensions.AI.ChatFinishReason.Stop;
    if (!string.IsNullOrEmpty(text)) observed.Contents.Add(new Microsoft.Extensions.AI.TextContent(text));
    if ((trailer ?? ProviderTokenUsage.Read(update)) is { } usage)
      observed.Contents.Add(new Microsoft.Extensions.AI.UsageContent(usage));
    observation.Append(observed);
  }

  private OpenAIClientOptions Options(ProviderConnection connection) => new() {
    Endpoint = connection.Endpoint,
    Transport = new HttpClientPipelineTransport(httpClient),
    RetryPolicy = new ClientRetryPolicy(0)
  };

  private static ProviderModel[] ReadPlanModels(JsonElement envelope, ProviderConnection connection) =>
      RequireArray(envelope, "models").EnumerateArray()
      .Where(model => model.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "list")
      .Select(model => {
        string id = Required(model.GetProperty("slug").GetString());
        string? name = model.TryGetProperty("display_name", out var display) ? display.GetString() : null;
        return new ProviderModel(id, string.IsNullOrWhiteSpace(name) ? id : name,
            ModelContextWindows.Resolve(connection, id, model));
      }).ToArray();

  private static Dictionary<string, int> ReadReportedContextWindows(JsonElement models) {
    var contextWindows = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (JsonElement model in models.EnumerateArray()) {
      if (!model.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String ||
          string.IsNullOrWhiteSpace(id.GetString()) || ModelContextWindows.ReadReported(model) is not { } contextWindow)
        continue;

      contextWindows[id.GetString()!] = contextWindow;
    }
    return contextWindows;
  }

  private static JsonElement RequireArray(JsonElement envelope, string name) {
    if (!envelope.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
      throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);
    return array;
  }

  private static string Required(string? value) => !string.IsNullOrWhiteSpace(value) ? value :
      throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);

  private static void AppendPreview(StringBuilder text, string? delta, ref bool truncated) {
    if (string.IsNullOrEmpty(delta)) return;
    int take = Math.Min(delta.Length, PreviewLimit - text.Length);
    text.Append(delta, 0, take);
    truncated |= take != delta.Length;
  }

  private static bool CanClassify(Exception error, CancellationToken ct) => error is not ProviderOperationException &&
      !(error is OperationCanceledException && ct.IsCancellationRequested) &&
      error is ClientResultException or HttpRequestException or IOException or OperationCanceledException or
          JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException;
}

#pragma warning restore OPENAI001, MEAI001
