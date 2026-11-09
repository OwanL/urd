using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable OPENAI001, MEAI001, SCME0001 // Read compatible reasoning fields through the pinned SDK patch API.

namespace Urd.Core.Providers;

// Bounded completed state, never a transcript of streaming updates. Native terminals authorize completion.
internal sealed class ProviderStreamState(LlmProviderScope scope, IEnumerable<AITool>? tools, bool isChatGpt) {
  private readonly HashSet<string> allowedFunctions = tools?.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal) ?? [];
  private readonly SortedDictionary<int, string> completedItems = [];
  private readonly HashSet<int> chatCallIndexes = [];
  private readonly List<FunctionCallContent> chatCalls = [];
  private readonly StringBuilder text = new();
  private readonly StringBuilder reasoning = new();
  private ResponseItem[]? output;
  private ChatResponseUpdate? terminal;
  private bool toolFinish;
  private string? reasoningField;
  private int contentBytes;
  private int completedBytes;
  private int argumentBytes;

  internal ChatResponseUpdate? Accept(ChatResponseUpdate update) {
    if (scope.Api == LlmApi.OpenAIResponses) AcceptResponses(update);
    else AcceptChat(update);
    foreach (var content in update.Contents) {
      switch (content) {
        case TextContent value:
          Count(value.Text);
          text.Append(value.Text);
          break;
        case TextReasoningContent value:
          Count(value.Text);
          Count(value.ProtectedData);
          reasoning.Append(value.Text);
          break;
        case FunctionCallContent call:
          ValidateCall(call);
          if (scope.Api == LlmApi.OpenAIChatCompletions) chatCalls.Add(call);
          break;
        case UsageContent:
          break;
        case AIContent when content.GetType() == typeof(AIContent) && scope.Api == LlmApi.OpenAIResponses:
          // Opaque native items live in the codec, not the portable streamed content.
          break;
        default:
          throw Invalid();
      }
    }
    update.Contents = update.Contents.Where(content => content is TextContent or TextReasoningContent or UsageContent).ToList();
    update.FinishReason = null;
    update.ConversationId = null;
    update.ContinuationToken = null;
    update.RawRepresentation = null;
    return update.Contents.Count == 0 ? null : update;
  }

  private void AcceptResponses(ChatResponseUpdate update) {
    if (terminal is not null) throw Invalid();
    switch (update.RawRepresentation) {
      case StreamingResponseCompletedUpdate completed:
        if (completed.Response?.Status != ResponseStatus.Completed || completed.Response.Error is not null ||
            completed.Response.IncompleteStatusDetails is not null)
          throw Invalid();
        output = completed.Response.OutputItems.ToArray();
        int bytes = 0;
        for (int index = 0; index < output.Length; index++) {
          ValidateItem(output[index]);
          string json = Encode(output[index]);
          bytes = Add(bytes, Encoding.UTF8.GetByteCount(json));
          if (completedItems.TryGetValue(index, out string? previous) && previous != json) throw Invalid();
        }
        if (completedItems.Keys.Any(index => index >= output.Length)) throw Invalid();
        terminal = update;
        break;
      case StreamingResponseIncompleteUpdate:
        throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
      case StreamingResponseFailedUpdate or StreamingResponseErrorUpdate:
        throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
      case StreamingResponseOutputItemDoneUpdate done:
        ValidateItem(done.Item);
        string encoded = Encode(done.Item);
        completedBytes = Add(completedBytes, Encoding.UTF8.GetByteCount(encoded));
        if (done.OutputIndex < 0 || !completedItems.TryAdd(done.OutputIndex, encoded)) throw Invalid();
        break;
      case null:
        throw Invalid();
    }
  }

  private void AcceptChat(ChatResponseUpdate update) {
    if (update.RawRepresentation is not StreamingChatCompletionUpdate native) {
      // The official adapter assembles function arguments after the native enumerator has closed.
      if (update.RawRepresentation is not null || update.Contents.Any(content => content is not FunctionCallContent)) throw Invalid();
      return;
    }
    AcceptChatReasoning(native, update);
    bool hasContent = native.ContentUpdate.Count != 0 || native.ToolCallUpdates.Count != 0 ||
        native.RefusalUpdate is not null || native.OutputAudioUpdate is not null ||
        update.Contents.Any(content => content is TextContent or TextReasoningContent);
    if (terminal is not null && (hasContent || native.FinishReason is not null)) throw Invalid();
    if (native.RefusalUpdate is not null || native.OutputAudioUpdate is not null) throw Invalid();
    foreach (var call in native.ToolCallUpdates) {
      if (call.Index < 0 || call.Kind is { } kind && kind != ChatToolCallKind.Function) throw Invalid();
      // Empty argument fragments still allocate an assembly entry inside the official adapter.
      if (chatCallIndexes.Add(call.Index)) argumentBytes = Add(argumentBytes, 256);
      argumentBytes = Add(argumentBytes, call.FunctionArgumentsUpdate?.ToMemory().Length ?? 0);
      argumentBytes = Add(argumentBytes, Encoding.UTF8.GetByteCount(call.ToolCallId ?? "") + Encoding.UTF8.GetByteCount(call.FunctionName ?? ""));
    }
    if (native.FinishReason is not { } finish) return;
    if (finish != OpenAI.Chat.ChatFinishReason.Stop && finish != OpenAI.Chat.ChatFinishReason.ToolCalls)
      throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
    toolFinish = finish == OpenAI.Chat.ChatFinishReason.ToolCalls;
    terminal = update;
  }

  private void AcceptChatReasoning(StreamingChatCompletionUpdate native, ChatResponseUpdate update) {
    string? alternate = ReadReasoning(native, "$.choices[0].delta.reasoning"u8);
    string? standard = ReadReasoning(native, "$.choices[0].delta.reasoning_content"u8);
    bool hasAlternate = !string.IsNullOrEmpty(alternate);
    bool hasStandard = !string.IsNullOrEmpty(standard);
    if (hasAlternate && hasStandard) throw Invalid();
    if (!hasAlternate && !hasStandard) return;
    string field = hasAlternate ? "reasoning" : "reasoning_content";
    if (reasoningField is not null && reasoningField != field) throw Invalid();
    reasoningField = field;
    // MEAI already extracts reasoning_content; the compatible reasoning alias needs explicit preservation.
    if (hasAlternate) {
      if (update.Contents.Any(content => content is TextReasoningContent)) throw Invalid();
      update.Contents.Add(new TextReasoningContent(alternate));
    }
  }

  private static string? ReadReasoning(StreamingChatCompletionUpdate native, ReadOnlySpan<byte> path) =>
      native.Patch.TryGetJson(path, out ReadOnlyMemory<byte> json) ? JsonSerializer.Deserialize<string>(json.Span) : null;

  internal ChatResponseUpdate Complete() {
    if (terminal is null) throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
    var calls = scope.Api == LlmApi.OpenAIResponses ? ResponseCalls() : chatCalls;
    if (scope.Api == LlmApi.OpenAIChatCompletions &&
        (chatCallIndexes.Count != calls.Count || toolFinish != (calls.Count > 0))) throw Invalid();
    if (calls.Select(call => call.CallId).Distinct(StringComparer.Ordinal).Count() != calls.Count) throw Invalid();
    foreach (var call in calls) ValidateCall(call);
    LlmProviderResponse response;
    if (scope.Api == LlmApi.OpenAIResponses) {
      // A completed envelope must agree with what was already shown to the caller.
      string finalText = string.Concat(output!.OfType<MessageResponseItem>()
          .SelectMany(item => item.Content).Select(part => part.Text));
      if (finalText != text.ToString()) throw Invalid();
      response = LlmProviderContext.CaptureResponses(scope, output!);
    } else {
      var message = new ChatMessage(ChatRole.Assistant, text.ToString());
      if (reasoning.Length > 0) message.Contents.Add(new TextReasoningContent(reasoning.ToString()));
      foreach (var call in calls) message.Contents.Add(call);
      response = LlmProviderContext.CaptureChat(scope, message, reasoningField ?? "reasoning_content");
    }
    var final = new ChatResponseUpdate(ChatRole.Assistant, calls.Cast<AIContent>().ToList()) {
      ResponseId = terminal.ResponseId,
      MessageId = terminal.MessageId,
      ModelId = terminal.ModelId,
      CreatedAt = terminal.CreatedAt,
      FinishReason = calls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop
    };
    LlmProviderContext.SetResponse(final, response);
    return final;
  }

  private List<FunctionCallContent> ResponseCalls() {
    List<FunctionCallContent> calls = [];
    foreach (var item in output!.OfType<FunctionCallResponseItem>()) {
      using var arguments = JsonDocument.Parse(item.FunctionArguments);
      if (arguments.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
      var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.FunctionArguments.ToString())!;
      calls.Add(new FunctionCallContent(item.CallId, item.FunctionName, values));
    }
    return calls;
  }

  private void ValidateCall(FunctionCallContent call) {
    if (string.IsNullOrWhiteSpace(call.CallId) || string.IsNullOrWhiteSpace(call.Name) ||
        !allowedFunctions.Contains(call.Name) || call.Arguments is null || call.Exception is not null || call.InformationalOnly)
      throw Invalid();
  }

  private void ValidateItem(ResponseItem item) {
    using var document = JsonDocument.Parse(Encode(item));
    var root = document.RootElement;
    string? type = root.GetProperty("type").GetString();
    if (type is "message") {
      if (root.GetProperty("role").GetString() != "assistant" || root.GetProperty("content").EnumerateArray()
          .Any(part => part.GetProperty("type").GetString() != "output_text")) throw Invalid();
    } else if (type is "function_call") {
      if (root.TryGetProperty("status", out var status) && status.GetString() != "completed") throw Invalid();
      string? functionNamespace = root.TryGetProperty("namespace", out var ns) ? ns.GetString() : null;
      if (isChatGpt ? functionNamespace != "urd" : functionNamespace is not null) throw Invalid();
    } else if (type != "reasoning") {
      throw Invalid();
    }
  }

  private void Count(string? value) => contentBytes = Add(contentBytes, Encoding.UTF8.GetByteCount(value ?? ""));
  private static int Add(int total, int count) => count <= LlmProviderContext.MaximumBytes - total ? total + count : throw Invalid();
  private static string Encode(ResponseItem item) => ModelReaderWriter.Write(item).ToString();
  private static ProviderOperationException Invalid() => new(ProviderFailure.InvalidProviderResponse);
}

#pragma warning restore OPENAI001, MEAI001, SCME0001
