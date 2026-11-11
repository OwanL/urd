using System.ClientModel;
using System.Text.Json;

namespace Urd.Core.Providers;

// A safe, classified failure; provider response bodies and credentials are never messages.
public sealed class ProviderOperationException(ProviderFailure failure) : Exception(failure.ToString()) {
  public ProviderFailure Failure { get; } = failure;

  internal static ProviderOperationException FromProviderError(Exception error,
      bool discovery = false, bool contextWindow = false) => new(error switch {
    ClientResultException { Status: 401 } => ProviderFailure.AuthenticationRequired,
    ClientResultException { Status: 403 } => ProviderFailure.PlanAccessDenied,
    ClientResultException clientError when contextWindow && IsContextWindowExceeded(clientError) =>
        ProviderFailure.ContextWindowExceeded,
    ClientResultException { Status: 404 or 405 or 501 } when discovery => ProviderFailure.DiscoveryUnsupported,
    ClientResultException or HttpRequestException or IOException or OperationCanceledException => ProviderFailure.ProviderUnavailable,
    _ => ProviderFailure.InvalidProviderResponse
  });

  internal static bool IsContextWindowExceeded(ClientResultException error) {
    try {
      var response = error.GetRawResponse();
      if (response is null) return false;

      using JsonDocument document = JsonDocument.Parse(response.Content);
      JsonElement root = document.RootElement;
      if (root.ValueKind != JsonValueKind.Object) return false;
      if (root.TryGetProperty("error", out JsonElement providerError) && providerError.ValueKind == JsonValueKind.Object &&
          providerError.TryGetProperty("code", out JsonElement nestedCode) && IsContextWindowExceededCode(nestedCode))
        return true;
      return root.TryGetProperty("code", out JsonElement code) && IsContextWindowExceededCode(code);
    } catch (Exception parseError) when (parseError is JsonException or InvalidOperationException or ArgumentException) {
      return false;
    }
  }

  internal static bool IsContextWindowExceededCode(string? code) => code == "context_length_exceeded";

  private static bool IsContextWindowExceededCode(JsonElement code) =>
      code.ValueKind == JsonValueKind.String && IsContextWindowExceededCode(code.GetString());
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
  ContextWindowExceeded
}
