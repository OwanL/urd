using System.Text.Json;
using static Urd.Core.Providers.AnthropicJson;

namespace Urd.Core.Providers;

internal static class AnthropicErrors {
  private const int MaximumErrorBytes = 16 * 1024;

  internal static async ValueTask<ProviderOperationException> FromHttpAsync(HttpResponseMessage response,
      ProviderCredentials? credentials, CancellationToken cancellationToken) {
    JsonElement root = default;
    if (response.Content.Headers.ContentLength is not > MaximumErrorBytes) {
      Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
      var bytes = new byte[MaximumErrorBytes + 1];
      int count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, cancellationToken).ConfigureAwait(false);
      if (count is > 0 and <= MaximumErrorBytes) {
        try {
          root = JsonSerializer.Deserialize<JsonElement>(bytes.AsSpan(0, count), Strict);
        } catch (JsonException) {
          // An HTML gateway error still has a meaningful HTTP status.
        }
      }
    }
    var error = Details(root);
    string? requestId = response.Headers.TryGetValues("request-id", out var ids) ||
        response.Headers.TryGetValues("x-request-id", out ids) ? ids.FirstOrDefault() : ReadString(root, "request_id");
    var retry = response.Headers.RetryAfter;
    TimeSpan? retryAfter = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
    if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
    if (retryAfter is null && response.Headers.TryGetValues("retry-after", out var retryValues) &&
        retryValues.FirstOrDefault()?.Trim() is { Length: > 0 } rawDelay && rawDelay.All(char.IsAsciiDigit))
      retryAfter = TimeSpan.MaxValue; // Outside the typed parser's range; never retry early.
    return ProviderOperationException.FromHttpError((int)response.StatusCode, error.Type, error.Code, error.Message,
        requestId, retryAfter, credentials);
  }

  internal static ProviderOperationException FromStream(JsonElement root, ProviderCredentials? credentials) {
    var error = Details(root);
    if (error.Type is null or "error" && error.Code is null) throw Invalid("invalid Anthropic stream error");
    string? code = error.Type switch {
      "rate_limit_error" => "rate_limit_exceeded",
      "invalid_request_error" when error.Message?.StartsWith("prompt is too long:", StringComparison.OrdinalIgnoreCase) == true =>
          "context_length_exceeded",
      _ => error.Code ?? error.Type
    };
    return ProviderOperationException.FromProviderStreamError(code, error.Message, null, credentials);
  }

  private static (string? Type, string? Code, string? Message) Details(JsonElement root) {
    JsonElement nested = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var value) ? value : default;
    return (ReadString(nested, "type") ?? ReadString(root, "type"), ReadString(nested, "code") ?? ReadString(root, "code"),
        ReadString(nested, "message") ?? ReadString(root, "message") ?? ReadString(root, "detail"));
  }
}
