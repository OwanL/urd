using System.Globalization;
using System.Net;

namespace Urd.Core.Tools;

internal sealed record PlaywrightNetworkDestination(Uri Url, IPAddress? LoopbackAddress) {
  public string Origin => Url.GetLeftPart(UriPartial.Authority);

  public static PlaywrightNetworkDestination? FromNavigation(Uri url) {
    if (!WebBrowserAddress.IsHttpUrl(url))
      return null;
    return FromAbsolute(url, url.Scheme, requirePort: false);
  }

  public static PlaywrightNetworkDestination? FromRequest(string target, bool tunnel) {
    if (target.AsSpan().ContainsAny('\\', '#') || (tunnel && target.AsSpan().ContainsAny('/', '?')))
      return null;
    string absolute = tunnel ? $"https://{target}/" : target;
    string scheme = tunnel ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
    return Uri.TryCreate(absolute, UriKind.Absolute, out Uri? url) ? FromAbsolute(url, scheme, requirePort: tunnel) : null;
  }

  private static PlaywrightNetworkDestination? FromAbsolute(Uri url, string scheme, bool requirePort) {
    if (!WebBrowserAddress.IsHttpUrl(url) || url.Scheme != scheme || !url.IsWellFormedOriginalString()
        || url.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
      return null;
    try {
      _ = url.IdnHost; // IDN conversion is lazy and can reject an otherwise parsed URI.
    } catch (UriFormatException) {
      return null;
    }

    string prefix = scheme + "://";
    if (!url.OriginalString.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
      return null;
    ReadOnlySpan<char> authority = url.OriginalString.AsSpan(prefix.Length);
    int end = authority.IndexOfAny('/', '?', '#');
    if (end >= 0)
      authority = authority[..end];
    if (authority.ContainsAny('@', '%', '\\'))
      return null;
    int hostEnd = authority.StartsWith("[", StringComparison.Ordinal) ? authority.IndexOf(']') + 1 : 0;
    int portSeparator = authority[hostEnd..].IndexOf(':');
    if (portSeparator < 0 && requirePort)
      return null;
    ReadOnlySpan<char> host = authority;
    if (portSeparator >= 0) {
      portSeparator += hostEnd;
      host = authority[..portSeparator];
      if (!int.TryParse(authority[(portSeparator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port)
          || port != url.Port)
        return null;
    }

    // Recognize only these spellings, before Uri normalization can turn an alias into a loopback literal.
    IPAddress? loopback = null;
    if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.SequenceEqual("127.0.0.1"))
      loopback = IPAddress.Loopback;
    else if (host.SequenceEqual("[::1]"))
      loopback = IPAddress.IPv6Loopback;
    return new PlaywrightNetworkDestination(url, loopback);
  }
}
