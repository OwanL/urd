using System.Buffers;
using System.ClientModel.Primitives;
using System.Text.Json;
using Urd.Core.Execution;
using Urd.Core.Usage;

namespace Urd.Core.Providers;

// Observe the serialized full-replay request without retaining the body or affecting inference.
internal sealed class PromptCacheCapture(PromptCacheMonitor monitor, ContextStamp stamp,
    ProviderState provider, string model) : PipelinePolicy {
  private const int MaximumBodyBytes = 1024 * 1024;
  private PromptCacheMonitor.Attempt? attempt;

  public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) {
    Capture(message);
    ProcessNext(message, pipeline, currentIndex);
  }

  public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) {
    Capture(message);
    return ProcessNextAsync(message, pipeline, currentIndex);
  }

  internal void Complete(long? inputTokens, long? cachedInputTokens, bool successful, string? reportedModel) {
    if (attempt is null)
      monitor.Reset();
    else
      monitor.Complete(attempt, inputTokens, cachedInputTokens, successful, reportedModel);
  }

  private void Capture(PipelineMessage message) {
    if (!PromptCacheMonitor.Supports(provider) ||
        message.Request.Content is not { } content || !content.TryComputeLength(out long length) ||
        length is <= 0 or > MaximumBodyBytes)
      return;

    // The OpenAI SDK supplies replayable BinaryContent; the fixed-size view bounds the extra copy.
    int size = (int)length;
    byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
    try {
      using var body = new MemoryStream(buffer, 0, size, writable: true);
      content.WriteTo(body, message.CancellationToken);
      if (body.Position != size) return;
      using var json = JsonDocument.Parse(buffer.AsMemory(0, size));
      attempt = monitor.Begin(provider, model, stamp, json.RootElement);
    } catch (Exception failure) when (failure is IOException or JsonException or ArgumentException or InvalidOperationException or NotSupportedException) {
      // Diagnostics are optional. Let the SDK transport handle the original request as usual.
    } finally {
      ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }
  }
}
