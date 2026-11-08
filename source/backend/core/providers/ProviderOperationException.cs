namespace Urd.Core.Providers;

// A safe, classified failure; provider response bodies and credentials are never messages.
public sealed class ProviderOperationException(ProviderFailure failure) : Exception(failure.ToString()) {
  public ProviderFailure Failure { get; } = failure;
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
