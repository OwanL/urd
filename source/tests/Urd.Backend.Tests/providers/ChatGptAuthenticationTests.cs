using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Xunit;

namespace Urd.Backend.Tests.Providers;

public sealed class ChatGptAuthenticationTests {
  private const string HostId = "urn:uuid:2fb0eb84-81b9-4c2b-9ccf-90e254d20756";

  [Fact]
  public async Task SignInUsesPkceStateAndTheIssuedIdentityWithoutExposingTokens() {
    using var issuer = new FakeOpenAIIdentity();
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    using var attempt = await auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, null, CancellationToken.None);
    var query = FakeOpenAIIdentity.Parse(attempt.AuthorizationUrl.Query);

    Assert.Equal(attempt.State, query["state"]);
    Assert.Equal("S256", query["code_challenge_method"]);
    Assert.False(query.ContainsKey("access_token"));
    Assert.False(query.ContainsKey("refresh_token"));
    Assert.Equal(1, issuer.DiscoveryGets);
    Assert.Equal(1, issuer.KeyGets);

    ProviderAuthentication result = await ChatGptAuthentication.CompleteAsync(
        attempt, await issuer.AuthorizeAsync(attempt), CancellationToken.None);

    Assert.Equal(new(FakeOpenAIIdentity.ClientId, FakeOpenAIIdentity.Subject, "hint@example.invalid"), result.Registration);
    Assert.Equal("synthetic-access-1", result.Credentials.AccessToken);
    Assert.Equal("synthetic-refresh-1", result.Credentials.RefreshToken);
    Assert.Equal(query["nonce"], result.Credentials.Nonce);
    Assert.Equal("[provider authentication]", result.ToString());
    Assert.Equal("[provider credentials]", result.Credentials.ToString());
    Assert.Equal(1, issuer.CodePosts);
  }

  [Fact]
  public async Task CallbackStateIsRequiredBeforeCodeExchange() {
    using var issuer = new FakeOpenAIIdentity();
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    using var attempt = await auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, null, CancellationToken.None);
    string callback = await issuer.AuthorizeAsync(attempt);

    var error = await Assert.ThrowsAsync<ProviderOperationException>(() =>
        ChatGptAuthentication.CompleteAsync(attempt, callback.Replace(attempt.State, "wrong-state", StringComparison.Ordinal), CancellationToken.None));

    Assert.Equal(ProviderFailure.SignInRejected, error.Failure);
    Assert.Equal(0, issuer.CodePosts);
  }

  [Theory]
  [InlineData("wrong-key")]
  [InlineData("missing-refresh")]
  public async Task InvalidIssuedIdentityFailsWithSecretSafeError(string fault) {
    using var issuer = new FakeOpenAIIdentity { Fault = fault };
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    using var attempt = await auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, null, CancellationToken.None);

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () =>
        await ChatGptAuthentication.CompleteAsync(attempt, await issuer.AuthorizeAsync(attempt), CancellationToken.None));

    Assert.Equal(ProviderFailure.SignInRejected, error.Failure);
    Assert.Equal("SignInRejected", error.Message);
    Assert.Null(error.InnerException);
    Assert.DoesNotContain("synthetic-private", error.ToString());
  }

  [Theory]
  [InlineData("none", 1, true)]
  [InlineData("changed-client", 0, false)]
  [InlineData("other-subject", 1, false)]
  public async Task ReturningSignInCannotChangeTheBoundClientOrSubject(string fault, int expectedPosts, bool succeeds) {
    using var issuer = new FakeOpenAIIdentity { Fault = fault };
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    var registration = new ProviderRegistration(FakeOpenAIIdentity.ClientId, FakeOpenAIIdentity.Subject, "old@example.invalid");
    using var attempt = await auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, registration, CancellationToken.None);
    var query = FakeOpenAIIdentity.Parse(attempt.AuthorizationUrl.Query);
    Assert.Equal(registration.ClientId, query["client_id"]);

    if (succeeds) {
      var result = await ChatGptAuthentication.CompleteAsync(attempt, await issuer.AuthorizeAsync(attempt), CancellationToken.None);
      Assert.Equal(registration.Subject, result.Registration.Subject);
      Assert.Equal("hint@example.invalid", result.Registration.Email);
    } else {
      var error = await Assert.ThrowsAsync<ProviderOperationException>(async () =>
          await ChatGptAuthentication.CompleteAsync(attempt, await issuer.AuthorizeAsync(attempt), CancellationToken.None));
      Assert.Equal(ProviderFailure.SignInRejected, error.Failure);
    }
    Assert.Equal(expectedPosts, issuer.CodePosts);
  }
}
