using System.ClientModel.Primitives;

namespace Urd.Core.Providers;

// Scoped to one SDK stream. Response headers and native updates count as progress,
// even if neither produces visible assistant text.
internal sealed class InferenceStreamProgress(ProviderCancellation stop, TimeSpan timeout, Guid? sessionId = null) : PipelinePolicy {
  private bool receivedHeaders;
  private bool receivedUpdate;

  internal CancellationToken Token => stop.Token;
  internal string Transport { get; set; } = "https";
  internal string Stage => (receivedHeaders, receivedUpdate) switch {
    (_, true) => "stream",
    (true, false) => "first-stream-event",
    _ => "response-headers"
  };

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
    AddSessionHeader(message);
    ProcessNext(message, pipeline, currentIndex);
    RecordHeaders();
  }

  public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline,
      int currentIndex) {
    AddSessionHeader(message);
    await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    RecordHeaders();
  }

  private void AddSessionHeader(PipelineMessage message) {
    if (sessionId is { } id)
      message.Request.Headers.Set("session-id", id.ToString("D"));
  }

  private void RecordHeaders() {
    receivedHeaders = true;
    BeginRead();
  }
}
