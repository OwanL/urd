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
internal sealed class ProviderStreamState(LlmProviderScope scope, IEnumerable<AITool>? tools, bool isChatGpt,
    ProviderCredentials? credentials) {
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
          throw Invalid("unsupported streamed content type");
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
    if (terminal is not null) throw Invalid("response update after terminal");
    switch (update.RawRepresentation) {
      case StreamingResponseCompletedUpdate completed:
        if (completed.Response?.Status != ResponseStatus.Completed) throw Invalid("response status is not completed");
        if (completed.Response.Error is not null) throw Invalid("completed response includes provider error");
        if (completed.Response.IncompleteStatusDetails is not null) throw Invalid("completed response includes incomplete details");
        output = completed.Response.OutputItems.ToArray();
        int bytes = 0;
        for (int index = 0; index < output.Length; index++) {
          ValidateItem(output[index]);
          string json = Encode(output[index]);
          bytes = Add(bytes, Encoding.UTF8.GetByteCount(json), "completed output size limit exceeded");
          if (completedItems.TryGetValue(index, out string? previous) && previous != json)
            throw Invalid("completed output item differs from streamed item");
        }
        if (completedItems.Keys.Any(index => index >= output.Length)) throw Invalid("output item index outside completed output");
        terminal = update;
        break;
      case StreamingResponseIncompleteUpdate:
        throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
      case StreamingResponseFailedUpdate failed:
        throw StreamFailure(failed.Response?.Error?.Code.ToString(), failed.Response?.Error?.Message,
            failed.Response?.Error?.Param, ProviderDiagnosticTerminalKind.ResponseFailed);
      case StreamingResponseErrorUpdate error:
        throw StreamFailure(error.Code, error.Message, error.Param,
            ProviderDiagnosticTerminalKind.StandaloneError);
      case StreamingResponseOutputItemDoneUpdate done:
        ValidateItem(done.Item);
        string encoded = Encode(done.Item);
        completedBytes = Add(completedBytes, Encoding.UTF8.GetByteCount(encoded), "completed output size limit exceeded");
        if (done.OutputIndex < 0) throw Invalid("negative output item index");
        if (!completedItems.TryAdd(done.OutputIndex, encoded)) throw Invalid("duplicate output item index");
        break;
      case null:
        throw Invalid("missing native response update");
    }
  }

  private void AcceptChat(ChatResponseUpdate update) {
    if (update.RawRepresentation is not StreamingChatCompletionUpdate native) {
      // The official adapter assembles function arguments after the native enumerator has closed.
      if (update.RawRepresentation is not null) throw Invalid("unexpected non-native chat update");
      if (update.Contents.Any(content => content is not FunctionCallContent))
        throw Invalid("unexpected content in adapted chat update");
      return;
    }
    AcceptChatReasoning(native, update);
    bool hasContent = native.ContentUpdate.Count != 0 || native.ToolCallUpdates.Count != 0 ||
        native.RefusalUpdate is not null || native.OutputAudioUpdate is not null ||
        update.Contents.Any(content => content is TextContent or TextReasoningContent);
    if (terminal is not null && hasContent) throw Invalid("chat content after finish");
    if (terminal is not null && native.FinishReason is not null) throw Invalid("duplicate chat finish");
    if (native.RefusalUpdate is not null) throw Invalid("unsupported refusal update");
    if (native.OutputAudioUpdate is not null) throw Invalid("unsupported audio update");
    foreach (var call in native.ToolCallUpdates) {
      if (call.Index < 0) throw Invalid("negative tool call index");
      if (call.Kind is { } kind && kind != ChatToolCallKind.Function) throw Invalid("unsupported tool call kind");
      // Empty argument fragments still allocate an assembly entry inside the official adapter.
      if (chatCallIndexes.Add(call.Index)) argumentBytes = Add(argumentBytes, 256, "tool call size limit exceeded");
      argumentBytes = Add(argumentBytes, call.FunctionArgumentsUpdate?.ToMemory().Length ?? 0, "tool call size limit exceeded");
      argumentBytes = Add(argumentBytes, Encoding.UTF8.GetByteCount(call.ToolCallId ?? "") + Encoding.UTF8.GetByteCount(call.FunctionName ?? ""),
          "tool call size limit exceeded");
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
    if (hasAlternate && hasStandard) throw Invalid("multiple reasoning fields in one update");
    if (!hasAlternate && !hasStandard) return;
    string field = hasAlternate ? "reasoning" : "reasoning_content";
    if (reasoningField is not null && reasoningField != field) throw Invalid("reasoning field changed during stream");
    reasoningField = field;
    // MEAI already extracts reasoning_content; the compatible reasoning alias needs explicit preservation.
    if (hasAlternate) {
      if (update.Contents.Any(content => content is TextReasoningContent)) throw Invalid("duplicate reasoning content");
      update.Contents.Add(new TextReasoningContent(alternate));
    }
  }

  private static string? ReadReasoning(StreamingChatCompletionUpdate native, ReadOnlySpan<byte> path) =>
      native.Patch.TryGetJson(path, out ReadOnlyMemory<byte> json) ? JsonSerializer.Deserialize<string>(json.Span) : null;

  internal ChatResponseUpdate Complete() {
    if (terminal is null) throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
    var calls = scope.Api == LlmApi.OpenAIResponses ? ResponseCalls() : chatCalls;
    if (scope.Api == LlmApi.OpenAIChatCompletions && chatCallIndexes.Count != calls.Count)
      throw Invalid("tool call count does not match streamed indexes");
    if (scope.Api == LlmApi.OpenAIChatCompletions && toolFinish != (calls.Count > 0))
      throw Invalid("tool finish does not match tool calls");
    if (calls.Select(call => call.CallId).Distinct(StringComparer.Ordinal).Count() != calls.Count)
      throw Invalid("duplicate function call ID");
    foreach (var call in calls) ValidateCall(call);
    LlmProviderResponse response;
    if (scope.Api == LlmApi.OpenAIResponses) {
      // A completed envelope must agree with what was already shown to the caller.
      string finalText = string.Concat(output!.OfType<MessageResponseItem>()
          .SelectMany(item => item.Content).Select(part => part.Text));
      if (finalText != text.ToString()) throw Invalid("final text differs from streamed text");
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
      if (arguments.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("function call arguments are not an object");
      var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.FunctionArguments.ToString())!;
      calls.Add(new FunctionCallContent(item.CallId, item.FunctionName, values));
    }
    return calls;
  }

  private void ValidateCall(FunctionCallContent call) {
    if (string.IsNullOrWhiteSpace(call.CallId)) throw Invalid("missing function call ID");
    if (string.IsNullOrWhiteSpace(call.Name)) throw Invalid("missing function name");
    if (!allowedFunctions.Contains(call.Name)) throw Invalid("call to undeclared tool", call.Name);
    if (call.Arguments is null) throw Invalid("missing function call arguments");
    if (call.Exception is not null) throw Invalid("function call contains execution error");
    if (call.InformationalOnly) throw Invalid("informational function call");
  }

  private void ValidateItem(ResponseItem item) {
    using var document = JsonDocument.Parse(Encode(item));
    var root = document.RootElement;
    string? type = root.GetProperty("type").GetString();
    if (type is "message") {
      if (root.GetProperty("role").GetString() != "assistant") throw Invalid("message role is not assistant");
      if (root.GetProperty("content").EnumerateArray().Any(part => part.GetProperty("type").GetString() != "output_text"))
        throw Invalid("unsupported message content type");
    } else if (type is "function_call") {
      if (root.TryGetProperty("status", out var status) && status.GetString() != "completed")
        throw Invalid("function call is not completed");
      string? functionNamespace = root.TryGetProperty("namespace", out var ns) ? ns.GetString() : null;
      if (isChatGpt ? functionNamespace != "urd" : functionNamespace is not null)
        throw Invalid("function call namespace mismatch");
    } else if (type != "reasoning") {
      throw Invalid("unexpected output item type", type);
    }
  }

  private void Count(string? value) => contentBytes = Add(contentBytes, Encoding.UTF8.GetByteCount(value ?? ""),
      "streamed content size limit exceeded");
  private static int Add(int total, int count, string reason) =>
      count <= LlmProviderContext.MaximumBytes - total ? total + count : throw Invalid(reason);
  private static string Encode(ResponseItem item) => ModelReaderWriter.Write(item).ToString();
  private ProviderOperationException StreamFailure(string? code, string? message, string? param,
      ProviderDiagnosticTerminalKind terminalKind) =>
      ProviderOperationException.FromProviderStreamError(code, message, param, credentials, terminalKind);

  private static ProviderOperationException Invalid(string reason, string? identifier = null) {
    if (IsSafeIdentifier(identifier)) reason += $" '{identifier}'";
    return new(ProviderFailure.InvalidProviderResponse, reason);
  }

  private static bool IsSafeIdentifier(string? value) {
    if (value is not { Length: > 0 and <= 64 }) return false;
    return value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or
        '_' or '.' or ':' or '-');
  }
}

#pragma warning restore OPENAI001, MEAI001, SCME0001
