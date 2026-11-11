using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using OpenAI.Responses;

#pragma warning disable OPENAI001 // The pinned SDK's native Responses and authentication-policy APIs.

namespace Urd.Core.Providers;

// One-request diagnostics through the official SDK. The caller owns the HTTP client and deadline.
internal sealed class OpenAIProviderClient(HttpClient httpClient) {
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
    try {
      ct.ThrowIfCancellationRequested();
      return connection.Api switch {
        LlmApi.OpenAIResponses => await TestResponsesAsync(connection, credentials, modelId, ct).ConfigureAwait(false),
        LlmApi.OpenAIChatCompletions => await TestChatAsync(connection, credentials, modelId, ct).ConfigureAwait(false),
        _ => throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse)
      };
    } catch (Exception error) when (CanClassify(error, ct)) {
      ct.ThrowIfCancellationRequested();
      throw ProviderOperationException.FromProviderError(error, credentials: credentials);
    }
  }

  private async Task<ProviderModelTestResult> TestResponsesAsync(ProviderConnection connection,
      ProviderCredentials? credentials, string modelId, CancellationToken ct) {
    var client = new ResponsesClient(ProviderSdkAuthentication.Create(connection, credentials), new ResponsesClientOptions {
      Endpoint = connection.Endpoint,
      Transport = new HttpClientPipelineTransport(httpClient),
      RetryPolicy = new ClientRetryPolicy(0)
    });
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
      switch (update) {
        case StreamingResponseOutputTextDeltaUpdate delta:
          AppendPreview(text, delta.Delta, ref truncated);
          break;
        case StreamingResponseCompletedUpdate terminal:
          if (++completed != 1 || terminal.Response?.Status != ResponseStatus.Completed ||
              terminal.Response.Error is not null || terminal.Response.IncompleteStatusDetails is not null)
            throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);
          usage = ResponseUsage(terminal.Response.Usage);
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
      ProviderCredentials? credentials, string modelId, CancellationToken ct) {
    var client = new ChatClient(modelId, ProviderSdkAuthentication.Create(connection, credentials), Options(connection));
    var options = new ChatCompletionOptions { StoredOutputEnabled = false };
    var text = new StringBuilder();
    bool truncated = false;
    int completed = 0;
    ProviderUsage? usage = null;
    await foreach (var update in client.CompleteChatStreamingAsync(
        [new UserChatMessage(TestPrompt)], options, ct).ConfigureAwait(false)) {
      foreach (var part in update.ContentUpdate) AppendPreview(text, part.Text, ref truncated);
      if (update.FinishReason is { } finish) {
        if (finish != ChatFinishReason.Stop)
          throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
        if (++completed != 1) throw new ProviderOperationException(ProviderFailure.InvalidProviderResponse);
      }
      if (update.Usage is not null) usage = ChatUsage(update.Usage);
    }
    ct.ThrowIfCancellationRequested();
    if (completed != 1) throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
    return new(connection.Id, modelId, text.ToString(), truncated, usage);
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

#pragma warning disable SCME0001 // Read preserved wire presence: SDK integer properties default missing counts to zero.
  private static ProviderUsage? ResponseUsage(ResponseTokenUsage? usage) {
    if (usage is null) return null;
    long? cachedTokens = usage.InputTokenDetails is null ? null : Tokens(usage.InputTokenDetails.Patch, "$.cached_tokens"u8);
    return new(Tokens(usage.Patch, "$.input_tokens"u8), Tokens(usage.Patch, "$.output_tokens"u8),
        Tokens(usage.Patch, "$.total_tokens"u8), cachedTokens);
  }

  private static ProviderUsage ChatUsage(ChatTokenUsage usage) => new(
      Tokens(usage.Patch, "$.prompt_tokens"u8), Tokens(usage.Patch, "$.completion_tokens"u8),
      Tokens(usage.Patch, "$.total_tokens"u8),
      usage.InputTokenDetails is null ? null : Tokens(usage.InputTokenDetails.Patch, "$.cached_tokens"u8));

  private static long? Tokens(JsonPatch patch, ReadOnlySpan<byte> path) =>
      patch.TryGetValue(path, out long value) && value >= 0 ? value : null;
#pragma warning restore SCME0001

  private static bool CanClassify(Exception error, CancellationToken ct) => error is not ProviderOperationException &&
      !(error is OperationCanceledException && ct.IsCancellationRequested) &&
      error is ClientResultException or HttpRequestException or IOException or OperationCanceledException or
          JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException;
}

#pragma warning restore OPENAI001
