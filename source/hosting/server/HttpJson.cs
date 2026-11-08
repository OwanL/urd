using System.Text;
using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace Urd.Server;

// Shared JSON admission rules for local command endpoints.
internal static class HttpJson {
  internal static bool TryPrepare(HttpRequest request) {
    if (!request.HasJsonContentType())
      return false;
    MediaTypeHeaderValue mediaType = MediaTypeHeaderValue.Parse(request.ContentType!);
    if (!mediaType.Charset.HasValue)
      return true;
    try {
      Encoding encoding = Encoding.GetEncoding(HeaderUtilities.RemoveQuotes(mediaType.Charset).Value!);
      mediaType.Charset = encoding.WebName;
      request.ContentType = mediaType.ToString();
      return true;
    } catch (ArgumentException) {
      return false;
    } catch (NotSupportedException) {
      return false;
    }
  }

  internal static async Task RequireEmptyAsync(HttpRequest request, CancellationToken token) {
    JsonElement body = await request.ReadFromJsonAsync<JsonElement>(token);
    if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any())
      throw new ArgumentException("This operation requires an empty JSON object.");
  }
}
