using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Urd.Core.Providers;
using Xunit;

namespace Urd.Backend.Tests.Support;

// All discovery, authorization and token traffic terminates here; no socket fallback.
internal sealed class FakeOpenAIIdentity : HttpMessageHandler {
  internal static readonly Uri Issuer = new("https://auth.urd.invalid");
  internal static readonly Uri Callback = new("http://127.0.0.1:54321/auth/callback");
  internal const string ClientId = "oaiapp_synthetic_urd";
  internal const string Subject = "synthetic-subject";
  internal const string GrantedScopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
  private readonly RSA key = RSA.Create(2048);
  private readonly RSA otherKey = RSA.Create(2048);
  private Dictionary<string, string> authorization = [];

  internal string Fault { get; set; } = "none";
  internal Uri CallbackUri { get; set; } = Callback;
  internal string ExpectedRevocationToken { get; set; } = "synthetic-refresh-1";
  internal int DiscoveryGets { get; private set; }
  internal int KeyGets { get; private set; }
  internal int CodePosts { get; private set; }
  internal int RefreshPosts { get; private set; }
  internal int RevocationPosts { get; private set; }
  internal bool Disposed { get; private set; }

  internal Task<string> AuthorizeAsync(ChatGptSignIn attempt) => AuthorizeAsync(attempt.AuthorizationUrl);

