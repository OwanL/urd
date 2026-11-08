using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

public sealed class ChatGptAuthenticationTests {
  private const string HostId = "urn:uuid:2fb0eb84-81b9-4c2b-9ccf-90e254d20756";

  [Fact]
  public async Task InitialSignInUsesRealDiscoveryPkceAndIssuedIdentityWithoutExposingTokens() {
    using var issuer = new FakeOpenAIIdentity();
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    Assert.Equal(0, issuer.DiscoveryGets);
    using var attempt = await auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, null, CancellationToken.None);
    Assert.Equal(1, issuer.DiscoveryGets);
    Assert.Equal(1, issuer.KeyGets);
    Assert.InRange(attempt.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
    var query = FakeOpenAIIdentity.Parse(attempt.AuthorizationUrl.Query);
    Assert.Equal(attempt.State, query["state"]);
    Assert.False(query.ContainsKey("id_token_hint"));
    Assert.False(query.ContainsKey("access_token"));
    Assert.False(query.ContainsKey("refresh_token"));

    string callback = await issuer.AuthorizeAsync(attempt);
    var result = await ChatGptAuthentication.CompleteAsync(attempt, callback, CancellationToken.None);
    Assert.Equal(new(FakeOpenAIIdentity.ClientId, FakeOpenAIIdentity.Subject, "hint@example.invalid"), result.Registration);
    Assert.Equal("synthetic-access-1", result.Credentials.AccessToken);
    Assert.Equal("synthetic-refresh-1", result.Credentials.RefreshToken);
    Assert.Equal(query["nonce"], result.Credentials.Nonce);
    Assert.Contains("chatgpt.tokens.use.direct", result.Credentials.Scopes);
    Assert.Contains("resource.invoke", result.Credentials.Scopes);
    Assert.Equal(1, issuer.CodePosts);
    Assert.False(result.Credentials.RequiresSignIn);
    Assert.InRange(result.Credentials.ExpiresAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));
    Assert.Equal("[provider authentication]", result.ToString());
    Assert.Equal("[provider credentials]", result.Credentials.ToString());

