namespace Urd.Core.Display;

internal static class DisplayLinkPolicy {
  // Operates on Markdig's decoded destination. No URL is fetched here.
  internal static bool IsAllowed(string destination) {
    if (destination.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\')) return false;
    if (destination.StartsWith("//", StringComparison.Ordinal)) return false;
    var colon = destination.IndexOf(':');
    var separator = destination.IndexOfAny(['/', '?', '#']);
    if (colon < 0 || (separator >= 0 && separator < colon)) return true;
    if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri)) return false;
    return uri.Scheme switch {
      "http" or "https" => !string.IsNullOrEmpty(uri.Host),
      "mailto" => destination.Length > "mailto:".Length,
      _ => false
    };
  }
}
