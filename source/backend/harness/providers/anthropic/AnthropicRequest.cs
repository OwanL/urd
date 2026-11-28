using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using Urd.Core.Usage;
using static Urd.Core.Providers.AnthropicJson;

#pragma warning disable MEAI001 // Validate background, continuation and reasoning options from the pinned MEAI SDK.

namespace Urd.Core.Providers;

internal static class AnthropicRequest {
  private const int FallbackMaxOutputTokens = 4096;
  private static readonly ChatRole developer = new("developer");

  internal static HttpRequestMessage Create(ProviderState state, IReadOnlyList<ChatMessage> original,
      IReadOnlyList<ChatMessage> messages, ChatOptions options, CancellationToken cancellationToken) {
    if (state.Connection is not { Route: ProviderRoute.GitHubCopilot, Api: LlmApi.AnthropicMessages,
        Authentication: AuthenticationMethod.OAuth })
      throw Invalid("unsupported Anthropic provider binding");
    if (state.Credentials is not { RequiresSignIn: false, AccessToken: { Length: > 0 } token })
      throw new ProviderOperationException(ProviderFailure.AuthenticationRequired);
    ValidateOptions(options);
    int? modelLimit = state.Catalog?.Models.FirstOrDefault(model => model.Id == options.ModelId)?.MaxOutputTokens;
    int maxTokens = options.MaxOutputTokens ?? (modelLimit is > 0 ? modelLimit.Value : FallbackMaxOutputTokens);
    // Opt in for conversation requests, without sending the local session ID to Copilot.
    bool cachePrefix = options.AdditionalProperties?.TryGetValue(OpenAIInferenceClient.SessionIdProperty,
        out object? sessionId) == true && sessionId is Guid id && id != Guid.Empty;

    using var body = new MemoryStream();
    using (var writer = new Utf8JsonWriter(body)) {
      writer.WriteStartObject();
      writer.WriteString("model", options.ModelId);
      writer.WriteBoolean("stream", true);
      writer.WriteNumber("max_tokens", maxTokens);
      if (options.Temperature is { } temperature) writer.WriteNumber("temperature", temperature);
      if (options.TopP is { } topP) writer.WriteNumber("top_p", topP);
      if (options.TopK is { } topK) writer.WriteNumber("top_k", topK);
      if (options.StopSequences is { Count: > 0 } stops) {
        writer.WriteStartArray("stop_sequences");
        foreach (string stop in stops) writer.WriteStringValue(stop);
        writer.WriteEndArray();
      }
      // A generic effort value cannot distinguish Claude's budgeted and adaptive thinking modes.
      WriteSystem(writer, messages, options.Instructions, cachePrefix);
      WriteMessages(writer, messages, cancellationToken);
      WriteTools(writer, options, cachePrefix);
      writer.WriteEndObject();
    }

    string endpoint = state.Connection.Endpoint.AbsoluteUri.TrimEnd('/') + "/v1/messages";
    var content = new ByteArrayContent(body.GetBuffer(), 0, checked((int)body.Length));
    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
    try {
      request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
      request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
      request.Headers.Add("anthropic-version", "2023-06-01");
      GitHubCopilotRequest.ApplyHeaders(request, original);
      return request;
    } catch {
      request.Dispose();
      throw;
    }
  }