    var replay = await Assert.ThrowsAsync<ProviderOperationException>(() => ChatGptAuthentication.CompleteAsync(attempt, callback, CancellationToken.None));
    Assert.Equal(ProviderFailure.SignInRejected, replay.Failure);
    Assert.Equal(1, issuer.CodePosts);
    attempt.Dispose();
    Assert.False(issuer.Disposed); // Attempt owns its HttpClient, not the shared handler.
  }

  [Fact]
  public async Task AttemptsUseFreshStateNoncePkceAndOnlyAcceptTheirExactCallbackRoute() {
    using var issuer = new FakeOpenAIIdentity();
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    using var first = await auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, null, CancellationToken.None);
    using var second = await auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, null, CancellationToken.None);
    var firstQuery = FakeOpenAIIdentity.Parse(first.AuthorizationUrl.Query);
    var secondQuery = FakeOpenAIIdentity.Parse(second.AuthorizationUrl.Query);
    Assert.NotEqual(first.State, second.State);
    Assert.NotEqual(firstQuery["nonce"], secondQuery["nonce"]);
    Assert.NotEqual(firstQuery["code_challenge"], secondQuery["code_challenge"]);
    string callback = await issuer.AuthorizeAsync(first);
    string otherRoute = callback.Replace("/auth/callback?", "/other/callback?", StringComparison.Ordinal);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() => ChatGptAuthentication.CompleteAsync(first, otherRoute, CancellationToken.None));
    Assert.Equal(ProviderFailure.SignInRejected, error.Failure);
    Assert.Equal(0, issuer.CodePosts);
  }

  [Theory]
  [InlineData("wrong-state")]
  [InlineData("missing-state")]
  [InlineData("denial")]
  [InlineData("denial-wrong-state")]
  [InlineData("denial-missing-state")]
  [InlineData("missing-client")]
  [InlineData("placeholder-client")]
  public async Task InvalidCallbackNeverExchangesCode(string fault) {
    using var issuer = new FakeOpenAIIdentity { Fault = fault };
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    using var attempt = await auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, null, CancellationToken.None);
    string callback = await issuer.AuthorizeAsync(attempt);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() => ChatGptAuthentication.CompleteAsync(attempt, callback, CancellationToken.None));
    Assert.Equal(ProviderFailure.SignInRejected, error.Failure);
    Assert.Equal(0, issuer.CodePosts);
  }

  [Theory]
  [InlineData("missing-id")]
  [InlineData("missing-refresh")]
  [InlineData("unsigned")]
  [InlineData("wrong-key")]
  [InlineData("wrong-issuer")]
  [InlineData("wrong-audience")]
  [InlineData("expired")]
  [InlineData("missing-expiry")]
  [InlineData("wrong-nonce")]
  [InlineData("missing-nonce")]
  [InlineData("missing-subject")]
  public async Task SignedIdentityAndRenewableGrantAreRequired(string fault) {
    using var issuer = new FakeOpenAIIdentity { Fault = fault };
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    using var attempt = await auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, null, CancellationToken.None);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () =>
        await ChatGptAuthentication.CompleteAsync(attempt, await issuer.AuthorizeAsync(attempt), CancellationToken.None));
    Assert.Equal(ProviderFailure.SignInRejected, error.Failure);
    Assert.Equal("SignInRejected", error.Message);
    Assert.Null(error.InnerException);
    Assert.Equal(1, issuer.CodePosts);
  }

  [Theory]
  [InlineData("none", 1, true)]
  [InlineData("changed-client", 0, false)]
  [InlineData("other-subject", 1, false)]
  public async Task ReturningSignInKeepsClientAndSubjectBinding(string fault, int expectedPosts, bool success) {
    using var issuer = new FakeOpenAIIdentity { Fault = fault };
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    var registration = new ProviderRegistration(FakeOpenAIIdentity.ClientId, FakeOpenAIIdentity.Subject, "previous-hint@example.invalid");
    using var attempt = await auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, registration, CancellationToken.None);
    var query = FakeOpenAIIdentity.Parse(attempt.AuthorizationUrl.Query);
    Assert.Equal(registration.ClientId, query["client_id"]);
    Assert.False(query.ContainsKey("agent_name_hint"));
    Assert.False(query.ContainsKey("id_token_hint"));
    string callback = await issuer.AuthorizeAsync(attempt);
    if (success) {
      var result = await ChatGptAuthentication.CompleteAsync(attempt, callback, CancellationToken.None);
      Assert.Equal(registration.Subject, result.Registration.Subject);
      Assert.Equal("hint@example.invalid", result.Registration.Email); // Email changes do not change account identity.
    } else {
      var error = await Assert.ThrowsAsync<ProviderOperationException>(() => ChatGptAuthentication.CompleteAsync(attempt, callback, CancellationToken.None));
      Assert.Equal(ProviderFailure.SignInRejected, error.Failure);
    }
    Assert.Equal(expectedPosts, issuer.CodePosts);
  }

  [Theory]
  [InlineData("missing-plan")]
  [InlineData("missing-invoke")]
  [InlineData("missing-scope")]
  [InlineData("callback-scope")]
  [InlineData("scope-substring")]
  public async Task ValidIdentityDoesNotInventPlanGrants(string fault) {
    using var issuer = new FakeOpenAIIdentity { Fault = fault };
    var result = await SignInAsync(issuer);
    Assert.Equal(FakeOpenAIIdentity.Subject, result.Registration.Subject);
    Assert.False(result.Credentials.Scopes.Contains("resource.invoke", StringComparer.Ordinal) &&
        result.Credentials.Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal));
  }

  [Theory]
  [InlineData("none")]
  [InlineData("omitted-scope")]
  [InlineData("omitted-refresh")]
  [InlineData("refresh-id")]
  [InlineData("refresh-id-nonce")]
  public async Task RefreshPreservesOrReplacesOnlySuppliedGrantFields(string fault) {
    using var issuer = new FakeOpenAIIdentity();
    var initial = await SignInAsync(issuer);
    issuer.Fault = fault;
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    var refreshed = await auth.RefreshAsync(initial.Registration, initial.Credentials, CancellationToken.None);
    Assert.Equal("synthetic-access-2", refreshed.AccessToken);
    Assert.Equal(fault == "omitted-refresh" ? "synthetic-refresh-1" : "synthetic-refresh-2", refreshed.RefreshToken);
    Assert.Equal(initial.Credentials.Nonce, refreshed.Nonce);
    Assert.Equal(fault == "omitted-scope" ? initial.Credentials.Scopes : ["openid", "resource.invoke"], refreshed.Scopes);
    Assert.InRange(refreshed.ExpiresAt!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
    Assert.Equal(1, issuer.RefreshPosts);
    Assert.False(issuer.Disposed);
  }

  [Theory]
  [InlineData("refresh-id-wrong-key")]
  [InlineData("refresh-id-wrong-issuer")]
  [InlineData("refresh-id-wrong-audience")]
  [InlineData("refresh-id-expired")]
  [InlineData("refresh-id-wrong-nonce")]
  [InlineData("refresh-id-other-subject")]
  public async Task RefreshedIdentityCannotChangeTheValidatedAccount(string fault) {
    using var issuer = new FakeOpenAIIdentity();
    var initial = await SignInAsync(issuer);
    issuer.Fault = fault;
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() =>
        auth.RefreshAsync(initial.Registration, initial.Credentials, CancellationToken.None));
    Assert.Equal(ProviderFailure.SignInRejected, error.Failure);
    Assert.Equal(1, issuer.RefreshPosts);
    Assert.Equal("synthetic-refresh-1", initial.Credentials.RefreshToken);
  }

  [Fact]
  public async Task InvalidGrantRetainsTokensForExplicitDisconnectAndRequiresSignIn() {
    using var issuer = new FakeOpenAIIdentity();
    var initial = await SignInAsync(issuer);
    issuer.Fault = "invalid-grant";
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    var result = await auth.RefreshAsync(initial.Registration, initial.Credentials, CancellationToken.None);
    Assert.Equal(initial.Credentials with { RequiresSignIn = true }, result);
    Assert.Equal(1, issuer.RefreshPosts);
    Assert.Equal(result, await auth.RefreshAsync(initial.Registration, result, CancellationToken.None));
    Assert.Equal(1, issuer.RefreshPosts);
  }

  [Theory]
  [InlineData("transient", 1)]
  [InlineData("network", 0)]
  [InlineData("timeout", 0)]
  public async Task UncertainRefreshDoesNotRetryOrExposeProviderErrors(string fault, int expectedPosts) {
    using var issuer = new FakeOpenAIIdentity();
    var initial = await SignInAsync(issuer);
    issuer.Fault = fault;
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() =>
        auth.RefreshAsync(initial.Registration, initial.Credentials, CancellationToken.None));
    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Equal("ProviderUnavailable", error.Message);
    Assert.Null(error.InnerException);
    Assert.Equal(expectedPosts, issuer.RefreshPosts);
    Assert.False(initial.Credentials.RequiresSignIn);
  }

  [Theory]
  [InlineData("/.well-known/openid-configuration")]
  [InlineData("/jwks")]
  public async Task CancellationDuringRefreshDiscoveryPreservesTheKnownGrant(string path) {
    using var issuer = new FakeOpenAIIdentity();
    var initial = await SignInAsync(issuer);
    using var held = new HeldDiscovery(issuer, path);
    var auth = new ChatGptAuthentication(held, FakeOpenAIIdentity.Issuer);
    using var cancellation = new CancellationTokenSource();
    Task<ProviderCredentials> refresh = auth.RefreshAsync(initial.Registration, initial.Credentials, cancellation.Token);
    try {
      await held.Entered.Task.WaitAsync(TestTimeout);
      await cancellation.CancelAsync(); // The manager's refresh deadline, without a wall-clock delay.
      var error = await Assert.ThrowsAsync<ProviderOperationException>(() => refresh.WaitAsync(TestTimeout));
      Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
      Assert.Equal("ProviderUnavailable", error.Message);
      Assert.Null(error.InnerException);
      Assert.Equal(0, issuer.RefreshPosts);
      Assert.False(initial.Credentials.RequiresSignIn);
    } finally {
      await cancellation.CancelAsync();
      try {
        await refresh.WaitAsync(TestTimeout);
      } catch (Exception failure) when (failure is OperationCanceledException or ProviderOperationException) {
        // Join the cancelled discovery before disposing its borrowed transport.
      }
    }

    held.Hold = false;
    var refreshed = await auth.RefreshAsync(initial.Registration, initial.Credentials, CancellationToken.None).WaitAsync(TestTimeout);
    Assert.Equal("synthetic-access-2", refreshed.AccessToken);
    Assert.False(refreshed.RequiresSignIn);
    Assert.Equal(1, issuer.RefreshPosts);
  }

  [Theory]
  [InlineData("none", true, 1)]
  [InlineData("revoke-failure", false, 1)]
  [InlineData("no-revocation", false, 0)]
  [InlineData("network", false, 0)]
  public async Task RevocationReportsOnlyConfirmedSuccess(string fault, bool confirmed, int posts) {
    using var issuer = new FakeOpenAIIdentity();
    var initial = await SignInAsync(issuer);
    issuer.Fault = fault;
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    Assert.Equal(confirmed, await auth.RevokeAsync(initial.Registration, initial.Credentials, CancellationToken.None));
    Assert.Equal(posts, issuer.RevocationPosts);
    Assert.False(issuer.Disposed);
  }

  [Fact]
  public async Task CancellationPropagatesWithoutStartingNetworkWork() {
    using var issuer = new FakeOpenAIIdentity();
    var initial = await SignInAsync(issuer);
    int discoveryGets = issuer.DiscoveryGets;
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, null, cancellation.Token));
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.RefreshAsync(initial.Registration, initial.Credentials, cancellation.Token));
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.RevokeAsync(initial.Registration, initial.Credentials, cancellation.Token));
    Assert.Equal(discoveryGets, issuer.DiscoveryGets);
  }

  private sealed class HeldDiscovery(HttpMessageHandler inner, string path) : DelegatingHandler(inner) {
    internal TaskCompletionSource Entered { get; } = Signal();
    internal bool Hold { get; set; } = true;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      if (Hold && request.RequestUri!.AbsolutePath == path) {
        Assert.Equal(HttpMethod.Get, request.Method);
        Entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      }
      return await base.SendAsync(request, cancellationToken);
    }
  }

  private static async Task<ProviderAuthentication> SignInAsync(FakeOpenAIIdentity issuer) {
    var auth = new ChatGptAuthentication(issuer, FakeOpenAIIdentity.Issuer);
    using var attempt = await auth.BeginAsync(HostId, FakeOpenAIIdentity.Callback, null, CancellationToken.None);
    return await ChatGptAuthentication.CompleteAsync(attempt, await issuer.AuthorizeAsync(attempt), CancellationToken.None);
  }
}
