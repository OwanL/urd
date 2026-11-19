using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Urd.Core.Usage;

namespace Urd.Core.Context;

// One send attempt. Prompt accounting runs alongside the stream; output is counted in bounded batches.
internal sealed class RequestContextEstimate(Func<RequestContextEstimate, CancellationToken, Task> publish) : IAsyncDisposable {
  internal const string PropertyName = "urd.request_context_estimate";
  internal const string ToolArgumentDeltaProperty = "urd.context_tool_arguments_delta";
  private const int MaxBatchCharacters = 16_384;
  private const int PromptSampleBudget = 524_288;
  private static readonly TimeSpan OutputInterval = TimeSpan.FromMilliseconds(500);
  private readonly CancellationTokenSource lifetime = new();
  private readonly StringBuilder output = new();
  private Task? preparation;
  private long promptTokens = -1;
  private long outputTokens;
  private long outputLength;
  private long lastOutputCount;

  internal static bool SupportsModel(string model) => model == "gpt-6.2-sol" || UsageTokenCounter.UsesO200k(model);

  internal Task? FlushDelay { get; private set; }

  internal long? UsedTokens => Volatile.Read(ref promptTokens) is >= 0 and var input
      ? input + Volatile.Read(ref outputTokens) : null;

  internal void Prepare(IEnumerable<ChatMessage> messages, ChatOptions options) {
    preparation = Task.Run(async () => {
      try {
        if (TryCountPrompt(messages, options, lifetime.Token) is not long tokens) return;

        Volatile.Write(ref promptTokens, tokens);
        await publish(this, lifetime.Token);
      } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) {
        // The owning attempt stopped or completed before the estimate was ready.
      }
    }, CancellationToken.None); // Always enter the owned operation; it handles cancellation itself.
  }

  internal async Task ObserveAsync(ChatResponseUpdate update) {
    foreach (AIContent content in update.Contents) {
      string? text = content switch {
        TextReasoningContent reasoning => reasoning.Text,
        TextContent visible => visible.Text,
        _ => null
      };
      AppendOutput(text);
    }

    if (update.AdditionalProperties?.TryGetValue(ToolArgumentDeltaProperty, out object? delta) == true && delta is BinaryData data)
      AppendOutput(data);
    if (outputLength == 0 || FlushDelay is not null) return;

    TimeSpan remaining = lastOutputCount == 0 ? TimeSpan.Zero : OutputInterval - Stopwatch.GetElapsedTime(lastOutputCount);
    if (remaining <= TimeSpan.Zero) await FlushAsync();
    else FlushDelay = Task.Delay(remaining, lifetime.Token);
  }

  private void AppendOutput(string? text) {
    if (string.IsNullOrEmpty(text)) return;
    output.Append(text.AsSpan(0, Math.Min(text.Length, MaxBatchCharacters - output.Length)));
    outputLength += text.Length;
  }

  private void AppendOutput(BinaryData data) {
    ReadOnlySpan<byte> bytes = data.ToMemory().Span;
    int prefixLength = Math.Min(bytes.Length, MaxBatchCharacters - output.Length);
    string prefix = Encoding.UTF8.GetString(bytes[..prefixLength]);
    output.Append(prefix);
    outputLength += prefixLength == bytes.Length ? prefix.Length : Encoding.UTF8.GetCharCount(bytes);
  }

  internal async Task FlushAsync() {
    FlushDelay = null;
    bool canCount = UsedTokens is not null;
    if (!canCount && preparation is { IsCompleted: false } pending) {
      // Do not initialize the tokenizer or wait for prompt accounting on the response read path.
      FlushDelay = pending;
      return;
    }

    long? tokens = canCount ? Estimate(output.ToString(), outputLength) : null;
    output.Clear();
    outputLength = 0;
    lastOutputCount = Stopwatch.GetTimestamp();
    if (tokens is > 0) {
      Interlocked.Add(ref outputTokens, tokens.Value);
      if (UsedTokens is not null)
        await publish(this, lifetime.Token);
    }
  }

  public async ValueTask DisposeAsync() {
    await lifetime.CancelAsync();
    try {
      if (preparation is not null) await preparation;
    } finally {
      lifetime.Dispose();
    }
  }

  private static long? TryCountPrompt(IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken cancellation) {
    try {
      cancellation.ThrowIfCancellationRequested();
      return O200kTokens.Instance is null ? null : CountPrompt(messages, options, cancellation);
    } catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or NotSupportedException) {
      // Estimation is optional; an unsupported representation must not interrupt the send.
      return null;
    }
  }

  private static long? CountPrompt(IEnumerable<ChatMessage> messages, ChatOptions options, CancellationToken cancellation) {
    var count = new PromptCount();
    count.Add(options.Instructions);
    // Count the changing suffix before using the shared density for very long histories.
    foreach (ChatMessage message in messages.Reverse()) {
      cancellation.ThrowIfCancellationRequested();
      count.Tokens += 4;
      count.Add(message.Role.ToString());
      count.Add(message.AuthorName);
      if (message.Contents.Any(content => !count.AddContent(content))) return null;
    }
    foreach (AITool tool in options.Tools ?? []) {
      cancellation.ThrowIfCancellationRequested();
      if (tool is not AIFunctionDeclaration function) return null;
      count.Tokens += 8;
      count.Add(function.Name);
      count.Add(function.Description);
      count.Add(function.JsonSchema.GetRawText());
    }
    return count.Tokens;
  }

  private static long? Estimate(string text, long length) {
    if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
      text = text[..^1];
    return O200kTokens.Estimate(text, length);
  }

  private sealed class PromptCount {
    private BoundedJsonBuffer? json;
    private int sampledCharacters;
    private long sampledTokens;
    internal long Tokens { get; set; }

    internal void Add(string? text) {
      if (string.IsNullOrEmpty(text)) return;
      int length = Math.Min(text.Length, Math.Min(MaxBatchCharacters, PromptSampleBudget - sampledCharacters));
      if (length > 0 && Sample(text, length) is long tokens) {
        sampledCharacters += length;
        sampledTokens += tokens;
        Tokens += Scale(tokens, text.Length, length);
      } else if (sampledCharacters > 0) {
        Tokens += Scale(sampledTokens, text.Length, sampledCharacters);
      }
    }

    internal bool AddContent(AIContent content) {
      switch (content) {
        case TextReasoningContent reasoning:
          Add(reasoning.Text); // Ciphertext length isn't a reasoning-token count.
          break;
        case TextContent text:
          Add(text.Text);
          break;
        case FunctionCallContent call:
          Add(call.Name);
          AddJson(call.Arguments);
          break;
        case FunctionResultContent result:
          return AddResult(result.Result);
        default:
          return false; // No credible estimate for an image or unknown modality.
      }
      return true;
    }

    private bool AddResult(object? result) {
      switch (result) {
        case null: return true;
        case string text: Add(text); return true;
        case AIContent content: return AddContent(content);
        case IEnumerable<AIContent> contents: return contents.All(AddContent);
        default: AddJson(result); return true;
      }
    }

    private void AddJson<T>(T value) {
      json ??= new();
      json.Clear();
      using var writer = new Utf8JsonWriter(json);
      JsonSerializer.Serialize(writer, value);
      writer.Flush();
      Add(Encoding.UTF8.GetString(json.WrittenSpan));
    }

    private static long? Sample(string text, int length) {
      if (length == text.Length || length < 512) return Estimate(text[..length], length);

      // One prefix can be all boilerplate in a large tool result. Sample both ends.
      int first = length / 2;
      int tail = length - first;
      long? a = Estimate(text[..first], first);
      long? b = Estimate(text[^tail..], tail);
      return a + b;
    }

    private static long Scale(long tokens, int length, int sample) => (long)Math.Ceiling(tokens * (double)length / sample);
  }

  private sealed class BoundedJsonBuffer : IBufferWriter<byte> {
    private const int MaximumBytes = 128 * 1024;
    private readonly ArrayBufferWriter<byte> buffer = new();
    internal ReadOnlySpan<byte> WrittenSpan => buffer.WrittenSpan;
    internal void Clear() => buffer.ResetWrittenCount();
    public void Advance(int count) => buffer.Advance(count);
    public Memory<byte> GetMemory(int sizeHint = 0) {
      int remaining = MaximumBytes - buffer.WrittenCount;
      if (remaining < Math.Max(sizeHint, 1))
        throw new NotSupportedException("Structured content exceeds the live estimate budget.");
      Memory<byte> memory = buffer.GetMemory(sizeHint);
      return memory[..Math.Min(memory.Length, remaining)];
    }
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
  }
}