  internal async Task<string> AuthorizeAsync(Uri authorizationUrl) {
    using var http = new HttpClient(this, disposeHandler: false);
    using var response = await http.GetAsync(authorizationUrl);
    return response.Headers.Location!.AbsoluteUri;
  }

  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    Assert.False(Disposed);
    Assert.Equal(Issuer.GetLeftPart(UriPartial.Authority), request.RequestUri!.GetLeftPart(UriPartial.Authority));
    string path = request.RequestUri.AbsolutePath;
    if (Fault == "network")
      throw new HttpRequestException("synthetic-private-provider-body");
    if (Fault == "timeout")
      throw new TaskCanceledException("synthetic-private-provider-body");
    if (path == "/.well-known/openid-configuration") {
      Assert.Equal(HttpMethod.Get, request.Method);
      DiscoveryGets++;
      return Json(new {
        issuer = Issuer.GetLeftPart(UriPartial.Authority),
        authorization_endpoint = Issuer + "api/accounts/authorize",
        token_endpoint = Issuer + "api/accounts/oauth/token",
        revocation_endpoint = Fault == "no-revocation" ? null : Issuer + "revoke",
        jwks_uri = Issuer + "jwks",
        response_types_supported = new[] { "code" }, subject_types_supported = new[] { "public" },
        id_token_signing_alg_values_supported = new[] { "RS256" },
        token_endpoint_auth_methods_supported = new[] { "none" }, code_challenge_methods_supported = new[] { "S256" }
      });
    }
    if (path == "/jwks") {
      Assert.Equal(HttpMethod.Get, request.Method);
      KeyGets++;
      var publicKey = key.ExportParameters(false);
      return Json(new { keys = new[] { new {
        kty = "RSA", kid = "ephemeral", use = "sig", alg = "RS256",
        n = Base64UrlEncoder.Encode(publicKey.Modulus!), e = Base64UrlEncoder.Encode(publicKey.Exponent!)
      } } });
    }
    if (path == "/api/accounts/authorize") {
      Assert.Equal(HttpMethod.Get, request.Method);
      authorization = Parse(request.RequestUri.Query);
      bool returning = authorization["client_id"] == ClientId;
      Assert.Equal(returning ? ClientId : "dynamic_agent_client", authorization["client_id"]);
      Assert.Equal(CallbackUri.AbsoluteUri, authorization["redirect_uri"]);
      Assert.Equal(ChatGptAuthentication.Resource, authorization["resource"]);
      Assert.Equal(GrantedScopes, authorization["scope"]);
      Assert.Equal("code", authorization["response_type"]);
      Assert.Equal("S256", authorization["code_challenge_method"]);
      Assert.False(string.IsNullOrEmpty(authorization["nonce"]));
      Assert.StartsWith("urn:uuid:", authorization["ext_agent_host_id"], StringComparison.Ordinal);
      Assert.Equal(!returning, authorization.ContainsKey("agent_name_hint"));
      Assert.False(authorization.ContainsKey("id_token_hint"));
      var callback = new Dictionary<string, string> { ["state"] = authorization["state"] };
      if (Fault.Contains("wrong-state", StringComparison.Ordinal)) callback["state"] = "other-state";
      if (Fault.Contains("missing-state", StringComparison.Ordinal)) callback.Remove("state");
      if (Fault.StartsWith("denial", StringComparison.Ordinal)) callback["error"] = "access_denied";
      else {
        callback["code"] = "synthetic-code";
        if (!returning) callback["client_id"] = ClientId;
        if (Fault == "missing-client") callback.Remove("client_id");
        if (Fault == "placeholder-client") callback["client_id"] = "dynamic_agent_client";
        if (Fault == "changed-client") callback["client_id"] = "oaiapp_other";
        if (Fault == "callback-scope") callback["scope"] = GrantedScopes;
      }
      return new(HttpStatusCode.Redirect) { Headers = { Location = new(CallbackUri + "?" + string.Join('&',
          callback.Select(item => Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value)))) } };
    }

    Assert.Equal(HttpMethod.Post, request.Method);
    Assert.Null(request.Headers.Authorization);
    Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
    var form = Parse(await request.Content.ReadAsStringAsync(cancellationToken));
    Assert.False(form.ContainsKey("client_secret"));
    Assert.Equal(ClientId, form["client_id"]);
    if (path == "/revoke") {
      RevocationPosts++;
      Assert.Equal(ExpectedRevocationToken, form["token"]);
      Assert.Equal("refresh_token", form["token_type_hint"]);
      return Fault == "revoke-failure" ? Json(new { error = "private-failure" }, HttpStatusCode.ServiceUnavailable) : Json(new { });
    }
    Assert.Equal("/api/accounts/oauth/token", path);
    Assert.Equal(ChatGptAuthentication.Resource, form["resource"]);
    if (form["grant_type"] == "refresh_token") {
      RefreshPosts++;
      Assert.Equal("synthetic-refresh-1", form["refresh_token"]);
      Assert.False(form.ContainsKey("scope"));
      if (Fault == "refresh-network") // Simulate rotation followed by loss of the token-endpoint acknowledgement.
        throw new HttpRequestException("synthetic-private-provider-body");
      if (Fault == "invalid-grant") return Json(new { error = "invalid_grant", error_description = "private-description" }, HttpStatusCode.BadRequest);
      if (Fault == "transient") return Json(new { error = "temporarily_unavailable", error_description = "private-description" }, HttpStatusCode.ServiceUnavailable);
      var refreshed = new Dictionary<string, object> {
        ["access_token"] = "synthetic-access-2", ["refresh_token"] = "synthetic-refresh-2",
        ["expires_in"] = 1800, ["token_type"] = "Bearer"
      };
      if (Fault != "omitted-scope") refreshed["scope"] = "openid resource.invoke";
      if (Fault == "omitted-refresh") refreshed.Remove("refresh_token");
      if (Fault.StartsWith("refresh-id", StringComparison.Ordinal)) refreshed["id_token"] = CreateToken(refresh: true);
      return Json(refreshed);
    }
    CodePosts++;
    Assert.Equal("authorization_code", form["grant_type"]);
    Assert.Equal("synthetic-code", form["code"]);
    Assert.Equal(CallbackUri.AbsoluteUri, form["redirect_uri"]);
    Assert.InRange(form["code_verifier"].Length, 43, 128);
    Assert.Equal(authorization["code_challenge"], Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))));
    var tokens = new Dictionary<string, object> {
      ["access_token"] = "synthetic-access-1", ["refresh_token"] = "synthetic-refresh-1",
      ["expires_in"] = 3600, ["token_type"] = "Bearer", ["scope"] = GrantedScopes
    };
    if (Fault != "missing-id") tokens["id_token"] = CreateToken(refresh: false);
    if (Fault == "missing-refresh") tokens.Remove("refresh_token");
    if (Fault == "missing-plan") tokens["scope"] = "openid resource.invoke";
    if (Fault == "missing-invoke") tokens["scope"] = "openid chatgpt.tokens.use.direct";
    if (Fault is "missing-scope" or "callback-scope") tokens.Remove("scope");
    if (Fault == "scope-substring") tokens["scope"] = "openid resource.invoke chatgpt.tokens.use.direct.extra";
    return Json(tokens);
  }

  private string CreateToken(bool refresh) {
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var claims = new Dictionary<string, object> {
      ["iss"] = Fault.EndsWith("wrong-issuer", StringComparison.Ordinal) ? "https://wrong.urd.invalid" : Issuer.GetLeftPart(UriPartial.Authority),
      ["aud"] = Fault.EndsWith("wrong-audience", StringComparison.Ordinal) ? "dynamic_agent_client" : ClientId,
      ["sub"] = Fault is "other-subject" or "refresh-id-other-subject" ? "other-subject" : Subject,
      ["iat"] = now - 600, ["nbf"] = now - 600,
      ["exp"] = Fault.EndsWith("expired", StringComparison.Ordinal) ? now - 60 : now + 300,
      ["nonce"] = Fault.EndsWith("wrong-nonce", StringComparison.Ordinal) ? "other-nonce" : authorization["nonce"],
      ["email"] = "hint@example.invalid"
    };
    if (Fault == "missing-expiry") claims.Remove("exp");
    if (Fault == "missing-subject") claims.Remove("sub");
    if (Fault == "missing-nonce" || (refresh && Fault != "refresh-id-nonce" && !Fault.EndsWith("wrong-nonce", StringComparison.Ordinal)))
      claims.Remove("nonce");
    var signingKey = new RsaSecurityKey(Fault.EndsWith("wrong-key", StringComparison.Ordinal) ? otherKey : key) { KeyId = "ephemeral" };
    return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor {
      Claims = claims,
      SigningCredentials = Fault == "unsigned" ? null : new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
    });
  }

  internal static Dictionary<string, string> Parse(string text) => text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
      .Select(item => item.Split('=', 2)).ToDictionary(item => WebUtility.UrlDecode(item[0]), item => WebUtility.UrlDecode(item.Length == 2 ? item[1] : ""));

  private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) {
    Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
  };

  protected override void Dispose(bool disposing) {
    if (disposing) {
      Disposed = true;
      key.Dispose();
      otherKey.Dispose();
    }
    base.Dispose(disposing);
  }
}
