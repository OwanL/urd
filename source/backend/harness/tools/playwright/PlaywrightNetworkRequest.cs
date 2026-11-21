using System.Globalization;
using System.Text;

namespace Urd.Core.Tools;

internal sealed class PlaywrightNetworkRequest {
  private const int MaxHeaderBytes = 32 * 1024;
  private const long MaxBodyBytes = 64L * 1024 * 1024;
  private readonly List<KeyValuePair<string, string>> headers;
  private readonly byte[] buffered;
  private readonly int bufferedOffset;
  private readonly int bufferedCount;
  private readonly long bodyLength;

  private PlaywrightNetworkRequest(string method, string target, List<KeyValuePair<string, string>> headers,
    long bodyLength, byte[] buffered, int bufferedOffset, int bufferedCount) {
    Method = method;
    Target = target;
    this.headers = headers;
    this.bodyLength = bodyLength;
    this.buffered = buffered;
    this.bufferedOffset = bufferedOffset;
    this.bufferedCount = bufferedCount;
  }

  public string Method { get; }
  public string Target { get; }

  public static async Task<PlaywrightNetworkRequest?> ReadAsync(Stream source, CancellationToken cancellation) {
    var buffer = new byte[MaxHeaderBytes];
    var count = 0;
    var scanned = 0;
    while (count < buffer.Length) {
      var read = await source.ReadAsync(buffer.AsMemory(count), cancellation);
      if (read == 0)
        return null;
      count += read;
      for (var i = scanned; i <= count - 4; i++) {
        if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
          return Parse(buffer, i + 4, count);
      }
      scanned = Math.Max(0, count - 3);
    }
    return null;
  }

  public async Task ForwardAsync(Stream source, Stream destination, Uri url, CancellationToken cancellation) {
    string host = url.HostNameType == UriHostNameType.IPv6 ? url.Host : url.IdnHost;
    if (!url.IsDefaultPort)
      host += ":" + url.Port.ToString(CultureInfo.InvariantCulture);

    var request = new StringBuilder();
    request.Append(Method).Append(' ').Append(url.PathAndQuery).Append(" HTTP/1.1\r\n");
    request.Append("Host: ").Append(host).Append("\r\n");
    foreach (var header in headers)
      request.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
    request.Append("Content-Length: ").Append(bodyLength.ToString(CultureInfo.InvariantCulture)).Append("\r\nConnection: close\r\n\r\n");
    await destination.WriteAsync(Encoding.Latin1.GetBytes(request.ToString()), cancellation);

    var remaining = bodyLength;
    var prefixLength = (int)Math.Min(remaining, bufferedCount);
    if (prefixLength > 0) {
      await destination.WriteAsync(buffered.AsMemory(bufferedOffset, prefixLength), cancellation);
      remaining -= prefixLength;
    }
    if (remaining == 0)
      return;

    var copyBuffer = new byte[16 * 1024];
    while (remaining > 0) {
      var read = await source.ReadAsync(copyBuffer.AsMemory(0, (int)Math.Min(remaining, copyBuffer.Length)), cancellation);
      if (read == 0)
        throw new IOException("The request body ended before its declared Content-Length.");
      await destination.WriteAsync(copyBuffer.AsMemory(0, read), cancellation);
      remaining -= read;
    }
  }

  public async Task ForwardTunnelPrefixAsync(Stream destination, CancellationToken cancellation) {
    if (bufferedCount > 0)
      await destination.WriteAsync(buffered.AsMemory(bufferedOffset, bufferedCount), cancellation);
  }

  private static PlaywrightNetworkRequest? Parse(byte[] buffer, int headerEnd, int count) {
    var text = Encoding.Latin1.GetString(buffer, 0, headerEnd);
    var lines = text.Split("\r\n", StringSplitOptions.None);
    var parts = lines[0].Split(' ');
    if (parts.Length != 3 || !IsToken(parts[0]) || !IsTarget(parts[1]) ||
        (parts[2] != "HTTP/1.0" && parts[2] != "HTTP/1.1"))
      return null;

    var headers = new List<KeyValuePair<string, string>>();
    var nominated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    long bodyLength = 0;
    var hasContentLength = false;
    for (var i = 1; i < lines.Length - 2; i++) {
      var line = lines[i];
      var colon = line.IndexOf(':');
      if (colon <= 0)
        return null;
      var name = line[..colon];
      var value = line[(colon + 1)..].Trim(' ', '\t');
      if (!IsToken(name) || !IsHeaderValue(value))
        return null;
      if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
          name.Equals("Expect", StringComparison.OrdinalIgnoreCase) ||
          name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
        return null;
      if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) {
        if (hasContentLength || value.Length == 0 || value.Any(c => c < '0' || c > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out bodyLength) || bodyLength > MaxBodyBytes)
          return null;
        hasContentLength = true;
      } else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) {
        foreach (var token in value.Split(',')) {
          var trimmed = token.Trim(' ', '\t');
          if (!IsToken(trimmed))
            return null;
          nominated.Add(trimmed);
        }
      } else {
        headers.Add(new(name, value));
      }
    }
    if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase) && (parts[0] != "CONNECT" || bodyLength != 0))
      return null;
    headers.RemoveAll(header => nominated.Contains(header.Key) || IsHopByHop(header.Key));
    return new PlaywrightNetworkRequest(parts[0], parts[1], headers, bodyLength, buffer, headerEnd, count - headerEnd);
  }

  private static bool IsHopByHop(string name) =>
    name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
    name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("Trailer", StringComparison.OrdinalIgnoreCase);

  private static bool IsToken(string value) => value.Length > 0 && value.All(c =>
    (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
    "!#$%&'*+-.^_`|~".Contains(c));

  private static bool IsTarget(string value) => value.Length > 0 && value.All(c => c >= '!' && c <= '~');

  private static bool IsHeaderValue(string value) => value.All(c => c == '\t' || (c >= ' ' && c != '\x7f'));
}
