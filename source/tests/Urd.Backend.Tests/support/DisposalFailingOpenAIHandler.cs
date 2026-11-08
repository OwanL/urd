using System.Net;
using System.Text;

namespace Urd.Backend.Tests.Support;

// A positive native terminal is not success if ownership cleanup itself fails.
internal sealed class DisposalFailingOpenAIHandler(string response) : HttpMessageHandler {
  internal int Disposals { get; private set; }

  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
      Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
        RequestMessage = request,
        Content = new StreamContent(new DisposalFailingStream(Encoding.UTF8.GetBytes(response), this)) {
          Headers = { ContentType = new("text/event-stream") }
        }
      });

  private sealed class DisposalFailingStream(byte[] bytes, DisposalFailingOpenAIHandler owner) : MemoryStream(bytes) {
    private bool disposed;

    protected override void Dispose(bool disposing) {
      base.Dispose(disposing);
      if (disposed) return;
      disposed = true;
      owner.Disposals++;
      throw new IOException("Synthetic private cleanup details");
    }
  }
}
