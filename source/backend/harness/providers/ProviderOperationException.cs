using System.ClientModel;
using System.Text;
using System.Text.Json;

namespace Urd.Core.Providers;

// Provider error text is retained only after known credentials are redacted and output is bounded.
public sealed class ProviderOperationException : Exception {
  private const int MaximumErrorBodyBytes = 16 * 1024;
  private const int MaximumMessageLength = 1024;

  public ProviderOperationException(ProviderFailure failure) : this(failure, failure.ToString(), null) { }

  internal ProviderOperationException(ProviderFailure failure, string reason) :
      this(failure, $"{failure}: {reason}", reason) { }

  private ProviderOperationException(ProviderFailure failure, string message, string? reason) : base(message) {
    Failure = failure;
    Reason = reason;
  }

  public ProviderFailure Failure { get; }
  public string? Reason { get; }
  internal ProviderDiagnosticTerminalKind? TerminalKind { get; private init; }
  internal ProviderDiagnostic.Cause? DiagnosticCause { get; private init; }

  internal static ProviderOperationException FromProviderError(Exception error,
      bool discovery = false, bool contextWindow = false, ProviderCredentials? credentials = null) {
    ProviderErrorDetails? details = error is ClientResultException clientError ? ReadErrorDetails(clientError) : null;
    ProviderFailure failure = error switch {
      ClientResultException { Status: 401 } => ProviderFailure.AuthenticationRequired,
      ClientResultException { Status: 403 } => ProviderFailure.PlanAccessDenied,
      ClientResultException when contextWindow && IsContextWindowExceededCode(details?.Code) =>
          ProviderFailure.ContextWindowExceeded,
      ClientResultException { Status: 400 or 422 } when IsModelImageRejection(details?.Code, details?.Message) =>
          ProviderFailure.ImageInputUnsupported,
      ClientResultException { Status: 404 or 405 or 501 } when discovery => ProviderFailure.DiscoveryUnsupported,
      ClientResultException or HttpRequestException or IOException or OperationCanceledException => ProviderFailure.ProviderUnavailable,
      _ => ProviderFailure.InvalidProviderResponse
    };

    int? status = details?.Status is >= 100 and <= 599 ? details.Status : null;
    var cause = ProviderDiagnostic.Describe(error, status);
    if (details is not null && status is null)
      return new(failure, "No HTTP response received from the provider.") { DiagnosticCause = cause };

    if (details is not null)
      return new(failure, FormatMessage(failure, details.Message, details.Code, details.Param, details.RequestId,
          status, null, credentials), null) { DiagnosticCause = cause };

    if (error is ResponsesSocketStream.ReceiveException receiveError)
      return new(failure, receiveError.Message) { DiagnosticCause = cause };

    return new(failure, FormatMessage(failure, null, null, null, null, null, error.GetType().Name, credentials), null) {
      DiagnosticCause = cause
    };
  }

  internal static ProviderOperationException FromProviderStreamError(string? code, string? message,
      string? param, ProviderCredentials? credentials, ProviderDiagnosticTerminalKind? terminalKind = null) {
    ProviderFailure failure = ProviderFailure.ProviderUnavailable;
    if (IsContextWindowExceededCode(code))
      failure = ProviderFailure.ContextWindowExceeded;
    else if (IsModelImageRejection(code, message))
      failure = ProviderFailure.ImageInputUnsupported;
    return new(failure, FormatMessage(failure, message, code, param, null, null, null, credentials), null) {
      TerminalKind = terminalKind
    };
  }

  // Do not treat malformed images, unsupported formats or generic invalid requests as model capability evidence.
  private static bool IsModelImageRejection(string? code, string? message) {
    if (code is "model_does_not_support_image_input" or "model_does_not_support_images")
      return true;
    if (code is not (null or "invalid_value" or "invalid_request_error" or "invalid_request" or "unsupported_value"))
      return false;

    return message?.Trim().TrimEnd('.') is { } text && (text.Equals("This model does not support image inputs", StringComparison.OrdinalIgnoreCase)
        || text.Equals("This model does not support image input", StringComparison.OrdinalIgnoreCase)
        || text.Equals("This model does not support images", StringComparison.OrdinalIgnoreCase)
        || text.Equals("Invalid content type. image_url is only supported by certain models", StringComparison.OrdinalIgnoreCase));
  }

  internal static bool IsContextWindowExceeded(ClientResultException error) =>
      IsContextWindowExceededCode(ReadErrorDetails(error).Code);