  private static void ValidateOptions(ChatOptions options) {
    if (options.RawRepresentationFactory is not null || options.ConversationId is not null || options.ContinuationToken is not null ||
        options.AllowBackgroundResponses is not null || options.ResponseFormat is not null || options.Reasoning?.Output is not null ||
        options.FrequencyPenalty is not null || options.PresencePenalty is not null || options.Seed is not null ||
        options.MaxOutputTokens is <= 0 || options.Reasoning?.Effort is { } effort && !Enum.IsDefined(effort) ||
        options.AdditionalProperties?.Keys.Any(key => key is not (OpenAIInferenceClient.SessionIdProperty or
            ToolDraftBuffer.PropertyName or RequestContextEstimate.PropertyName or ChatGptSocketRun.PropertyName or
            UsageContext.PropertyName or RateLimitRetry.StatusProperty)) == true)
      throw Invalid("unsupported Anthropic chat options");
    if (options.Tools?.Any(tool => tool is not AIFunctionDeclaration function || string.IsNullOrWhiteSpace(function.Name) ||
        function.JsonSchema.ValueKind != JsonValueKind.Object ||
        function.AdditionalProperties.TryGetValue(ProviderModelOptions.StrictProperty, out object? strict) && strict is not false) == true ||
        options.Tools?.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != options.Tools?.Count)
      throw Invalid("unsupported Anthropic tool declaration");
    if (options.ToolMode is not (null or AutoChatToolMode or NoneChatToolMode or RequiredChatToolMode) ||
        options.ToolMode is RequiredChatToolMode required && (options.Tools is not { Count: > 0 } ||
            required.RequiredFunctionName is { } name && !options.Tools.Any(tool => tool.Name == name)))
      throw Invalid("unsupported Anthropic tool choice");
  }

  private static bool IsSystem(ChatMessage message) => message.Role == ChatRole.System || message.Role == developer;

  private static void WriteSystem(Utf8JsonWriter writer, IReadOnlyList<ChatMessage> messages, string? instructions,
      bool cachePrefix) {
    var parts = new List<string>();
    if (!string.IsNullOrEmpty(instructions)) parts.Add(instructions);
    foreach (AIContent part in messages.Where(IsSystem).SelectMany(message => message.Contents)) {
      if (part is not TextContent text) throw Invalid("non-text Anthropic system instruction");
      if (!string.IsNullOrEmpty(text.Text)) parts.Add(text.Text);
    }
    if (parts.Count == 0) return;
    writer.WriteStartArray("system");
    for (int i = 0; i < parts.Count; i++)
      WriteText(writer, parts[i], cachePrefix && i == parts.Count - 1);
    writer.WriteEndArray();
  }

  private static void WriteMessages(Utf8JsonWriter writer, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) {
    writer.WriteStartArray("messages");
    string? role = null;
    foreach (ChatMessage message in messages.Where(message => !IsSystem(message))) {
      cancellationToken.ThrowIfCancellationRequested();
      if (message.RawRepresentation is JsonElement { ValueKind: JsonValueKind.Array } blocks) {
        if (blocks.GetArrayLength() == 0) continue;
        StartRole("assistant");
        foreach (JsonElement block in blocks.EnumerateArray()) block.WriteTo(writer);
        continue;
      }
      string nextRole = message.Role == ChatRole.Assistant ? "assistant" : "user";
      foreach (AIContent part in message.Contents) {
        if (part is TextContent { Text.Length: 0 }) continue;
        StartRole(nextRole);
        WritePart(writer, part);
      }
    }
    if (role is null) throw Invalid("Anthropic requests require conversation messages");
    CloseRole();
    writer.WriteEndArray();

    void StartRole(string nextRole) {
      // In particular, adjacent tool results occupy the start of one user message.
      if (role == nextRole) return;
      if (role is not null) CloseRole();
      writer.WriteStartObject();
      writer.WriteString("role", nextRole);
      writer.WriteStartArray("content");
      role = nextRole;
    }

    void CloseRole() {
      writer.WriteEndArray();
      writer.WriteEndObject();
    }
  }

