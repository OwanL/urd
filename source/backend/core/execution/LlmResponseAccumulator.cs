using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Urd.Core.Execution;

// Accepts complete fake-model content only; provider-native deltas are not supported.
internal sealed class LlmResponseAccumulator(LlmResponseWriter writer) {
  private const int ToolCallLimit = 16;
  private const int JsonByteLimit = 16 * 1024;
  private readonly List<LlmToolCall> calls = [];
  private readonly HashSet<string> callIds = new(StringComparer.Ordinal);
  private ChatFinishReason? finishReason;

  internal IReadOnlyList<LlmToolCall> ToolCalls => calls.ToArray();

  internal async Task AppendAsync(ChatResponseUpdate update) {
    foreach (AIContent content in update.Contents) {
      if (content is UsageContent)
        continue;

      if (content is TextContent text) {
        if (finishReason is not null && text.Text.Length != 0)
          throw new InvalidDataException("The response contained text after its finish reason.");

        await writer.AppendTextAsync(text.Text);
        continue;
      }

      if (content is not FunctionCallContent call || finishReason is not null)
        throw new InvalidDataException("The response contained unsupported content.");

      AddCall(call);
    }

    if (update.FinishReason is not { } reason)
      return;

    if (reason != ChatFinishReason.Stop && reason != ChatFinishReason.ToolCalls)
      throw new InvalidDataException("The response finished without a supported finish reason.");
    if (finishReason is not null && finishReason != reason)
      throw new InvalidDataException("The response contained conflicting finish reasons.");
    if ((reason == ChatFinishReason.ToolCalls) != (calls.Count != 0))
      throw new InvalidDataException("The finish reason did not match the response's tool calls.");

    finishReason = reason;
  }

  internal Task FinishAsync() {
    if (finishReason is null)
      throw new InvalidDataException("The response ended without a supported finish reason.");

    return writer.FinishResponseAsync();
  }

  private void AddCall(FunctionCallContent call) {
    if (call.InformationalOnly || call.Exception is not null ||
        string.IsNullOrWhiteSpace(call.CallId) || string.IsNullOrWhiteSpace(call.Name))
      throw new InvalidDataException("The response contained an incomplete or informational tool call.");
    if (calls.Count == ToolCallLimit || !callIds.Add(call.CallId))
      throw new InvalidDataException("The response contained too many tool calls or duplicate call IDs.");

    JsonElement arguments = FreezeJson(call.Arguments ?? new Dictionary<string, object?>());
    if (arguments.ValueKind != JsonValueKind.Object)
      throw new InvalidDataException("Tool arguments must be a JSON object.");

    calls.Add(new(call.CallId, call.Name, arguments));
  }

  // Freeze borrowed values and reject oversized JSON before it can be persisted or dispatched.
  // Result serialization uses this too, outside the invocation-error catch.
  internal static JsonElement FreezeJson(object? value) {
    using var bytes = new BoundedJsonStream();
    JsonSerializer.Serialize(bytes, value, value?.GetType() ?? typeof(object));
    using JsonDocument document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, (int)bytes.Length));
    return document.RootElement.Clone();
  }

  private sealed class BoundedJsonStream() : MemoryStream(JsonByteLimit) {
    public override void Write(byte[] buffer, int offset, int count) {
      CheckLength(count);
      base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
      CheckLength(buffer.Length);
      // MemoryStream's span overload delegates back to Write on subclasses.
      // Copy directly into its fixed-capacity buffer instead.
      int offset = (int)Length;
      SetLength(Length + buffer.Length);
      buffer.CopyTo(GetBuffer().AsSpan(offset));
      Position = Length;
    }

    private void CheckLength(int count) {
      if (Length + count > JsonByteLimit)
        throw new InvalidDataException("Tool JSON exceeds the 16 KiB limit.");
    }
  }
}
