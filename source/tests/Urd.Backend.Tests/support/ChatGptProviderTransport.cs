namespace Urd.Backend.Tests.Support;

// Composes existing socket-free fixtures; any unexpected destination fails closed.
internal sealed class ChatGptProviderTransport(
    FakeOpenAIIdentity identity, ScriptedOpenAIHandler catalog, ScriptedOpenAIHandler responses) : HttpMessageHandler {
  private readonly HttpMessageInvoker auth = new(identity);
  private readonly HttpMessageInvoker models = new(catalog);
  private readonly HttpMessageInvoker inference = new(responses);

  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
    Uri uri = request.RequestUri!;
    if (uri.GetLeftPart(UriPartial.Authority) == FakeOpenAIIdentity.Issuer.GetLeftPart(UriPartial.Authority))
      return auth.SendAsync(request, cancellationToken);
    if (uri.GetLeftPart(UriPartial.Authority) != "https://api.openai.com")
      throw new InvalidOperationException("Unexpected fixture destination.");
    return uri.AbsolutePath switch {
      "/v1/models" => models.SendAsync(request, cancellationToken),
      "/v1/responses" => inference.SendAsync(request, cancellationToken),
      _ => throw new InvalidOperationException("Unexpected fixture operation.")
    };
  }

  protected override void Dispose(bool disposing) {
    if (disposing) {
      auth.Dispose();
      models.Dispose();
      inference.Dispose();
    }
    base.Dispose(disposing);
  }
}
