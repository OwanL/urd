using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Providers;

namespace Urd.Core.Execution;

// Keeps visible text separate from completed calls and opaque provider continuation state.
internal sealed class LlmResponseAccumulator(LlmResponseWriter writer, LlmResponseWriter? reasoningWriter) {
  private const int ToolCallLimit = 16;
  private readonly List<LlmToolCall> calls = [];
  private readonly HashSet<string> callIds = new(StringComparer.Ordinal);
  private ChatFinishReason? finishReason;
  private bool hasReasoning;

  internal IReadOnlyList<LlmToolCall> ToolCalls => calls.ToArray();
  internal UsageDetails? Usage { get; private set; }
  internal LlmProviderResponse? ProviderResponse { get; private set; }

  internal async Task AddUpdateAsync(ChatResponseUpdate update) {
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
      if (content is TextReasoningContent reasoning) {
        hasReasoning = true;
        if (finishReason is not null)
          throw new InvalidDataException("The response contained reasoning after its finish reason.");
        // The preview captures only the provider's reasoning text; opaque continuation state stays excluded.
        if (reasoningWriter is not null && reasoning.Text is { Length: > 0 } reasoningText)
          await reasoningWriter.AppendTextAsync(reasoningText);
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

  internal async Task FinishAsync() {
    if (finishReason is null)
      throw new InvalidDataException("The response ended without a supported finish reason.");
    if (hasReasoning && ProviderResponse is null)
      throw new InvalidDataException("The response contained reasoning without provider context.");

    if (reasoningWriter is not null)
      await reasoningWriter.FinishResponseAsync();
    await writer.FinishResponseAsync();
  }

  private async Task AddCallAsync(FunctionCallContent call) {
    if (string.IsNullOrWhiteSpace(call.CallId))
      throw new InvalidDataException("The response contained a tool call without an ID.");
    if (calls.Count == ToolCallLimit || !callIds.Add(call.CallId))
      throw new InvalidDataException("The response contained too many tool calls or duplicate call IDs.");

    await writer.RecordToolCallAsync(call.CallId, string.IsNullOrWhiteSpace(call.Name) ? null : call.Name);
    if (call.InformationalOnly || call.Exception is not null || string.IsNullOrWhiteSpace(call.Name))
      throw new InvalidDataException("The response contained an incomplete or informational tool call.");

    // Freeze borrowed arguments before they are persisted or dispatched.
    JsonElement arguments = JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, object?>());
    if (arguments.ValueKind != JsonValueKind.Object)
      throw new InvalidDataException("Tool arguments must be a JSON object.");

    calls.Add(new(call.CallId, call.Name, arguments));
  }
}
