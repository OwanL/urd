using System.ClientModel.Primitives;

namespace Urd.Core.Providers;

// Scoped to one SDK stream. Response headers and native updates count as progress,
// even if neither produces visible assistant text.
internal sealed class InferenceStreamProgress(ProviderCancellation stop, TimeSpan timeout) : PipelinePolicy {
  private bool receivedHeaders;
  private bool receivedUpdate;

  internal CancellationToken Token => stop.Token;

  internal void BeginRead() => stop.SetDeadline(timeout);
  internal void EndRead() => stop.SetDeadline(Timeout.InfiniteTimeSpan);
  internal void RecordUpdate() => receivedUpdate = true;

  internal ProviderOperationException TimeoutFailure(bool cleanupFailed = false) {
    string stage = (receivedHeaders, receivedUpdate) switch {
      (_, true) => "the next provider stream event",
      (true, false) => "the first provider stream event",
      _ => "provider response headers"
    };
    string reason = $"Timed out waiting for {stage}.";
    if (cleanupFailed) reason += " Provider stream cleanup also failed.";
    return new(ProviderFailure.ProviderUnavailable, reason);
  }

  public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) {
    ProcessNext(message, pipeline, currentIndex);
    RecordHeaders();
  }

  public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline,
      int currentIndex) {
    await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    RecordHeaders();
  }

  private void RecordHeaders() {
    receivedHeaders = true;
    BeginRead();
  }
}
