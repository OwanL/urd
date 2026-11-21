using System.Net;

namespace Urd.Server;

internal static class PlaywrightConfig {
  internal static bool Supported => OperatingSystem.IsWindows();

  internal static IReadOnlyList<Uri> ReadLocalOrigins() =>
      ParseLocalOrigins(Environment.GetEnvironmentVariable("URD_PLAYWRIGHT_LOCAL_ORIGINS"));

  internal static IReadOnlyList<Uri> ParseLocalOrigins(string? value) {
    if (string.IsNullOrWhiteSpace(value))
      return [];

    var origins = new List<Uri>();
    foreach (string part in value.Split(',')) {
      string text = part.Trim().TrimEnd('/');
      int authorityStart = text.IndexOf("://", StringComparison.Ordinal) + 3;
      if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
          || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
          || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
          || uri.AbsolutePath != "/" || text.Contains('\\') || text.Contains('@')
          || authorityStart < 3 || text.IndexOfAny(['/', '?', '#'], authorityStart) >= 0
          || !(string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
              || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address))))
        throw new ArgumentException("URD_PLAYWRIGHT_LOCAL_ORIGINS must contain comma-separated loopback http/https origins.");
      origins.Add(uri);
    }
    return origins;
  }
}
