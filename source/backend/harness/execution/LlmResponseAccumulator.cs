using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Providers;

namespace Urd.Core.Execution;

// Keeps visible text separate from completed calls and opaque provider continuation state.
internal sealed class LlmResponseAccumulator(LlmResponseWriter writer) {
  private const int ToolCallLimit = 16;
  private readonly List<LlmToolCall> calls = [];
  private readonly HashSet<string> callIds = new(StringComparer.Ordinal);
  private ChatFinishReason? finishReason;
  private bool hasReasoning;

  internal IReadOnlyList<LlmToolCall> ToolCalls => calls.ToArray();
  internal UsageDetails? Usage { get; private set; }
  internal LlmProviderResponse? ProviderResponse { get; private set; }

  internal async Task AppendAsync(ChatResponseUpdate update) {
    if (ProviderResponse is not null)
      throw new InvalidDataException("The response contained updates after its provider context.");
    LlmProviderResponse? providerResponse = LlmProviderContext.GetResponse(update);
    if (providerResponse is not null && (update.FinishReason is null || finishReason is not null))
      throw new InvalidDataException("Provider context requires one final response update.");

    foreach (AIContent content in update.Contents) {
      if (content is UsageContent usage) {
        (Usage ??= new()).Add(usage.Details);
        continue;
      }
      if (content is TextReasoningContent) {
        hasReasoning = true;
        if (finishReason is not null)
          throw new InvalidDataException("The response contained reasoning after its finish reason.");
        continue;
      }

      if (content is TextContent text) {
        if (finishReason is not null && text.Text.Length != 0)
          throw new InvalidDataException("The response contained text after its finish reason.");

        await writer.AppendTextAsync(text.Text);
        continue;
      }

      if (content is not FunctionCallContent call || finishReason is not null)
        throw new InvalidDataException("The response contained unsupported content.");

      await AddCallAsync(call);
    }

    if (update.FinishReason is not { } reason)
      return;

    if (reason != ChatFinishReason.Stop && reason != ChatFinishReason.ToolCalls)
      throw new InvalidDataException("The response finished without a supported finish reason.");
    if (finishReason is not null && finishReason != reason)
      throw new InvalidDataException("The response contained conflicting finish reasons.");
    if ((reason == ChatFinishReason.ToolCalls) != (calls.Count != 0))
      throw new InvalidDataException("The finish reason did not match the response's tool calls.");

    if (providerResponse is not null)
      _ = LlmProviderContext.Restore(providerResponse);
    finishReason = reason;
    ProviderResponse = providerResponse;
  }

  internal Task FinishAsync() {
    if (finishReason is null)
      throw new InvalidDataException("The response ended without a supported finish reason.");
    if (hasReasoning && ProviderResponse is null)
      throw new InvalidDataException("The response contained reasoning without provider context.");

    return writer.FinishResponseAsync();
  }

  private async Task AddCallAsync(FunctionCallContent call) {
    if (string.IsNullOrWhiteSpace(call.CallId))
      throw new InvalidDataException("The response contained a tool call without an ID.");
    if (calls.Count == ToolCallLimit || !callIds.Add(call.CallId))
      throw new InvalidDataException("The response contained too many tool calls or duplicate call IDs.");

    await writer.ObserveToolAsync(call.CallId, string.IsNullOrWhiteSpace(call.Name) ? null : call.Name);
    if (call.InformationalOnly || call.Exception is not null || string.IsNullOrWhiteSpace(call.Name))
      throw new InvalidDataException("The response contained an incomplete or informational tool call.");

    JsonElement arguments = FreezeJson(call.Arguments ?? new Dictionary<string, object?>());
    if (arguments.ValueKind != JsonValueKind.Object)
      throw new InvalidDataException("Tool arguments must be a JSON object.");

    calls.Add(new(call.CallId, call.Name, arguments));
  }

  // Freeze borrowed values before they are persisted or dispatched.
  // Result serialization uses this too, outside the invocation-error catch.
  internal static JsonElement FreezeJson(object? value) {
    using var bytes = new MemoryStream();
    JsonSerializer.Serialize(bytes, value, value?.GetType() ?? typeof(object));
    using JsonDocument document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, (int)bytes.Length));
    return document.RootElement.Clone();
  }
}
