using System.ClientModel.Primitives;

namespace Urd.Core.Providers;

// Scoped to one SDK stream. Response headers and native updates count as progress,
// even if neither produces visible assistant text.
internal sealed class InferenceStreamProgress(ProviderCancellation stop, TimeSpan timeout, Guid? sessionId = null) : PipelinePolicy {
  private int? httpStatus;
  private bool receivedUpdate;

  internal CancellationToken Token => stop.Token;
  internal int? PreStreamHttpStatus => !receivedUpdate && Transport is "http" or "https" ? httpStatus : null;
  internal string Transport { get; set; } = "https";
  internal ProviderStreamTerminal? WireTerminal { get; set; }
  internal ProviderStreamTerminalKind? SdkTerminal { get; set; }
  internal string Stage => (httpStatus is not null, receivedUpdate) switch {
    (_, true) => "stream",
    (true, false) => "first-stream-event",
    _ => "response-headers"
  };

  internal void BeginRead() => stop.SetDeadline(timeout);
  internal void EndRead() => stop.SetDeadline(Timeout.InfiniteTimeSpan);
  internal void RecordUpdate() => receivedUpdate = true;

  internal ProviderOperationException TimeoutFailure(bool cleanupFailed = false) {
    string stage = (httpStatus is not null, receivedUpdate) switch {
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
    RecordHeaders(message.Response);
  }

  public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline,
      int currentIndex) {
    AddSessionHeader(message);
    await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    RecordHeaders(message.Response);
  }

  private void AddSessionHeader(PipelineMessage message) {
    if (sessionId is { } id)
      message.Request.Headers.Set("session-id", id.ToString("D"));
  }

  private void RecordHeaders(PipelineResponse? response) {
    httpStatus = response?.Status;
    BeginRead();
  }
}
