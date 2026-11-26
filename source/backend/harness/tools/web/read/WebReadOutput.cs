using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Urd.Core.Tools;

// Turns one captured page into the bounded model result; large extracted text spills to a temp file.
internal static class WebReadOutput {
  private static readonly JsonSerializerOptions indented = new() { WriteIndented = true };
  private static readonly string[] challengeMarkers =
    ["cf-chl-", "challenge-platform", "browser-verification"];

  public static async Task<ToolOutput> Present(WebPageVisit visit, string requestedUrl, CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    if (visit.BlockReason is string transport)
      return Settled(visit, requestedUrl, WebReadStatus.Blocked, transport);
    if (visit.StatusCode is not int code)
      return Settled(visit, requestedUrl, WebReadStatus.Failed, "The navigation produced no document response.");
    if (code is 401 or 403 or 407)
      return Settled(visit, requestedUrl, WebReadStatus.Blocked, $"The server returned HTTP {code}.");
    if (code == 429)
      return Settled(visit, requestedUrl, WebReadStatus.RateLimited, "The server answered HTTP 429; a later read may succeed.");
    if (code >= 400)
      return Settled(visit, requestedUrl, WebReadStatus.Failed, $"The server returned HTTP {code}.");
    if (Challenge(visit))
      return Settled(visit, requestedUrl, WebReadStatus.Blocked, "The page presents a browser challenge; it is reported, not bypassed.");
    return visit.Kind switch {
      WebReadKind.Html => await Html(visit, requestedUrl, cancellation).ConfigureAwait(false),
      WebReadKind.Json => await Text(visit, requestedUrl, WebReadKind.Json, Pretty(visit.Body), cancellation).ConfigureAwait(false),
      WebReadKind.PlainText => await Text(visit, requestedUrl, WebReadKind.PlainText, BodyText(visit), cancellation).ConfigureAwait(false),
      WebReadKind.Pdf => await Pdf(visit, requestedUrl, cancellation).ConfigureAwait(false),
      _ => Settled(visit, requestedUrl, WebReadStatus.Unsupported, $"The content type {WebPageVisit.Mime(visit.ContentType)} is not supported.")
    };
  }

  private static async Task<ToolOutput> Html(WebPageVisit visit, string requestedUrl, CancellationToken cancellation) {
    (string markdown, bool truncated) = HtmlBound(visit);
    if (markdown.Length == 0)
      return Settled(visit, requestedUrl, WebReadStatus.Empty, "The page returned no readable text.");
    return await Deliverable(visit, requestedUrl, WebReadKind.Html, markdown, truncated, cancellation).ConfigureAwait(false);
  }

  private static (string Markdown, bool Truncated) HtmlBound(WebPageVisit visit) {
    string html = visit.Html ?? BodyText(visit);
    bool truncated = false;
    if (Encoding.UTF8.GetByteCount(html) > WebReadLimits.MaxInputBytes) {
      html = Slice(html, (int)WebReadLimits.MaxInputBytes);
      truncated = true;
    }
    WebPageExtraction extraction = WebPageExtractor.Extract(html, visit.FinalUrl);
    return (extraction.Markdown, truncated || extraction.Truncated);
  }

  private static async Task<ToolOutput> Pdf(WebPageVisit visit, string requestedUrl, CancellationToken cancellation) {
    (string text, bool hasTextLayer, bool truncated) = await PdfText.ReadAsync(visit.Body ?? [], cancellation).ConfigureAwait(false);
    if (!hasTextLayer)
      return Settled(visit, requestedUrl, WebReadStatus.Unsupported, "The PDF has no text layer; scanned PDFs are unsupported.");
    return await Text(visit, requestedUrl, WebReadKind.Pdf, text, cancellation, truncated).ConfigureAwait(false);
  }

  private static async Task<ToolOutput> Text(WebPageVisit visit, string requestedUrl, WebReadKind kind,
      string text, CancellationToken cancellation, bool extractionTruncated = false) {
    (text, bool truncated) = Bound(text.Trim());
    if (text.Length == 0)
      return Settled(visit, requestedUrl, WebReadStatus.Empty, "The page returned no readable text.");
    return await Deliverable(visit, requestedUrl, kind, text, extractionTruncated || truncated, cancellation).ConfigureAwait(false);
  }

  // Small reads stay inline; everything larger spills the full text to a disposable temp file and
  // returns a bounded preview, so the model can search or read the file without refetching.
  private static async Task<ToolOutput> Deliverable(WebPageVisit visit, string requestedUrl, WebReadKind kind,
      string text, bool extractionTruncated, CancellationToken cancellation) {
    byte[] payload = Encoding.UTF8.GetBytes(text);
    string header = $"web_read completed: {visit.FinalUrl}" +
      (visit.Title.Length == 0 ? "" : $"\nTitle: {visit.Title}");
    if (payload.Length <= WebReadLimits.InlineTextBytes) {
      string inline = $"{header}\n\n{text}\n{Notices(visit, extractionTruncated)}".TrimEnd();
      return new ToolOutput(inline, [], Details(visit, requestedUrl, WebKindName(kind), WebReadStatus.Completed,
        payload.Length, extractionTruncated, false, null, 0));
    }
    string path = Path.Combine(Path.GetTempPath(), $"urd-web-read-{Guid.NewGuid():N}{SpillExtension(kind)}");
    try {
      await File.WriteAllTextAsync(path, text, cancellation).ConfigureAwait(false);
    } catch {
      File.Delete(path);
      throw;
    }
    string preview = Slice(text, WebReadLimits.PreviewTextBytes);
    string body = $"{header}\n\n" +
      $"[Full text: {path} ({Bytes(payload.Length)} bytes). Source: {requestedUrl}] Preview below; search or read the file for more.\n" +
      preview;
    string spilled = $"{body}\n{Notices(visit, extractionTruncated)}".TrimEnd();
    int previewEnd = Math.Min(body.Length, spilled.Length);
    return new ToolOutput(spilled, [], Details(visit, requestedUrl, WebKindName(kind), WebReadStatus.Completed,
      payload.Length, extractionTruncated, true, path, payload.Length),
      Spill: new ToolOutputSpill(path, PreviewEnd: previewEnd, Suffix: spilled[previewEnd..]));
  }