  internal static bool IsContextWindowExceededCode(string? code) => code == "context_length_exceeded";

  private static string FormatMessage(ProviderFailure failure, string? message, string? code, string? param,
      string? requestId, int? status, string? exceptionType, ProviderCredentials? credentials) {
    var text = new StringBuilder(failure.ToString());
    if (status is { } httpStatus) text.Append(" (HTTP ").Append(httpStatus).Append(')');
    if (exceptionType is not null) text.Append(" (").Append(exceptionType).Append(')');
    bool hasDetails = !string.IsNullOrWhiteSpace(message);
    if (hasDetails) text.Append(": ").Append(message);
    AppendDetail(text, "code", code, ref hasDetails);
    AppendDetail(text, "param", param, ref hasDetails);
    AppendDetail(text, "request ID", requestId, ref hasDetails);
    return BoundAndRedact(text.ToString(), credentials);
  }

  private static void AppendDetail(StringBuilder text, string label, string? value, ref bool hasDetails) {
    if (string.IsNullOrWhiteSpace(value)) return;
    text.Append(hasDetails ? "; " : ": ").Append(label).Append('=').Append(value);
    hasDetails = true;
  }

  private static string BoundAndRedact(string value, ProviderCredentials? credentials) {
    if (credentials is not null) {
      string[] secrets = new[] { credentials.ApiKey, credentials.AccessToken, credentials.RefreshToken,
          credentials.IdToken, credentials.Nonce }.OfType<string>().Where(secret => secret.Length > 0)
          .Distinct(StringComparer.Ordinal).OrderByDescending(secret => secret.Length).ToArray();
      foreach (string secret in secrets)
        value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
    }

    var normalized = new StringBuilder(value.Length);
    bool pendingSpace = false;
    foreach (char character in value) {
      if (char.IsControl(character) || char.IsWhiteSpace(character)) {
        pendingSpace = normalized.Length > 0;
        continue;
      }
      if (pendingSpace) normalized.Append(' ');
      normalized.Append(character);
      pendingSpace = false;
    }

    if (normalized.Length <= MaximumMessageLength) return normalized.ToString();
    int length = MaximumMessageLength - 3;
    if (char.IsHighSurrogate(normalized[length - 1])) length--;
    return normalized.ToString(0, length) + "...";
  }

  private static ProviderErrorDetails ReadErrorDetails(ClientResultException error) {
    int status = error.Status;
    var response = error.GetRawResponse();
    if (response is null) return new(status, null, null, null, null);

    string? requestId = response.Headers.TryGetValue("x-request-id", out var headerRequestId)
        ? headerRequestId : null;
    try {
      ReadOnlyMemory<byte> content = response.Content.ToMemory();
      if (content.Length > MaximumErrorBodyBytes) return new(status, null, null, null, requestId);
      using JsonDocument document = JsonDocument.Parse(content);
      JsonElement root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object) return new(status, null, null, null, requestId);

      JsonElement nested = root.TryGetProperty("error", out JsonElement providerError) &&
          providerError.ValueKind == JsonValueKind.Object ? providerError : default;
      string? message = FirstNonBlank(ReadString(nested, "message"), ReadString(root, "detail"), ReadString(root, "message"));
      string? code = FirstNonBlank(ReadString(nested, "code"), ReadString(root, "code"));
      string? param = FirstNonBlank(ReadString(nested, "param"), ReadString(root, "param"));
      requestId ??= ReadString(nested, "request_id") ?? ReadString(root, "request_id");
      return new(status, message, code, param, requestId);
    } catch (Exception parseError) when (parseError is JsonException or InvalidOperationException or ArgumentException) {
      return new(status, null, null, null, requestId);
    }
  }

  private static string? ReadString(JsonElement element, string property) =>
      element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) &&
      value.ValueKind == JsonValueKind.String ? value.GetString() : null;

  private static string? FirstNonBlank(params string?[] values) =>
      values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

  private sealed record ProviderErrorDetails(int Status, string? Message, string? Code, string? Param, string? RequestId);
}

public enum ProviderFailure {
  ConnectionChanged,
  AuthenticationRequired,
  PlanAccessDenied,
  SignInRejected,
  InvalidProviderResponse,
  ProviderUnavailable,
  RefreshUncertain,
  IncompleteResponse,
  Disconnecting,
  DiscoveryUnsupported,
  ContextWindowExceeded,
  ImageInputUnsupported
}
