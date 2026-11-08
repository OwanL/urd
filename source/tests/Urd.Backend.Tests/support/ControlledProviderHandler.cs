using System.Collections.Concurrent;

namespace Urd.Backend.Tests.Support;

// A socket-free transport that lets lifecycle tests hold responses and cancellation cleanup.
internal sealed class ControlledProviderHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler {
  private readonly ConcurrentQueue<ScriptedOpenAIRequest> requests = new();
  private int disposeCalls;

  internal IReadOnlyList<ScriptedOpenAIRequest> Requests => requests.ToArray();
  internal int DisposeCalls => Volatile.Read(ref disposeCalls);

  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
    string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
    requests.Enqueue(new(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
    return await respond(request, cancellationToken);
  }

  protected override void Dispose(bool disposing) {
    if (disposing)
      Interlocked.Increment(ref disposeCalls);
    base.Dispose(disposing);
  }
}
