using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Urd.Core.Providers;

// Borrows the manager's redirect-disabled transport. GitHub OAuth tokens go only to the fixed GitHub REST endpoints.
internal sealed class GitHubCopilotAuthentication(HttpClient client, string? clientId, TimeProvider? clock = null) {
  internal const string DefaultClientId = "Iv1.b507a08c87ecfe98";
  private const int MaximumResponseBytes = 64 * 1024;
  private const int MaximumTokenLength = 16 * 1024;
  private const int MaximumDeviceLifetimeSeconds = 15 * 60;
  private static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(30);
  private static readonly Uri deviceCodeEndpoint = new("https://github.com/login/device/code");
  private static readonly Uri verificationEndpoint = new("https://github.com/login/device");
  private static readonly Uri oauthTokenEndpoint = new("https://github.com/login/oauth/access_token");
  private static readonly Uri userEndpoint = new("https://api.github.com/user");
  private static readonly Uri copilotTokenEndpoint = new("https://api.github.com/copilot_internal/v2/token");
  private readonly TimeProvider time = clock ?? TimeProvider.System;
  private readonly string configuredClientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId.Trim();

  internal static Uri DefaultEndpoint { get; } = new("https://api.individual.githubcopilot.com/");
  internal bool IsExpired(DateTimeOffset expiresAt) => time.GetUtcNow() >= expiresAt;

  internal async Task<GitHubCopilotDeviceFlow> BeginAsync(CancellationToken cancellationToken) {
    DateTimeOffset requestedAt = time.GetUtcNow();
    using HttpRequestMessage request = Form(deviceCodeEndpoint, ("client_id", configuredClientId), ("scope", "read:user"));
    using JsonDocument response = await ReadAsync(request, cancellationToken).ConfigureAwait(false);
    JsonElement root = response.RootElement;
    string deviceCode = RequireToken(root, "device_code");
    string? userCode = ReadString(root, "user_code");
    int lifetime = RequirePositiveInt(root, "expires_in");
    int interval = root.TryGetProperty("interval", out _) ? RequirePositiveInt(root, "interval") : 5;
    if (userCode is not { Length: > 0 and <= 32 } || !userCode.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ||
        ReadString(root, "verification_uri") != verificationEndpoint.AbsoluteUri)
      throw InvalidResponse();

    // Count the lifetime from the request to avoid polling after a delayed issuance response.
    return new(deviceCode, userCode, verificationEndpoint, requestedAt.AddSeconds(Math.Min(lifetime, MaximumDeviceLifetimeSeconds)),
        time.GetUtcNow().AddSeconds(Math.Min(interval, MaximumDeviceLifetimeSeconds)), interval);
  }

  internal async Task<ProviderAuthentication?> CompleteAsync(GitHubCopilotDeviceFlow flow,
      ProviderRegistration? registration, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    TimeSpan remaining = flow.ExpiresAt - time.GetUtcNow();
    if (remaining <= TimeSpan.Zero)
      return null;

    using var expiry = new CancellationTokenSource(remaining, time);
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
    CancellationToken token = lifetime.Token;
    try {
      string? githubToken = await PollAsync(flow, token).ConfigureAwait(false);
      token.ThrowIfCancellationRequested();
      if (githubToken is null || time.GetUtcNow() >= flow.ExpiresAt)
        return null;

      ProviderRegistration account = await ReadAccountAsync(githubToken, token).ConfigureAwait(false);
      token.ThrowIfCancellationRequested();
      if (time.GetUtcNow() >= flow.ExpiresAt)
        return null;
      if (registration is not null && account.Subject != registration.Subject)
        throw new ProviderOperationException(ProviderFailure.SignInRejected);

      ProviderCredentials credentials = await ExchangeAsync(githubToken, token).ConfigureAwait(false);
      token.ThrowIfCancellationRequested();
      return time.GetUtcNow() < flow.ExpiresAt ? new(account, credentials) : null;
    } catch (OperationCanceledException) when (expiry.IsCancellationRequested && !cancellationToken.IsCancellationRequested) {
      return null;
    }
  }

