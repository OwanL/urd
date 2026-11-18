using System.Text;

namespace Urd.Core.Tools;

internal enum WebSearchStatus { Completed, Blocked, RateLimited, TimedOut, Failed }
internal sealed record WebSearchHit(string Title, string Url, string Snippet, string? Published, int Rank);
internal sealed record WebSearchSourceResult(WebSearchStatus Status, IReadOnlyList<WebSearchHit> Hits, string? Message = null);
internal sealed record WebSearchProvenance(string Source, int Rank);
internal sealed record WebSearchMergedHit(WebSearchHit Hit, List<WebSearchProvenance> Sources);

internal static class WebSearchResults {
  private static readonly HashSet<string> Tracking = new(StringComparer.OrdinalIgnoreCase) {
    "gclid", "dclid", "fbclid", "msclkid", "mc_cid", "mc_eid", "yclid",
    "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id"
  };

  public static IReadOnlyList<WebSearchMergedHit> Merge(WebSearchSourceResult you, WebSearchSourceResult bing) {
    List<WebSearchMergedHit> merged = [];
    Dictionary<string, WebSearchMergedHit> seen = new(StringComparer.Ordinal);
    for (int i = 0; i < 5; i++) {
      Add(you, i, "You.com", merged, seen);
      Add(bing, i, "Bing", merged, seen);
    }
    return merged;
  }

  private static void Add(WebSearchSourceResult result, int index, string source, List<WebSearchMergedHit> merged,
      Dictionary<string, WebSearchMergedHit> seen) {
    if (result.Status != WebSearchStatus.Completed)
      return;
    WebSearchHit? hit = result.Hits.FirstOrDefault(candidate => candidate.Rank == index + 1);
    if (hit is null)
      return;
    string url = Destination(hit.Url);
    if (url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https") ||
        !string.IsNullOrEmpty(uri.UserInfo) || IsBingRedirect(uri))
      return;
    string key = Key(uri);
    if (!seen.TryGetValue(key, out WebSearchMergedHit? item)) {
      item = new WebSearchMergedHit(hit with { Url = uri.AbsoluteUri }, []);
      seen.Add(key, item);
      merged.Add(item);
    }
    item.Sources.Add(new WebSearchProvenance(source, hit.Rank));
  }

  internal static string Destination(string value) {
    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !IsBingRedirect(uri))
      return value;
    foreach (string part in uri.Query.TrimStart('?').Split('&')) {
      string[] pair = part.Split('=', 2);
      if (pair.Length != 2 || !pair[0].Equals("u", StringComparison.OrdinalIgnoreCase))
        continue;
      try {
        string encoded = Uri.UnescapeDataString(pair[1]);
        if (!encoded.StartsWith("a1", StringComparison.Ordinal))
          break;
        encoded = encoded[2..].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        if (Uri.TryCreate(decoded, UriKind.Absolute, out Uri? target) && target.Scheme is "http" or "https")
          return decoded;
      } catch (FormatException) {
        break;
      }
    }
    return value;
  }

  internal static bool IsBingRedirect(Uri uri) =>
    (uri.Host.Equals("bing.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".bing.com", StringComparison.OrdinalIgnoreCase)) &&
    uri.AbsolutePath.Equals("/ck/a", StringComparison.OrdinalIgnoreCase);

  private static string Key(Uri uri) {
    string authority = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    string path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
    string query = string.Join('&', uri.Query.TrimStart('?').Split('&').Where(part => {
      string name = Uri.UnescapeDataString(part.Split('=', 2)[0]);
      return !Tracking.Contains(name);
    }));
    return authority + "/" + path + (query.Length > 0 ? "?" + query : "") + uri.Fragment;
  }
}
