using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Execution;
using static Urd.Core.Providers.AnthropicJson;

namespace Urd.Core.Providers;

internal sealed class AnthropicStreamState(LlmProviderScope scope, ChatOptions options, ProviderCredentials? credentials,
    ToolDraftBuffer? drafts, bool estimateContext) {
  private readonly AnthropicUsage usage = new();
  private readonly List<JsonElement> blocks = [];
  private readonly List<FunctionCallContent> calls = [];
  private readonly HashSet<string> callIds = new(StringComparer.Ordinal);
  private readonly HashSet<string> allowedTools = options.ToolMode is NoneChatToolMode ? [] :
      new(options.Tools?.Select(tool => tool.Name) ?? [], StringComparer.Ordinal);
  private readonly bool requireCall = options.ToolMode is RequiredChatToolMode;
  private readonly string? requiredTool = (options.ToolMode as RequiredChatToolMode)?.RequiredFunctionName;
  private readonly bool singleCall = options.AllowMultipleToolCalls == false;
  private AnthropicContentBlock? active;
  private int remainingBytes = LlmProviderContext.MaximumBytes;
  private string? messageId, model, stopReason;
  private bool stopped, hasMessageDelta;

  internal ChatResponseUpdate? Accept(SseItem<JsonElement> item) {
    JsonElement data = item.Data;
    string type = RequiredString(data, "type");
    if (item.EventType != type) throw Invalid("Anthropic SSE type does not match its payload");
    if (type == "ping") return null;
    if (type == "error") throw AnthropicErrors.FromStream(data, credentials);
    if (stopped) throw Invalid("Anthropic event after message_stop");
    if (messageId is null && type != "message_start") throw Invalid("missing Anthropic message_start");
    return type switch {
      "message_start" => StartMessage(data),
      "content_block_start" => StartBlock(data),
      "content_block_delta" => DeltaBlock(data),
      "content_block_stop" => StopBlock(data),
      "message_delta" => DeltaMessage(data),
      "message_stop" => StopMessage(),
      _ => throw Invalid("unsupported Anthropic stream event")
    };
  }

  private ChatResponseUpdate? StartMessage(JsonElement data) {
    JsonElement message = data.GetProperty("message");
    if (messageId is not null || RequiredString(message, "role") != "assistant" ||
        RequiredString(message, "type") != "message" || message.GetProperty("content").GetArrayLength() != 0 ||
        message.TryGetProperty("stop_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
      throw Invalid("invalid Anthropic message_start");
    messageId = RequiredString(message, "id");
    model = RequiredString(message, "model");
    return UsageUpdate(message);
  }

  private ChatResponseUpdate? StartBlock(JsonElement data) {
    RequireBlockIndex(data, open: false);
    active = new(data.GetProperty("content_block"), remainingBytes);
    if (active.Type == "tool_use") {
      if (!allowedTools.Contains(active.Name!) || requiredTool is not null && requiredTool != active.Name)
        throw Invalid("Anthropic call to an undeclared or disabled tool");
      if (!callIds.Add(active.Id!)) throw Invalid("duplicate Anthropic tool call ID");
      if (singleCall && callIds.Count != 1) throw Invalid("multiple Anthropic tool calls are disabled");
      drafts?.Append(blocks.Count, active.Id, active.Name, default);
    }
    return Display(active.InitialDisplay());
  }

  private ChatResponseUpdate? DeltaBlock(JsonElement data) {
    RequireBlockIndex(data, open: true);
    var (display, arguments) = active!.Append(data.GetProperty("delta"));
    if (arguments is not { Length: > 0 } || drafts is null && !estimateContext) return Display(display);
    byte[] bytes = Encoding.UTF8.GetBytes(arguments);
    drafts?.Append(blocks.Count, null, null, bytes);
    if (!estimateContext) return null;
    var update = Update([]);
    (update.AdditionalProperties ??= [])[RequestContextEstimate.ToolArgumentDeltaProperty] = BinaryData.FromBytes(bytes);
    return update;
  }

  private ChatResponseUpdate? StopBlock(JsonElement data) {
    RequireBlockIndex(data, open: true);
    JsonElement block = active!.Complete();
    remainingBytes -= Encoding.UTF8.GetByteCount(block.GetRawText());
    if (remainingBytes < 0) throw Invalid("Anthropic content exceeds the retained context limit");
    if (active.Type == "tool_use") {
      var call = AnthropicContentBlock.ReadCall(block);
      calls.Add(call);
      if (drafts is not null) drafts.CompleteItem(blocks.Count, call.CallId, call.Name,
          Encoding.UTF8.GetBytes(block.GetProperty("input").GetRawText()));
    }
    blocks.Add(block);
    active = null;
    return null;
  }

  private void RequireBlockIndex(JsonElement data, bool open) {
    if (hasMessageDelta || open != (active is not null) || !data.GetProperty("index").TryGetInt32(out int index) || index != blocks.Count)
      throw Invalid("unexpected Anthropic content block index or order");
  }

  private ChatResponseUpdate? DeltaMessage(JsonElement data) {
    if (active is not null) throw Invalid("Anthropic message_delta before content_block_stop");
    hasMessageDelta = true;
    var delta = data.GetProperty("delta");
    if (delta.ValueKind != JsonValueKind.Object) throw Invalid("invalid Anthropic message_delta");
    if (delta.TryGetProperty("stop_reason", out var reason) && reason.ValueKind != JsonValueKind.Null) {
      if (stopReason is not null) throw Invalid("duplicate Anthropic stop reason");
      stopReason = RequiredString(delta, "stop_reason");
    }
    return UsageUpdate(data);
  }

  private ChatResponseUpdate? StopMessage() {
    if (active is not null || stopReason is null) throw Invalid("Anthropic message_stop without a complete message_delta");
    stopped = true;
    return null;
  }

  private ChatResponseUpdate? UsageUpdate(JsonElement envelope) => Display(usage.Append(envelope) is { } details
      ? new UsageContent(details) : null);

  private ChatResponseUpdate? Display(AIContent? content) => content is null ? null : Update([content]);

  private ChatResponseUpdate Update(IList<AIContent> contents) => new(ChatRole.Assistant, contents) {
    ResponseId = messageId,
    MessageId = messageId,
    ModelId = model
  };

  internal ChatResponseUpdate Complete() {
    if (!stopped || stopReason is not ("end_turn" or "stop_sequence" or "tool_use"))
      throw new ProviderOperationException(ProviderFailure.IncompleteResponse);
    if ((stopReason == "tool_use") != (calls.Count > 0) || requireCall && calls.Count == 0)
      throw Invalid("Anthropic stop reason does not match tool calls");
    var update = Update(calls.Cast<AIContent>().ToList());
    update.FinishReason = calls.Count > 0 ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop;
    LlmProviderContext.SetResponse(update, LlmProviderContext.CaptureAnthropic(scope, blocks));
    drafts?.Reconcile(calls);
    return update;
  }
}