  internal async Task<ProviderCredentials> RefreshAsync(
      ProviderRegistration registration, ProviderCredentials credentials, CancellationToken cancellationToken) {
    cancellationToken.ThrowIfCancellationRequested();
    if (registration.ClientId != configuredClientId || credentials.RequiresSignIn || !IsToken(credentials.RefreshToken))
      return credentials with { RequiresSignIn = true };

    try {
      return await ExchangeAsync(credentials.RefreshToken!, cancellationToken).ConfigureAwait(false);
    } catch (ProviderOperationException failure) when (failure.Failure == ProviderFailure.AuthenticationRequired) {
      return credentials with { RequiresSignIn = true };
    }
  }

  internal static bool HasGrant(ProviderCredentials credentials) =>
      IsToken(credentials.AccessToken) && IsToken(credentials.RefreshToken) && credentials.ExpiresAt is not null;

  internal static Uri GetEndpoint(ProviderCredentials credentials) {
    if (!IsToken(credentials.AccessToken))
      throw InvalidResponse();

    Uri? endpoint = null;
    foreach (string part in credentials.AccessToken!.Split(';')) {
      if (!part.StartsWith("proxy-ep=", StringComparison.OrdinalIgnoreCase))
        continue;
      if (endpoint is not null)
        throw InvalidResponse();
      endpoint = part[9..].ToLowerInvariant() switch {
        "proxy.githubcopilot.com" => new("https://api.githubcopilot.com/"),
        "proxy.individual.githubcopilot.com" => DefaultEndpoint,
        "proxy.business.githubcopilot.com" => new("https://api.business.githubcopilot.com/"),
        "proxy.enterprise.githubcopilot.com" => new("https://api.enterprise.githubcopilot.com/"),
        _ => throw InvalidResponse()
      };
    }
    return endpoint ?? DefaultEndpoint;
  }