  private static ToolOutput Settled(WebPageVisit visit, string requestedUrl, WebReadStatus status, string reason) {
    string text = $"web_read {Label(status)}: {visit.FinalUrl}\n{reason}";
    return new ToolOutput(text, [], Details(visit, requestedUrl, WebKindName(visit.Kind), status, 0, false, false, null, 0),
      IsError: status is not (WebReadStatus.Completed or WebReadStatus.Empty));
  }

  private static string Notices(WebPageVisit visit, bool extractionTruncated) {
    var builder = new StringBuilder();
    if (extractionTruncated)
      builder.Append(CultureInfo.InvariantCulture, $"[Extraction stopped at the {WebReadLimits.MaxInputBytes / (1024 * 1024)}MiB document limit.]\n");
    if (!visit.TextSettled)
      builder.Append("[Page content kept changing; the text may be incomplete.]");
    return builder.ToString();
  }

  private static bool Challenge(WebPageVisit visit) {
    if (visit.Html is null && visit.Kind != WebReadKind.Html)
      return false;
    string title = visit.Title.Trim();
    if (title.Equals("Just a moment...", StringComparison.OrdinalIgnoreCase) ||
        title.StartsWith("Attention Required!", StringComparison.OrdinalIgnoreCase))
      return true;
    string html = visit.Html ?? BodyText(visit);
    string probe = html[..Math.Min(html.Length, 100_000)];
    return challengeMarkers.Any(marker => probe.Contains(marker, StringComparison.OrdinalIgnoreCase)) &&
      (probe.Contains("checking your browser", StringComparison.OrdinalIgnoreCase) ||
       probe.Contains("verify you are human", StringComparison.OrdinalIgnoreCase));
  }

  private static string BodyText(WebPageVisit visit) => Encoding.UTF8.GetString(visit.Body ?? []);

  private static string Pretty(byte[]? body) {
    string raw = Encoding.UTF8.GetString(body ?? []);
    try {
      using JsonDocument document = JsonDocument.Parse(raw);
      // JsonSerializer uses the platform's newline; normalize so reads look the same on every host.
      return JsonSerializer.Serialize(document.RootElement, indented).Replace("\r\n", "\n");
    } catch (JsonException) {
      return raw;
    }
  }

  private static (string Text, bool Truncated) Bound(string text) {
    if (Encoding.UTF8.GetByteCount(text) <= WebReadLimits.MaxInputBytes)
      return (text, false);
    return (Slice(text, (int)WebReadLimits.MaxInputBytes), true);
  }

  internal static string Slice(string text, int byteLimit) {
    if (text.Length == 0)
      return "";
    int count = Math.Min(text.Length, byteLimit);
    long bytes = Encoding.UTF8.GetByteCount(text, 0, count);
    while (bytes > byteLimit) {
      bytes -= Encoding.UTF8.GetByteCount(text, count - 1, 1);
      count--;
    }
    if (count > 0 && count < text.Length && char.IsLowSurrogate(text[count]) && char.IsHighSurrogate(text[count - 1]))
      count--;
    return count <= 0 ? "" : text[..count];
  }

  private static string SpillExtension(WebReadKind kind) => kind switch {
    WebReadKind.Html => ".md",
    WebReadKind.Json => ".json",
    _ => ".txt"
  };

  private static string WebKindName(WebReadKind kind) => kind switch {
    WebReadKind.Html => "html",
    WebReadKind.PlainText => "text",
    WebReadKind.Json => "json",
    WebReadKind.Pdf => "pdf",
    _ => "unknown"
  };

  private static string Label(WebReadStatus status) => status switch {
    WebReadStatus.RateLimited => "rate-limited",
    _ => status.ToString().ToLowerInvariant()
  };

  private static string Bytes(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

  private static JsonElement? Details(WebPageVisit visit, string requestedUrl, string kind, WebReadStatus status,
      int textBytes, bool extractionTruncated, bool previewOmitted, string? spillPath, int spillBytes) =>
    JsonSerializer.SerializeToElement(new {
      url = requestedUrl, finalUrl = visit.FinalUrl, title = visit.Title, statusCode = visit.StatusCode,
      kind, status = Label(status), textBytes, extractionTruncated, previewOmitted, spillPath, spillBytes,
      settled = visit.TextSettled
    });

  // One web read's settlement, distinct from its extracted content bounds.
  internal enum WebReadStatus {
    Completed, Empty, Blocked, RateLimited, Failed, Unsupported
  }
}
