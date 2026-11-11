using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;

#pragma warning disable OPENAI001 // The pinned SDK's authentication-policy APIs.

namespace Urd.Core.Providers;

// Selects SDK authentication from the connection's configured method and current credentials.
internal static class ProviderSdkAuthentication {
  internal static AuthenticationPolicy Create(ProviderConnection connection, ProviderCredentials? credentials) {
    if (connection.Authentication == AuthenticationMethod.None)
      return KeylessAuthenticationPolicy.Instance;
    string? token = connection.Authentication switch {
      AuthenticationMethod.ApiKey => credentials?.ApiKey,
      AuthenticationMethod.OAuth when credentials?.RequiresSignIn == false => credentials.AccessToken,
      _ => null
    };
    if (string.IsNullOrWhiteSpace(token))
      throw new ProviderOperationException(ProviderFailure.AuthenticationRequired);
    return ApiKeyAuthenticationPolicy.CreateBearerAuthorizationPolicy(new ApiKeyCredential(token));
  }
}

#pragma warning restore OPENAI001
