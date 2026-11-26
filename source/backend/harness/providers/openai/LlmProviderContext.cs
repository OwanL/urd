using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable OPENAI001 // The pinned SDK's native Responses and persistence APIs.

namespace Urd.Core.Providers;

internal static class LlmProviderContext {
  internal const int MaximumBytes = 4 * 1024 * 1024;
  internal const string AssistantPhase = "urd.assistant.phase";
  private const string ResponseKey = "urd.providerResponse";

  internal static LlmProviderResponse? GetResponse(ChatResponseUpdate update) =>
      update.AdditionalProperties?.TryGetValue(ResponseKey, out object? value) == true
          ? value as LlmProviderResponse : null;

  internal static void SetResponse(ChatResponseUpdate update, LlmProviderResponse response) =>
      (update.AdditionalProperties ??= [])[ResponseKey] = response;

  internal static ChatMessage Restore(LlmProviderResponse response) => Read(response, nativeReplay: false);

  internal static ChatMessage Replay(LlmProviderResponse response) => Read(response, nativeReplay: true);

  internal static LlmProviderResponse CaptureResponses(LlmProviderScope scope, IReadOnlyList<ResponseItem> items) {
    ValidateScope(scope);
    Require(scope.Api == LlmApi.OpenAIResponses);
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream)) {
      writer.WriteStartObject();
      writer.WriteNumber("version", 1);
      writer.WriteStartArray("items");
      foreach (ResponseItem item in items) {
        BinaryData data = ModelReaderWriter.Write(item);
        CheckSize(data.ToMemory().Length);
        writer.WriteRawValue(data.ToMemory().Span);
        writer.Flush();
        CheckSize(stream.Length);
      }
      writer.WriteEndArray();
      writer.WriteEndObject();
    }
    return Freeze(scope, stream);
  }

  internal static LlmProviderResponse CaptureChat(LlmProviderScope scope, ChatMessage message,
      string reasoningField = "reasoning_content") {
    ValidateScope(scope);
    Require(reasoningField is "reasoning_content" or "reasoning");
    Require(scope.Api == LlmApi.OpenAIChatCompletions && message.Role == ChatRole.Assistant);
    AssistantChatMessage native;
    if (message.RawRepresentation is AssistantChatMessage assistant) {
      native = assistant;
    } else {
      LlmMessageProjection.ValidatePortable(message, allowReasoning: true);
      native = new AssistantChatMessage(message.Contents.OfType<TextContent>()
          .Select(text => ChatMessageContentPart.CreateTextPart(text.Text)));
      foreach (FunctionCallContent call in message.Contents.OfType<FunctionCallContent>()) {
        Require(!call.InformationalOnly && call.Exception is null);
        native.ToolCalls.Add(ChatToolCall.CreateFunctionToolCall(call.CallId, call.Name,
            BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(call.Arguments ?? new Dictionary<string, object?>()))));
      }
      string reasoning = string.Concat(message.Contents.OfType<TextReasoningContent>().Select(content => content.Text));
      if (reasoning.Length != 0) {
        using JsonDocument document = JsonDocument.Parse(ModelReaderWriter.Write(native));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
          writer.WriteStartObject();
          foreach (JsonProperty property in document.RootElement.EnumerateObject())
            property.WriteTo(writer);
          writer.WriteString(reasoningField, reasoning);
          writer.WriteEndObject();
        }
        CheckSize(stream.Length);
        native = ModelReaderWriter.Read<AssistantChatMessage>(BinaryData.FromBytes(stream.ToArray()))!;
      }
    }

    BinaryData data = ModelReaderWriter.Write(native);
    CheckSize(data.ToMemory().Length);
    using var payload = JsonDocument.Parse(data);
    using var envelope = new MemoryStream();
    using (var writer = new Utf8JsonWriter(envelope)) {
      writer.WriteStartObject();
      writer.WriteNumber("version", 1);
      writer.WritePropertyName("message");
      payload.RootElement.WriteTo(writer);
      writer.WriteEndObject();
    }
    return Freeze(scope, envelope);
  }

  private static LlmProviderResponse Freeze(LlmProviderScope scope, MemoryStream stream) {
    CheckSize(stream.Length);
    var response = new LlmProviderResponse(scope, Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
    _ = Restore(response);
    return response;
  }

  private static ChatMessage Read(LlmProviderResponse response, bool nativeReplay) {
    try {
      CheckSize(Encoding.UTF8.GetByteCount(response.Json));
      using JsonDocument document = JsonDocument.Parse(response.Json);
      JsonElement root = document.RootElement;
      Require(root.GetProperty("version").GetInt32() == 1);
      ValidateScope(response.Scope);
      var message = new ChatMessage(ChatRole.Assistant, new List<AIContent>());
      if (response.Scope.Api == LlmApi.OpenAIResponses) {
        JsonElement items = root.GetProperty("items");
        Require(items.ValueKind == JsonValueKind.Array);
        foreach (JsonElement item in items.EnumerateArray()) {
          Require(item.ValueKind == JsonValueKind.Object);
          if (nativeReplay) {
            _ = RequiredString(item, "type");
            ResponseItem raw = ModelReaderWriter.Read<ResponseItem>(BinaryData.FromString(item.GetRawText()))!;
            message.Contents.Add(new AIContent { RawRepresentation = raw });
          } else {
            ExtractResponseItem(item, message.Contents);
          }
        }
      } else {
        Require(response.Scope.Api == LlmApi.OpenAIChatCompletions);
        JsonElement item = root.GetProperty("message");
        ExtractChat(item, message.Contents);
        if (nativeReplay)
          message.RawRepresentation = ModelReaderWriter.Read<AssistantChatMessage>(BinaryData.FromString(item.GetRawText()));
      }
      if (!nativeReplay)
        message.RawRepresentation = response;
      return message;
    } catch (Exception error) when (error is JsonException or InvalidOperationException or
        KeyNotFoundException or ArgumentException or FormatException) {
      // Never include provider payloads in exception messages or inner exceptions.
      throw new InvalidDataException("Invalid provider context.");
    }
  }

  private static void ExtractResponseItem(JsonElement item, IList<AIContent> contents) {
    switch (RequiredString(item, "type")) {
      case "message":
        ExtractResponseMessage(item, contents);
        break;
      case "function_call":
        // Only bare functions and Urd's ChatGPT namespace identify a local tool. A foreign
        // namespace must not become a same-named local call when the destination changes.
        bool local = !item.TryGetProperty("namespace", out JsonElement ns) || ns.ValueKind == JsonValueKind.Null ||
            (ns.ValueKind == JsonValueKind.String && ns.ValueEquals("urd"));
        contents.Add(local ? ReadCall(item, RequiredString(item, "call_id")) : new AIContent());
        break;
      case "reasoning":
        contents.Add(ReadReasoning(item));
        break;
      default:
        contents.Add(new AIContent());
        break;
    }
  }

  private static void ExtractResponseMessage(JsonElement item, IList<AIContent> contents) {
    if (RequiredString(item, "role") != "assistant") {
      contents.Add(new AIContent());
      return;
    }

    JsonElement parts = item.GetProperty("content");
    string? phase = item.TryGetProperty("phase", out JsonElement value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() : null;
    // Phase belongs to the whole message, not to the response or to each text fragment.
    if (phase is "commentary" or "final_answer" && parts.GetArrayLength() > 0 &&
        parts.EnumerateArray().All(part => RequiredString(part, "type") == "output_text")) {
      string text = string.Concat(parts.EnumerateArray().Select(part => RequiredString(part, "text", allowEmpty: true)));
      contents.Add(new TextContent(text) { AdditionalProperties = new() { [AssistantPhase] = phase } });
      return;
    }

    foreach (JsonElement part in parts.EnumerateArray())
      contents.Add(RequiredString(part, "type") == "output_text"
          ? new TextContent(RequiredString(part, "text", allowEmpty: true)) : new AIContent());
  }

  private static TextReasoningContent ReadReasoning(JsonElement item) {
    var reasoning = new StringBuilder();
    foreach (JsonElement summary in item.GetProperty("summary").EnumerateArray()) {
      Require(RequiredString(summary, "type") == "summary_text");
      reasoning.Append(RequiredString(summary, "text", allowEmpty: true));
    }
    return new TextReasoningContent(reasoning.ToString()) {
      ProtectedData = item.TryGetProperty("encrypted_content", out JsonElement encrypted) &&
          encrypted.ValueKind != JsonValueKind.Null ? encrypted.GetString() : null
    };
  }

  private static void ExtractChat(JsonElement item, IList<AIContent> contents) {
    Require(RequiredString(item, "role") == "assistant");
    foreach (string unsupported in new[] { "name", "refusal", "function_call", "audio" })
      Require(!item.TryGetProperty(unsupported, out JsonElement value) || value.ValueKind == JsonValueKind.Null);

    if (item.TryGetProperty("content", out JsonElement content) && content.ValueKind != JsonValueKind.Null)
      ExtractChatText(content, contents);
    if (item.TryGetProperty("tool_calls", out JsonElement calls) && calls.ValueKind != JsonValueKind.Null)
      ExtractChatCalls(calls, contents);
    string? reasoning = null;
    foreach (string field in new[] { "reasoning_content", "reasoning" }) {
      if (!item.TryGetProperty(field, out JsonElement value) || value.ValueKind == JsonValueKind.Null) continue;
      Require(value.ValueKind == JsonValueKind.String);
      string? text = value.GetString();
      if (string.IsNullOrEmpty(text)) continue;
      Require(reasoning is null);
      reasoning = text;
    }
    if (reasoning is not null)
      contents.Add(new TextReasoningContent(reasoning));
  }

  private static void ExtractChatText(JsonElement content, IList<AIContent> contents) {
    if (content.ValueKind == JsonValueKind.String) {
      contents.Add(new TextContent(content.GetString()));
      return;
    }
    foreach (JsonElement part in content.EnumerateArray()) {
      Require(RequiredString(part, "type") == "text");
      contents.Add(new TextContent(RequiredString(part, "text", allowEmpty: true)));
    }
  }

  private static void ExtractChatCalls(JsonElement calls, IList<AIContent> contents) {
    foreach (JsonElement call in calls.EnumerateArray()) {
      Require(RequiredString(call, "type") == "function");
      JsonElement function = call.GetProperty("function");
      contents.Add(ReadCall(function, RequiredString(call, "id")));
    }
  }

  private static FunctionCallContent ReadCall(JsonElement item, string id) {
    using JsonDocument arguments = JsonDocument.Parse(RequiredString(item, "arguments"));
    Require(arguments.RootElement.ValueKind == JsonValueKind.Object);
    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
    foreach (JsonProperty property in arguments.RootElement.EnumerateObject())
      values.Add(property.Name, property.Value.Clone());
    return new FunctionCallContent(id, RequiredString(item, "name"), values);
  }

  internal static void ValidateScope(LlmProviderScope scope) {
    Require(scope.Generation >= 0 && scope.ConnectionId != Guid.Empty);
    Require(scope.Api is LlmApi.OpenAIResponses or LlmApi.OpenAIChatCompletions);
    Require(!string.IsNullOrWhiteSpace(scope.ModelId) && scope.ModelId.Length <= 256 && !scope.ModelId.Any(char.IsControl));
  }

  private static string RequiredString(JsonElement value, string name, bool allowEmpty = false) {
    string? result = value.GetProperty(name).GetString();
    Require(result is not null && (allowEmpty || !string.IsNullOrWhiteSpace(result)));
    return result!;
  }

  private static void Require(bool valid) {
    if (!valid)
      throw new InvalidDataException("Invalid provider context.");
  }

  private static void CheckSize(long size) {
    if (size > MaximumBytes)
      throw new InvalidDataException("Provider context exceeds the 4 MiB limit.");
  }
}

#pragma warning restore OPENAI001
