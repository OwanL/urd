using System.ClientModel;

namespace Urd.Core.Providers;

// A safe, classified failure; provider response bodies and credentials are never messages.
public sealed class ProviderOperationException(ProviderFailure failure) : Exception(failure.ToString()) {
  public ProviderFailure Failure { get; } = failure;

  internal static ProviderOperationException FromProviderError(Exception error,
      bool discovery = false) => new(error switch {
    ClientResultException { Status: 401 } => ProviderFailure.AuthenticationRequired,
    ClientResultException { Status: 403 } => ProviderFailure.PlanAccessDenied,
    ClientResultException { Status: 404 or 405 or 501 } when discovery => ProviderFailure.DiscoveryUnsupported,
    ClientResultException or HttpRequestException or IOException or OperationCanceledException => ProviderFailure.ProviderUnavailable,
    _ => ProviderFailure.InvalidProviderResponse
  });
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
  DiscoveryUnsupported
}
