namespace Urd.Core.Providers;

// A secret-free view of one saved provider connection.
public sealed record ProviderConnection(
    Guid Id, string Name, ProviderRoute Route, LlmApi Api, Uri Endpoint,
    AuthenticationMethod Authentication);

public enum ProviderRoute { OpenAI, ChatGpt, Custom }
public enum LlmApi { OpenAIResponses, OpenAIChatCompletions }
public enum AuthenticationMethod { None, ApiKey, OAuth }

// The editable settings of a custom API connection, without credentials.
public sealed record CustomProviderSettings(
    string Name, LlmApi Api, Uri Endpoint, AuthenticationMethod Authentication);

// A private committed view used to fence asynchronous results against later changes.
internal sealed record ProviderState(
    ProviderConnection Connection, long Generation, long CredentialVersion,
    ProviderRegistration? Registration, ProviderCredentials? Credentials,
    bool Disconnecting, ProviderModelCatalog? Catalog) {
  public override string ToString() => $"Provider connection {Connection.Id}";
}
