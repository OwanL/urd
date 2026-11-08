using System.Net;
using System.Text;

namespace Urd.Backend.Tests.Support;

// Socket-free HTTP boundary: production SDK serialization, transport, streaming and cleanup remain real.
internal sealed class ScriptedOpenAIHandler(string response, string contentType = "text/event-stream",
    HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler {
  private int streamDisposals;
  internal List<ScriptedOpenAIRequest> Requests { get; } = [];
  internal bool HoldAtEnd { get; init; }
  internal bool FailAtEnd { get; init; }
  internal int StreamDisposals => Volatile.Read(ref streamDisposals);
  internal TaskCompletionSource ReachedEnd { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
    string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
    Requests.Add(new(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
    var stream = new ScriptStream(Encoding.UTF8.GetBytes(response), this);
    return new HttpResponseMessage(status) {
      RequestMessage = request,
      Content = new StreamContent(stream) { Headers = { ContentType = new(contentType) } }
    };
  }

  private sealed class ScriptStream(byte[] bytes, ScriptedOpenAIHandler owner) : MemoryStream(bytes) {
    private bool disposed;

    // System.ClientModel's ReadTimeoutStream uses the legacy overload, which MemoryStream
    // otherwise handles without entering our Memory<byte> override and its scripted tail.
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
      int count = await base.ReadAsync(buffer, cancellationToken);
      if (count != 0) return count;
      owner.ReachedEnd.TrySetResult();
      if (owner.HoldAtEnd) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      if (owner.FailAtEnd) throw new IOException("Synthetic private provider transport details");
      return 0;
    }

    protected override void Dispose(bool disposing) {
      if (!disposed) {
        disposed = true;
        Interlocked.Increment(ref owner.streamDisposals);
      }
      base.Dispose(disposing);
    }
  }
}

internal sealed record ScriptedOpenAIRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body);
