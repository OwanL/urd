using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Urd.Core.Tools;

internal static class WebBrowserProxyProtocol {
  private static readonly HashSet<string> ForwardedRequestHeaders = new(StringComparer.OrdinalIgnoreCase) {
    "accept", "accept-language", "cache-control", "cookie", "if-modified-since", "if-none-match",
    "origin", "range", "referer", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
    "sec-fetch-dest", "sec-fetch-mode", "sec-fetch-site", "sec-fetch-user", "user-agent"
  };
  private static readonly HashSet<string> OmittedResponseHeaders = new(StringComparer.OrdinalIgnoreCase) {
    "alt-svc", "connection", "content-length", "keep-alive", "proxy-authenticate", "proxy-authorization",
    "proxy-connection", "te", "trailer", "transfer-encoding", "upgrade", "www-authenticate", "x-urd-web-blocked"
  };

  public static string AddressOf(EndPoint endpoint) => new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(),
      ((IPEndPoint)endpoint).Port).Uri.AbsoluteUri;

  public static async Task<WebBrowserProxyRequest?> ReadRequestAsync(Stream stream, CancellationToken cancellation) {
    var buffer = new byte[32 * 1024];
    int length = 0;
    while (length < buffer.Length) {
      int read = await stream.ReadAsync(buffer.AsMemory(length), cancellation);
      if (read == 0)
        return null;
      int searchStart = Math.Max(0, length - 3);
      length += read;
      int marker = buffer.AsSpan(searchStart, length - searchStart).IndexOf("\r\n\r\n"u8);
      if (marker >= 0)
        return Parse(Encoding.Latin1.GetString(buffer, 0, searchStart + marker));
    }
    return null;
  }

  public static HttpRequestMessage UpstreamRequest(WebBrowserProxyRequest request, Uri url) {
    var upstream = new HttpRequestMessage(request.Method == "HEAD" ? HttpMethod.Head : HttpMethod.Get, url);
    foreach ((string name, string value) in request.Headers)
      if (ForwardedRequestHeaders.Contains(name))
        upstream.Headers.TryAddWithoutValidation(name, value);
    return upstream;
  }

  public static Task ConnectedAsync(Stream stream, CancellationToken cancellation) =>
      stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), cancellation).AsTask();

  public static async Task FailureAsync(Stream stream, HttpStatusCode status, string reason, CancellationToken cancellation) {
    byte[] body = Encoding.UTF8.GetBytes(reason);
    string headers = $"Content-Type: text/plain; charset=utf-8\r\nX-Urd-Web-Blocked: {reason}\r\n";
    await WriteHeadersAsync(stream, status, body.Length, headers, cancellation);
    await stream.WriteAsync(body, cancellation);
  }

  public static Task ResponseAsync(Stream stream, HttpResponseMessage response, long? contentLength, CancellationToken cancellation) {
    var headerLines = new StringBuilder();
    var omitted = new HashSet<string>(OmittedResponseHeaders, StringComparer.OrdinalIgnoreCase);
    omitted.UnionWith(response.Headers.Connection);
    AppendHeaders(headerLines, response.Headers, omitted);
    AppendHeaders(headerLines, response.Content.Headers, omitted);
    return WriteHeadersAsync(stream, response.StatusCode, contentLength, headerLines.ToString(), cancellation);
  }

  private static WebBrowserProxyRequest? Parse(string headerBlock) {
    string[] lines = headerBlock.Split("\r\n", StringSplitOptions.None);
    string[] first = lines[0].Split(' ');
    if (first.Length != 3 || first[2] is not ("HTTP/1.0" or "HTTP/1.1") || first[1].Any(char.IsControl))
      return null;

    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (string line in lines.Skip(1)) {
      int delimiter = line.IndexOf(':', StringComparison.Ordinal);
      if (delimiter <= 0)
        return null;
      string value = line[(delimiter + 1)..].Trim();
      if (value.Any(character => char.IsControl(character) && character != '\t')
          || !headers.TryAdd(line[..delimiter], value))
        return null;
    }
    return new WebBrowserProxyRequest(first[0], first[1], headers);
  }

  private static void AppendHeaders(StringBuilder builder, HttpHeaders headers, HashSet<string> omitted) {
    foreach ((string name, HeaderStringValues values) in headers.NonValidated) {
      if (omitted.Contains(name))
        continue;
      foreach (string value in values.Where(IsSingleLine))
        builder.Append(name).Append(": ").Append(value).Append("\r\n");
    }
  }

  private static bool IsSingleLine(string value) => !value.Contains('\r', StringComparison.Ordinal)
      && !value.Contains('\n', StringComparison.Ordinal);

  private static Task WriteHeadersAsync(Stream stream, HttpStatusCode status, long? length, string extra, CancellationToken cancellation) {
    string contentLength = length is null ? "" : string.Create(CultureInfo.InvariantCulture, $"Content-Length: {length}\r\n");
    string code = ((int)status).ToString(CultureInfo.InvariantCulture);
    return stream.WriteAsync(Encoding.Latin1.GetBytes($"HTTP/1.1 {code} {status}\r\nConnection: close\r\n{contentLength}{extra}\r\n"),
        cancellation).AsTask();
  }
}

internal sealed record WebBrowserProxyRequest(string Method, string Target, Dictionary<string, string> Headers);
