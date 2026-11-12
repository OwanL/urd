namespace Urd.Core.MarkdownRendering;

internal static class MarkdownLinkDestinationPolicy {
  // Operates on Markdig's decoded destination. No URL is fetched here.
  internal static bool IsAllowed(string destination) {
    if (ContainsDisallowedCharacters(destination)) return false;
    if (destination.StartsWith("//", StringComparison.Ordinal)) return false;
    if (!HasScheme(destination)) return true;
    if (!Uri.TryCreate(destination, UriKind.Absolute, out var uri)) return false;
    return IsAllowedAbsoluteUri(destination, uri);
  }

  private static bool ContainsDisallowedCharacters(string destination) =>
    destination.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\');

  private static bool HasScheme(string destination) {
    var colon = destination.IndexOf(':');
    if (colon < 0) return false;

    // A delimiter before the colon makes this a relative path, query or fragment.
    var delimiter = destination.IndexOfAny(['/', '?', '#']);
    return delimiter < 0 || colon < delimiter;
  }

  private static bool IsAllowedAbsoluteUri(string destination, Uri uri) =>
    uri.Scheme switch {
      "http" or "https" => !string.IsNullOrEmpty(uri.Host),
      "mailto" => destination.Length > "mailto:".Length,
      _ => false
    };
}