  private async Task<string?> PollAsync(GitHubCopilotDeviceFlow flow, CancellationToken cancellationToken) {
    long interval = flow.IntervalSeconds;
    DateTimeOffset next = flow.FirstPollAt;
    while (next < flow.ExpiresAt) {
      TimeSpan delay = next - time.GetUtcNow();
      if (delay > TimeSpan.Zero)
        await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();
      if (time.GetUtcNow() >= flow.ExpiresAt)
        return null;

      using var request = Form(oauthTokenEndpoint, ("client_id", configuredClientId), ("device_code", flow.DeviceCode),
          ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"));
      using JsonDocument response = await ReadAsync(request, cancellationToken, allowDeviceError: true).ConfigureAwait(false);
      JsonElement root = response.RootElement;
      switch (ReadString(root, "error")) {
        case null:
          if (!string.Equals(ReadString(root, "token_type"), "bearer", StringComparison.OrdinalIgnoreCase))
            throw InvalidResponse();
          return RequireToken(root, "access_token");
        case "authorization_pending":
          break;
        case "slow_down":
          interval += 5;
          if (root.TryGetProperty("interval", out _))
            interval = Math.Max(interval, RequirePositiveInt(root, "interval"));
          break;
        case "expired_token":
          return null;
        default:
          throw new ProviderOperationException(ProviderFailure.SignInRejected);
      }
      next = time.GetUtcNow().AddSeconds(Math.Min(interval, MaximumDeviceLifetimeSeconds));
    }

    // A polling interval may extend past the code's expiry; do not poll again or expire early.
    TimeSpan remaining = flow.ExpiresAt - time.GetUtcNow();
    if (remaining > TimeSpan.Zero)
      await Task.Delay(remaining, time, cancellationToken).ConfigureAwait(false);
    return null;
  }

  private async Task<ProviderRegistration> ReadAccountAsync(string githubToken, CancellationToken cancellationToken) {
    using HttpRequestMessage request = ApiGet(userEndpoint, githubToken);
    using JsonDocument response = await ReadAsync(request, cancellationToken).ConfigureAwait(false);
    JsonElement root = response.RootElement;
    string? login = ReadString(root, "login");
    if (!root.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number ||
        !id.TryGetInt64(out long subject) || subject <= 0 ||
        login is not { Length: > 0 and <= 128 } || !login.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
      throw InvalidResponse();
    return new(configuredClientId, subject.ToString(CultureInfo.InvariantCulture), login);
  }

  private async Task<ProviderCredentials> ExchangeAsync(string githubToken, CancellationToken cancellationToken) {
    using HttpRequestMessage request = CopilotApiGet(copilotTokenEndpoint, githubToken);
    using JsonDocument response = await ReadAsync(request, cancellationToken).ConfigureAwait(false);
    JsonElement root = response.RootElement;
    string accessToken = RequireToken(root, "token");
    if (!root.TryGetProperty("expires_at", out JsonElement expires) || expires.ValueKind != JsonValueKind.Number ||
        !expires.TryGetInt64(out long seconds) || seconds <= time.GetUtcNow().ToUnixTimeSeconds() ||
        seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
      throw InvalidResponse();

    var credentials = new ProviderCredentials(null, accessToken, githubToken, null, DateTimeOffset.FromUnixTimeSeconds(seconds), []);
    _ = GetEndpoint(credentials);
    return credentials;
  }

  private async Task<JsonDocument> ReadAsync(HttpRequestMessage request, CancellationToken cancellationToken,
      bool allowDeviceError = false) {
    using var timeout = new CancellationTokenSource(requestTimeout, time);
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
    CancellationToken token = lifetime.Token;
    try {
      token.ThrowIfCancellationRequested();
      request.Headers.Accept.ParseAdd("application/json");
      if (request.Headers.UserAgent.Count == 0)
        request.Headers.UserAgent.ParseAdd("Urd");
      using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
          .ConfigureAwait(false);
      token.ThrowIfCancellationRequested();
      if (!(response.IsSuccessStatusCode || (allowDeviceError && response.StatusCode == HttpStatusCode.BadRequest)))
        throw new ProviderOperationException(response.StatusCode switch {
          HttpStatusCode.Unauthorized => ProviderFailure.AuthenticationRequired,
          HttpStatusCode.Forbidden when request.RequestUri == copilotTokenEndpoint => ProviderFailure.PlanAccessDenied,
          _ => ProviderFailure.ProviderUnavailable
        });
      if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        throw InvalidResponse();

      using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
      using var body = new MemoryStream();
      byte[] buffer = new byte[4096];
      int read;
      while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) {
        token.ThrowIfCancellationRequested();
        if (body.Length + read > MaximumResponseBytes)
          throw InvalidResponse();
        await body.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
      }
      token.ThrowIfCancellationRequested();
      JsonDocument document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, (int)body.Length), new() { MaxDepth = 16 });
      try {
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            (response.IsSuccessStatusCode || ReadString(document.RootElement, "error") is not null))
          return document;
        throw InvalidResponse();
      } catch {
        document.Dispose();
        throw;
      }
    } catch (JsonException) {
      throw InvalidResponse();
    } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
      throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
    } catch (Exception failure) when (failure is HttpRequestException or IOException) {
      cancellationToken.ThrowIfCancellationRequested();
      throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
    }
  }

  private static HttpRequestMessage Form(Uri endpoint, params (string Name, string Value)[] fields) {
    var request = new HttpRequestMessage(HttpMethod.Post, endpoint) {
      Content = new FormUrlEncodedContent(fields.Select(field => KeyValuePair.Create(field.Name, field.Value)))
    };
    request.Headers.UserAgent.ParseAdd(GitHubCopilotRequest.UserAgent);
    return request;
  }

  private static HttpRequestMessage CopilotApiGet(Uri endpoint, string token) {
    var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    GitHubCopilotRequest.ApplyIdentityHeaders(request);
    return request;
  }

  private static HttpRequestMessage ApiGet(Uri endpoint, string token) {
    var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
    return request;
  }

  private static string? ReadString(JsonElement root, string property) {
    if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
      return null;
    try {
      return value.GetString();
    } catch (InvalidOperationException) {
      throw InvalidResponse();
    }
  }

  private static bool IsToken(string? value) => value is { Length: > 0 and <= MaximumTokenLength } &&
      value.All(c => c is > ' ' and <= '~');

  private static string RequireToken(JsonElement root, string property) => ReadString(root, property) is { } value && IsToken(value)
      ? value : throw InvalidResponse();

  private static int RequirePositiveInt(JsonElement root, string property) =>
      root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
      value.TryGetInt32(out int result) && result > 0 ? result : throw InvalidResponse();

  private static ProviderOperationException InvalidResponse() => new(ProviderFailure.InvalidProviderResponse);
}
