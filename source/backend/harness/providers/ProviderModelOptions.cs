using System.ClientModel.Primitives;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;

#pragma warning disable OPENAI001, MEAI001

namespace Urd.Core.Providers;

internal static class ProviderModelOptions {
  internal static ChatOptions Prepare(ProviderConnection connection, ChatOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrWhiteSpace(options.ModelId) || options.RawRepresentationFactory is not null ||
        options.ConversationId is not null || options.ContinuationToken is not null ||
        options.AllowBackgroundResponses is not null || options.AdditionalProperties is { Count: > 0 } ||
        options.TopK is not null || options.ResponseFormat is not null || options.Reasoning?.Output is not null)
      throw Invalid();
    if (options.Reasoning?.Effort is { } effort && !Enum.IsDefined(effort)) throw Invalid();
    if (options.Tools?.Any(tool => tool is not AIFunctionDeclaration) == true ||
        options.Tools?.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != options.Tools?.Count)
      throw Invalid();
    if (options.Tools?.Any(tool => string.IsNullOrWhiteSpace(tool.Name)) == true) throw Invalid();
    if (options.ToolMode is not null and not AutoChatToolMode and not NoneChatToolMode and not RequiredChatToolMode)
      throw Invalid();
    if (options.ToolMode is RequiredChatToolMode required &&
        (options.Tools is not { Count: > 0 } || required.RequiredFunctionName is { } name && !options.Tools.Any(tool => tool.Name == name)))
      throw Invalid();
    if (connection.Api == LlmApi.OpenAIResponses &&
        (options.FrequencyPenalty is not null || options.PresencePenalty is not null || options.Seed is not null || options.StopSequences is { Count: > 0 }))
      throw Invalid();
    bool isChatGpt = connection.Route == ProviderRoute.ChatGpt;
    // Namespace wrapping bypasses MEAI's strict-schema transformation; do not silently ignore it.
    if (isChatGpt && options.Tools?.Any(tool => tool.AdditionalProperties.ContainsKey("strict")) == true)
      throw Invalid();
    if (isChatGpt && (connection.Api != LlmApi.OpenAIResponses || options.Temperature is not null || options.TopP is not null ||
        options.MaxOutputTokens is not null || options.FrequencyPenalty is not null || options.PresencePenalty is not null ||
        options.Seed is not null || options.StopSequences is { Count: > 0 } || options.AllowMultipleToolCalls is not null ||
        options.ToolMode is not null and not AutoChatToolMode))
      throw Invalid();

    var result = options.Clone();
    if (connection.Api == LlmApi.OpenAIResponses) {
      // Only our factory can supply SDK options. Callers cannot enable storage, continuation or hosted tools.
      result.RawRepresentationFactory = _ => ModelReaderWriter.Read<CreateResponseOptions>(BinaryData.FromString(
          """{"store":false,"stream":true,"include":["reasoning.encrypted_content"]}"""))!;
      if (isChatGpt && options.Tools is { Count: > 0 }) result.Tools = [CreateNamespacedTools(options.Tools).AsAITool()];
    } else {
      result.RawRepresentationFactory = _ => new ChatCompletionOptions { StoredOutputEnabled = false };
    }
    return result;
  }

  private static ResponseTool CreateNamespacedTools(IEnumerable<AITool> tools) {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartObject();
      writer.WriteString("type", "namespace");
      writer.WriteString("name", "urd");
      writer.WriteString("description", "Tools provided by Urd.");
      writer.WritePropertyName("tools");
      writer.WriteStartArray();
      foreach (var function in tools.Cast<AIFunctionDeclaration>()) {
        writer.WriteStartObject();
        writer.WriteString("type", "function");
        writer.WriteString("name", function.Name);
        if (function.Description is not null) writer.WriteString("description", function.Description);
        writer.WritePropertyName("parameters");
        function.JsonSchema.WriteTo(writer);
        writer.WriteEndObject();
      }
      writer.WriteEndArray();
      writer.WriteEndObject();
    }
    if (buffer.Length > LlmProviderContext.MaximumBytes) throw Invalid();
    // Raw SDK tools retain the namespace schema even when the pinned SDK has no concrete namespace type.
    return ModelReaderWriter.Read<ResponseTool>(BinaryData.FromBytes(buffer.ToArray()))!;
  }

  private static ProviderOperationException Invalid() => new(ProviderFailure.InvalidProviderResponse);
}

#pragma warning restore OPENAI001, MEAI001
