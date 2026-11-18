using System.Text;

namespace Urd.Core.Tools;

// One document captured through the restricted browser transport; the body is decoded exactly once.
internal sealed record WebPageVisit(
  string FinalUrl, int? StatusCode, string ContentType, string Title,
  string? Html, byte[]? Body, string? BlockReason, bool TextSettled) {

  public WebReadKind Kind {
    get {
      if (Body is not null && Body.AsSpan(0, Math.Min(Body.Length, 5)).SequenceEqual("%PDF-"u8))
        return WebReadKind.Pdf;
      string mime = Mime(ContentType);
      if (mime.Length == 0 || mime.Contains("html"))
        return WebReadKind.Html;
      if (mime.Contains("json"))
        return WebReadKind.Json;
      if (mime.Contains("pdf"))
        return WebReadKind.Pdf;
      WebReadKind declared = mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? WebReadKind.PlainText : WebReadKind.Unsupported;
      return declared == WebReadKind.Unsupported && Html is null && Body is not null ? Sniffed(Body) : declared;
    }
  }

  // How the transport should capture the navigation response, decided before any body is read.
  internal enum WebFetchKind { HtmlDocument, TextBody, RawBody }

  internal static WebFetchKind Transport(string contentType) {
    string mime = Mime(contentType);
    if (mime.Length == 0 || mime.Contains("html"))
      return WebFetchKind.HtmlDocument;
    if (mime.Contains("json") || mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
      return WebFetchKind.TextBody;
    return WebFetchKind.RawBody;
  }

  internal static string Mime(string contentType) {
    string mime = contentType.Split(';')[0].Trim();
    return mime.Length == 0 ? "" : mime.ToLowerInvariant();
  }

  private static WebReadKind Sniffed(byte[] body) {
    if (body.AsSpan(0, Math.Min(body.Length, 5)).SequenceEqual("%PDF-"u8))
      return WebReadKind.Pdf;
    string probe = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 4096));
    if (probe.Contains('\0') || probe.Any(IsDisallowedControl))
      return WebReadKind.Unsupported;
    string trimmed = probe.Trim(' ', '\t', '\r', '\n', '\uFEFF');
    return trimmed.Length > 0 && trimmed[0] is '{' or '[' ? WebReadKind.Json : WebReadKind.PlainText;
  }

  private static bool IsDisallowedControl(char character) =>
    character is < ' ' and not ('\t' or '\n' or '\r' or '\u000C');
}

// The readable content type a captured page carries into the model result.
internal enum WebReadKind {
  Html, PlainText, Json, Pdf, Unsupported
}
