namespace Urd.Core.Providers;

// A private committed view used to fence asynchronous results against later changes.
internal sealed record ProviderState(
    ProviderConnection Connection, long Generation, long CredentialVersion,
    ProviderRegistration? Registration, ProviderCredentials? Credentials,
    bool Disconnecting, ProviderModelCatalog? Catalog) {
  public override string ToString() => $"Provider connection {Connection.Id}";
}
