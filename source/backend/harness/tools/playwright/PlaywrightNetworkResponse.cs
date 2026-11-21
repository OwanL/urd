using System.Globalization;
using System.Text;

namespace Urd.Core.Tools;

internal static class PlaywrightNetworkResponse {
  private const int MaxHeaderBytes = 32 * 1024;
  private const long MaxBodyBytes = 256L * 1024 * 1024;
  private const int MaxChunkMetadataBytes = 16 * 1024 * 1024;
  private const int MaxInterimResponses = 16;

  public static async Task ForwardAsync(Stream server, Stream browser, string method, CancellationToken cancellation) {
    var reader = new ResponseReader(server);
    var headerBytes = 0;
    for (var interim = 0; interim <= MaxInterimResponses; interim++) {
      string status = await reader.ReadLineAsync(MaxHeaderBytes - headerBytes, cancellation);
      headerBytes += status.Length + 2;
      if (status.Length < 13 ||
          (status[..8] != "HTTP/1.0" && status[..8] != "HTTP/1.1") || status[8] != ' ' ||
          status[9] < '1' || status[9] > '5' || status[10] < '0' || status[10] > '9' ||
          status[11] < '0' || status[11] > '9' || status[12] != ' ' ||
          !IsHeaderValue(status[12..]))
        throw new IOException("Invalid HTTP response status.");
      int code = (status[9] - '0') * 100 + (status[10] - '0') * 10 + status[11] - '0';
      var headers = new List<KeyValuePair<string, string>>();
      var nominated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      long? length = null;
      bool chunked = false;
      while (true) {
        string line = await reader.ReadLineAsync(MaxHeaderBytes - headerBytes, cancellation);
        headerBytes += line.Length + 2;
        if (line.Length == 0)
          break;
        var header = ParseHeader(line);
        if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) {
          if (length.HasValue || header.Value.Length == 0 || header.Value.Any(c => c < '0' || c > '9') ||
              !long.TryParse(header.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed > MaxBodyBytes)
            throw new IOException("Invalid response Content-Length.");
          length = parsed;
        } else if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) {
          if (chunked || !header.Value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unsupported response Transfer-Encoding.");
          chunked = true;
        } else if (header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                   header.Key.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) {
          foreach (string token in header.Value.Split(',')) {
            string name = token.Trim(' ', '\t');
            if (!IsToken(name))
              throw new IOException("Invalid response Connection header.");
            nominated.Add(name);
          }
        } else {
          headers.Add(header);
        }
      }
      if ((length.HasValue && chunked) || nominated.Contains("Content-Length") || nominated.Contains("Transfer-Encoding"))
        throw new IOException("Ambiguous response framing.");
      if (code == 101)
        throw new IOException("HTTP protocol upgrades are not supported.");
      if (code < 200) {
        if (interim >= MaxInterimResponses || length.HasValue || chunked)
          throw new IOException("Invalid interim response.");
        continue;
      }

      bool noBody = method == "HEAD" || code is 204 or 205 or 304;
      var output = new StringBuilder().Append(status).Append("\r\n");
      foreach (var header in headers.Where(header => !nominated.Contains(header.Key) && !IsHopByHop(header.Key)))
        output.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
      if (length.HasValue && code != 204 && (code != 205 || length.Value == 0))
        output.Append("Content-Length: ").Append(length.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
      else if (chunked && !noBody)
        output.Append("Transfer-Encoding: chunked\r\n");
      output.Append("Connection: close\r\n\r\n");
      await browser.WriteAsync(Encoding.Latin1.GetBytes(output.ToString()), cancellation);
      if (noBody)
        return;
      if (chunked)
        await ForwardChunksAsync(reader, browser, cancellation);
      else if (length.HasValue)
        await CopyExactAsync(reader, browser, length.Value, new byte[16 * 1024], cancellation);
      else
        await CopyToEndAsync(reader, browser, cancellation);
      return;
    }
    throw new IOException("Too many interim responses.");
  }

  private static async Task ForwardChunksAsync(ResponseReader reader, Stream browser, CancellationToken cancellation) {
    long total = 0;
    int metadataBytes = 0;
    var copyBuffer = new byte[16 * 1024];
    while (true) {
      string line = await reader.ReadLineAsync(MaxHeaderBytes, cancellation);
      metadataBytes += line.Length + 4;
      if (metadataBytes > MaxChunkMetadataBytes)
        throw new IOException("HTTP response chunk metadata is too large.");
      int semicolon = line.IndexOf(';');
      // Extensions are hop-by-hop. Only the validated size is emitted to the browser.
      string size = semicolon < 0 ? line : line[..semicolon].TrimEnd(' ', '\t');
      if (size.Length == 0 || size.Any(c => !Uri.IsHexDigit(c)) ||
          !long.TryParse(size, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long count) ||
          count < 0 || count > MaxBodyBytes - total || !IsHeaderValue(line))
        throw new IOException("Invalid response chunk.");
      total += count;
      if (count == 0) {
        int trailerBytes = 0;
        while (true) {
          string trailer = await reader.ReadLineAsync(MaxHeaderBytes - trailerBytes, cancellation);
          trailerBytes += trailer.Length + 2;
          if (trailer.Length == 0)
            break;
          _ = ParseHeader(trailer);
        }
        await browser.WriteAsync("0\r\n\r\n"u8.ToArray(), cancellation);
        return;
      }
      await browser.WriteAsync(Encoding.ASCII.GetBytes(count.ToString("x", CultureInfo.InvariantCulture) + "\r\n"), cancellation);
      await CopyExactAsync(reader, browser, count, copyBuffer, cancellation);
      if (await reader.ReadByteAsync(cancellation) != '\r' || await reader.ReadByteAsync(cancellation) != '\n')
        throw new IOException("Invalid response chunk ending.");
      await browser.WriteAsync("\r\n"u8.ToArray(), cancellation);
    }
  }

  private static async Task CopyExactAsync(ResponseReader reader, Stream browser, long count, byte[] buffer,
      CancellationToken cancellation) {
    while (count > 0) {
      int read = await reader.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), cancellation);
      if (read == 0)
        throw new IOException("HTTP response body ended early.");
      await browser.WriteAsync(buffer.AsMemory(0, read), cancellation);
      count -= read;
    }
  }

  private static async Task CopyToEndAsync(ResponseReader reader, Stream browser, CancellationToken cancellation) {
    var buffer = new byte[16 * 1024];
    long total = 0;
    while (true) {
      int read = await reader.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxBodyBytes - total + 1)), cancellation);
      if (read == 0)
        return;
      if (read > MaxBodyBytes - total)
        throw new IOException("HTTP response body is too large.");
      await browser.WriteAsync(buffer.AsMemory(0, read), cancellation);
      total += read;
    }
  }

  private static KeyValuePair<string, string> ParseHeader(string line) {
    int colon = line.IndexOf(':');
    if (colon <= 0 || !IsToken(line[..colon]))
      throw new IOException("Invalid HTTP response header.");
    string value = line[(colon + 1)..].Trim(' ', '\t');
    if (!IsHeaderValue(value))
      throw new IOException("Invalid HTTP response header value.");
    return new(line[..colon], value);
  }

  private static bool IsHopByHop(string name) =>
    name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
    name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
    name.Equals("Alt-Svc", StringComparison.OrdinalIgnoreCase);

  private static bool IsToken(string value) => value.Length > 0 && value.All(IsTokenChar);
  private static bool IsTokenChar(char c) =>
    (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
    "!#$%&'*+-.^_`|~".Contains(c);
  private static bool IsHeaderValue(string value) => value.All(c => c == '\t' || (c >= ' ' && c != '\x7f'));

  private sealed class ResponseReader(Stream source) {
    private readonly byte[] buffer = new byte[16 * 1024];
    private readonly byte[] line = new byte[MaxHeaderBytes];
    private int offset;
    private int count;

    public async Task<int> ReadAsync(Memory<byte> destination, CancellationToken cancellation) {
      if (offset == count)
        return await source.ReadAsync(destination, cancellation);
      int length = Math.Min(destination.Length, count - offset);
      buffer.AsMemory(offset, length).CopyTo(destination);
      offset += length;
      return length;
    }

    public async ValueTask<int> ReadByteAsync(CancellationToken cancellation) {
      if (offset == count) {
        count = await source.ReadAsync(buffer, cancellation);
        offset = 0;
        if (count == 0)
          return -1;
      }
      return buffer[offset++];
    }

    public async Task<string> ReadLineAsync(int limit, CancellationToken cancellation) {
      if (limit < 2)
        throw new IOException("HTTP lines exceeded their byte limit.");
      int length = 0;
      while (true) {
        int value = await ReadByteAsync(cancellation);
        if (value == '\r') {
          if (await ReadByteAsync(cancellation) != '\n')
            throw new IOException("Invalid HTTP line ending.");
          return Encoding.Latin1.GetString(line, 0, length);
        }
        if (value < 0 || value == '\n' || length >= limit - 2)
          throw new IOException("Invalid or oversized HTTP line.");
        line[length++] = (byte)value;
      }
    }
  }
}