  private static void WritePart(Utf8JsonWriter writer, AIContent part) {
    switch (part) {
      case TextContent text:
        WriteText(writer, text.Text);
        break;
      case DataContent image:
        WriteImage(writer, image);
        break;
      case FunctionCallContent { InformationalOnly: false, Exception: null } call when
          !string.IsNullOrWhiteSpace(call.CallId) && !string.IsNullOrWhiteSpace(call.Name):
        writer.WriteStartObject();
        writer.WriteString("type", "tool_use");
        writer.WriteString("id", call.CallId);
        writer.WriteString("name", call.Name);
        writer.WritePropertyName("input");
        JsonSerializer.Serialize(writer, call.Arguments ?? new Dictionary<string, object?>());
        writer.WriteEndObject();
        break;
      case FunctionResultContent result when !string.IsNullOrWhiteSpace(result.CallId):
        WriteResult(writer, result);
        break;
      default:
        throw Invalid("unsupported Anthropic message content");
    }
  }

  private static void WriteResult(Utf8JsonWriter writer, FunctionResultContent result) {
    writer.WriteStartObject();
    writer.WriteString("type", "tool_result");
    writer.WriteString("tool_use_id", result.CallId);
    if (result.Exception is not null) writer.WriteBoolean("is_error", true);
    if (result.Result is IReadOnlyList<AIContent> parts) {
      writer.WriteStartArray("content");
      foreach (AIContent part in parts) {
        if (part is TextContent { Text.Length: 0 }) continue;
        WritePart(writer, part);
      }
      writer.WriteEndArray();
    } else {
      string? text = result.Result as string ?? (result.Result is null ? null : JsonSerializer.Serialize(result.Result));
      if (text is { Length: > 0 }) writer.WriteString("content", text);
    }
    writer.WriteEndObject();
  }

  private static void WriteText(Utf8JsonWriter writer, string text, bool cacheBreakpoint = false) {
    writer.WriteStartObject();
    writer.WriteString("type", "text");
    writer.WriteString("text", text);
    if (cacheBreakpoint) WriteCacheBreakpoint(writer);
    writer.WriteEndObject();
  }

  private static void WriteCacheBreakpoint(Utf8JsonWriter writer) {
    writer.WriteStartObject("cache_control");
    writer.WriteString("type", "ephemeral");
    writer.WriteEndObject();
  }

  private static void WriteImage(Utf8JsonWriter writer, DataContent image) {
    writer.WriteStartObject();
    writer.WriteString("type", "image");
    writer.WriteStartObject("source");
    writer.WriteString("type", "base64");
    writer.WriteString("media_type", image.MediaType);
    writer.WriteBase64String("data", image.Data.Span);
    writer.WriteEndObject();
    writer.WriteEndObject();
  }

  private static void WriteTools(Utf8JsonWriter writer, ChatOptions options, bool cachePrefix) {
    if (options.Tools is not { Count: > 0 } tools || options.ToolMode is NoneChatToolMode) return;
    // Anthropic orders tools before system blocks; the tool prefix can survive changed instructions.
    writer.WriteStartArray("tools");
    int remaining = tools.Count;
    foreach (var function in tools.Cast<AIFunctionDeclaration>()) {
      writer.WriteStartObject();
      writer.WriteString("name", function.Name);
      if (!string.IsNullOrEmpty(function.Description)) writer.WriteString("description", function.Description);
      writer.WritePropertyName("input_schema");
      function.JsonSchema.WriteTo(writer);
      if (--remaining == 0 && cachePrefix) WriteCacheBreakpoint(writer);
      writer.WriteEndObject();
    }
    writer.WriteEndArray();
    if (options.ToolMode is not RequiredChatToolMode && options.AllowMultipleToolCalls is null) return;
    writer.WriteStartObject("tool_choice");
    string? name = (options.ToolMode as RequiredChatToolMode)?.RequiredFunctionName;
    string choice = options.ToolMode is RequiredChatToolMode ? "any" : "auto";
    writer.WriteString("type", name is not null ? "tool" : choice);
    if (name is not null) writer.WriteString("name", name);
    if (options.AllowMultipleToolCalls is { } multiple) writer.WriteBoolean("disable_parallel_tool_use", !multiple);
    writer.WriteEndObject();
  }
}

#pragma warning restore MEAI001
