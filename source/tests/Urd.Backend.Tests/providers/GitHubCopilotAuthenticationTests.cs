using System.Net;
using System.Threading.Channels;
using Urd.Backend.Tests.Support;
using Urd.Core.Concurrency;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

public sealed class GitHubCopilotAuthenticationTests {
  private const string clientId = "urd-fixture-client";
  private const string githubToken = "synthetic-github-credential";
  private const string deviceCode = "synthetic-device-credential";
  private const string userCode = "URDF-TEST";
  private const string verificationUrl = "https://github.com/login/device";

  [Fact]
  public async Task DeviceLoginUsesItsOwnClientAndHonorsPollIntervalsBeforeBindingTheGitHubAccount() {
    var clock = new DeviceClock();
    int polls = 0;
    var steps = new List<string>();
    using var client = Client(async (request, token) => {
      string path = request.RequestUri!.AbsolutePath;
      steps.Add(path);
      Assert.Contains("Urd", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
      Assert.False(request.Headers.Contains("Copilot-Integration-Id"));
      switch (path) {
        case "/login/device/code":
          Assert.Equal("https://github.com/login/device/code", request.RequestUri.AbsoluteUri);
          Assert.Equal(HttpMethod.Post, request.Method);
          Assert.Equal($"client_id={clientId}&scope=read%3Auser", await request.Content!.ReadAsStringAsync(token));
          return Device();
        case "/login/oauth/access_token":
          Assert.Equal("https://github.com/login/oauth/access_token", request.RequestUri.AbsoluteUri);
          Assert.Equal(HttpMethod.Post, request.Method);
          Assert.Equal($"client_id={clientId}&device_code={deviceCode}&grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code",
              await request.Content!.ReadAsStringAsync(token));
          return ++polls switch {
            1 => Json("""{"error":"authorization_pending"}"""),
            2 => Json("""{"error":"slow_down","interval":9}""", HttpStatusCode.BadRequest),
            _ => OAuthToken()
          };
        case "/user":
          AssertGitHubRequest(request, "https://api.github.com/user");
          return Account();
        case "/copilot_internal/user":
          AssertGitHubRequest(request, "https://api.github.com/copilot_internal/user");
          return Metadata();
        default:
          throw new InvalidOperationException("Unexpected request.");
      }
    });
    var authentication = new GitHubCopilotAuthentication(client, clientId, clock);
    GitHubCopilotDeviceFlow flow = await authentication.BeginAsync(CancellationToken.None);
    Assert.Equal(userCode, flow.UserCode);
    Assert.Equal(verificationUrl, flow.VerificationUrl.AbsoluteUri);
    Assert.DoesNotContain(deviceCode, flow.ToString(), StringComparison.Ordinal);

    Task<ProviderAuthentication?> completion = authentication.CompleteAsync(flow, new(clientId, "43210", "old-login"),
        CancellationToken.None);
    await clock.WaitForDelayAsync(2);
    Assert.Equal(["/login/device/code"], steps);
    clock.Advance(2);
    await clock.WaitForDelayAsync(2);
    clock.Advance(2);
    await clock.WaitForDelayAsync(9);
    clock.Advance(9);

    ProviderAuthentication? result = await completion.WaitAsync(TestTimeout);
    Assert.NotNull(result);
    Assert.Equal(new ProviderRegistration(clientId, "43210", "new-login"), result.Registration);
    Assert.Null(result.Credentials.RefreshToken);
    Assert.Equal(githubToken, result.Credentials.AccessToken);
    Assert.Null(result.Credentials.ExpiresAt);
    Assert.Equal(new Uri("https://api.business.githubcopilot.com/"), result.Credentials.CopilotEndpoint);
    Assert.Equal(new Uri("https://api.business.githubcopilot.com/"), GitHubCopilotAuthentication.GetEndpoint(result.Credentials));
    Assert.Equal(["/login/device/code", "/login/oauth/access_token", "/login/oauth/access_token", "/login/oauth/access_token",
        "/user", "/copilot_internal/user"], steps);
  }

  [Fact]
  public async Task DifferentGitHubAccountIsRejectedWithoutExchangingItsToken() {
    var clock = new DeviceClock();
    int calls = 0;
    using var client = Client((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch {
      "/login/device/code" => Device(),
      "/login/oauth/access_token" => OAuthToken(),
      "/user" => CountAccount(),
      _ => throw new InvalidOperationException("Account mismatch must stop the token exchange.")
    }));
    var authentication = new GitHubCopilotAuthentication(client, clientId, clock);
    GitHubCopilotDeviceFlow flow = await authentication.BeginAsync(CancellationToken.None);
    Task<ProviderAuthentication?> completion = authentication.CompleteAsync(flow, new(clientId, "7", "new-login"),
        CancellationToken.None);
    await clock.WaitForDelayAsync(2);
    clock.Advance(2);
    ProviderOperationException error = await Assert.ThrowsAsync<ProviderOperationException>(() => completion.WaitAsync(TestTimeout));
    Assert.Equal(ProviderFailure.SignInRejected, error.Failure);
    Assert.Equal(1, calls);

    HttpResponseMessage CountAccount() {
      calls++;
      return Account();
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CancelOrExpiryDuringAnInFlightPollPreventsProfileAndTokenExchange(bool expire) {
    var clock = new DeviceClock();
    TaskCompletionSource inFlight = Signal(), release = Signal();
    using var cancel = new CancellationTokenSource();
    using var client = Client(async (request, _) => {
      if (request.RequestUri!.AbsolutePath == "/login/device/code")
        return Device();
      Assert.Equal("/login/oauth/access_token", request.RequestUri.AbsolutePath);
      inFlight.TrySetResult();
      await release.Task.WaitAsync(TestTimeout, CancellationToken.None); // A late acknowledgement can arrive after cancellation.
      return OAuthToken();
    });
    var authentication = new GitHubCopilotAuthentication(client, clientId, clock);
    GitHubCopilotDeviceFlow flow = await authentication.BeginAsync(cancel.Token);
    Task<ProviderAuthentication?> completion = authentication.CompleteAsync(flow, null, cancel.Token);
    await clock.WaitForDelayAsync(2);
    clock.Advance(2);
    await inFlight.Task.WaitAsync(TestTimeout);
    if (expire)
      clock.Advance(58);
    else
      await cancel.CancelAsync();
    release.TrySetResult();

    if (expire)
      Assert.Null(await completion.WaitAsync(TestTimeout));
    else
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TestTimeout));
  }

  [Fact]
  public async Task NoPollWhenFirstIntervalWouldReachExpiry() {
    var clock = new DeviceClock();
    using var client = Client((request, _) => {
      Assert.Equal("/login/device/code", request.RequestUri!.AbsolutePath);
      return Task.FromResult(Device(expires: 2));
    });
    var authentication = new GitHubCopilotAuthentication(client, clientId, clock);
    GitHubCopilotDeviceFlow flow = await authentication.BeginAsync(CancellationToken.None);
    Task<ProviderAuthentication?> completion = authentication.CompleteAsync(flow, null, CancellationToken.None);
    await clock.WaitForDelayAsync(2);
    clock.Advance(2);
    Assert.Null(await completion.WaitAsync(TestTimeout));
  }

  [Theory]
  [InlineData("https://example.invalid/login/device")]
  [InlineData("https://github.com/login/device?return_to=https://example.invalid")]
  [InlineData("http://github.com/login/device")]
  public async Task BrowserVerificationUrlMustBeTheFixedGitHubDevicePage(string verification) {
    using var client = Client((_, _) => Task.FromResult(Device(verification: verification)));
    var authentication = new GitHubCopilotAuthentication(client, clientId, new DeviceClock());
    ProviderOperationException error = await Assert.ThrowsAsync<ProviderOperationException>(() => authentication.BeginAsync(CancellationToken.None));
    Assert.Equal(ProviderFailure.InvalidProviderResponse, error.Failure);
  }

  [Fact]
  public async Task MissingClientIdDoesNotSendARequest() {
    using var client = Client((_, _) => throw new InvalidOperationException("Sign-in must be disabled."));
    var authentication = new GitHubCopilotAuthentication(client, " ");
    Assert.False(authentication.IsConfigured);
    ProviderOperationException error = await Assert.ThrowsAsync<ProviderOperationException>(() => authentication.BeginAsync(CancellationToken.None));
    Assert.Equal(ProviderFailure.CopilotNotConfigured, error.Failure);
  }

  [Theory]
  [InlineData(HttpStatusCode.Unauthorized, ProviderFailure.AuthenticationRequired)]
  [InlineData(HttpStatusCode.Forbidden, ProviderFailure.PlanAccessDenied)]
  [InlineData(HttpStatusCode.ServiceUnavailable, ProviderFailure.ProviderUnavailable)]
  [InlineData(HttpStatusCode.Redirect, ProviderFailure.ProviderUnavailable)]
  public async Task MetadataFailureIsClassifiedWithoutUntrustedDetails(HttpStatusCode status, ProviderFailure failure) {
    using var response = Json($$"""{"message":"do not retain {{githubToken}}"}""", status);
    if (status == HttpStatusCode.Redirect)
      response.Headers.Location = new("https://untrusted.invalid/redirect");
    ProviderOperationException error = await Assert.ThrowsAsync<ProviderOperationException>(() => CompleteWithMetadataAsync(response));
    Assert.Equal(failure, error.Failure);
    Assert.Equal(failure.ToString(), error.Message);
    Assert.Null(error.InnerException);
  }

  [Theory]
  [InlineData("https://api.business.githubcopilot.com.attacker.invalid")]
  [InlineData("https://api.business.githubcopilot.com:444")]
  [InlineData("https://user@api.business.githubcopilot.com")]
  [InlineData("http://api.business.githubcopilot.com")]
  [InlineData("https://api.business.githubcopilot.com/path")]
  [InlineData("https://api.business.githubcopilot.com/path/..")]
  [InlineData("https://@api.business.githubcopilot.com")]
  [InlineData("https://api.business.githubcopilot.com/?query=value")]
  [InlineData("https://api.business.githubcopilot.com/#fragment")]
  [InlineData("not a uri")]
  public async Task MetadataRejectsUntrustedEndpoints(string endpoint) {
    using var response = Json(System.Text.Json.JsonSerializer.Serialize(new { endpoints = new { api = endpoint } }));
    ProviderOperationException error = await Assert.ThrowsAsync<ProviderOperationException>(() => CompleteWithMetadataAsync(response));
    Assert.Equal(ProviderFailure.InvalidProviderResponse, error.Failure);
    Assert.Equal(ProviderFailure.InvalidProviderResponse.ToString(), error.Message);
    Assert.Null(error.InnerException);
  }

  [Theory]
  [InlineData("{}")]
  [InlineData("{\"endpoints\":{}}")]
  [InlineData("{\"endpoints\":{\"api\":null}}")]
  [InlineData("{\"endpoints\":{\"api\":42}}")]
  [InlineData("not json")]
  public async Task MetadataRejectsMissingOrMalformedPayload(string body) {
    using var response = Json(body);
    ProviderOperationException error = await Assert.ThrowsAsync<ProviderOperationException>(() => CompleteWithMetadataAsync(response));
    Assert.Equal(ProviderFailure.InvalidProviderResponse, error.Failure);
  }

  [Fact]
  public async Task MetadataRejectsOversizedPayload() {
    using var response = Json(new string(' ', 65_537));
    ProviderOperationException error = await Assert.ThrowsAsync<ProviderOperationException>(() => CompleteWithMetadataAsync(response));
    Assert.Equal(ProviderFailure.InvalidProviderResponse, error.Failure);
  }

  [Theory]
  [InlineData(null)]
  [InlineData(120)]
  public async Task DeviceTokenExpiryIsStoredOnlyWhenPositive(int? expires) {
    using var response = Metadata();
    ProviderAuthentication result = await CompleteWithMetadataAsync(response, expires);
    Assert.Equal(expires > 0 ? DateTimeOffset.UnixEpoch.AddSeconds(2 + expires.Value) : null, result.Credentials.ExpiresAt);
    Assert.Equal(githubToken, result.Credentials.AccessToken);
    Assert.Null(result.Credentials.RefreshToken);
  }

  [Theory]
  [InlineData("0")]
  [InlineData("-1")]
  [InlineData("null")]
  [InlineData("\"invalid\"")]
  public async Task DeviceTokenRejectsInvalidExpiry(string expires) {
    var clock = new DeviceClock();
    var paths = new List<string>();
    using var client = Client((request, _) => {
      string path = request.RequestUri!.AbsolutePath;
      paths.Add(path);
      return Task.FromResult(path switch {
        "/login/device/code" => Device(),
        "/login/oauth/access_token" => Json($$"""{"access_token":"{{githubToken}}","token_type":"bearer","expires_in":{{expires}}}"""),
        _ => throw new InvalidOperationException("Invalid expiry must stop sign-in.")
      });
    });
    var authentication = new GitHubCopilotAuthentication(client, clientId, clock);
    GitHubCopilotDeviceFlow flow = await authentication.BeginAsync(CancellationToken.None);
    Task<ProviderAuthentication?> completion = authentication.CompleteAsync(flow, null, CancellationToken.None);
    await clock.WaitForDelayAsync(2);
    clock.Advance(2);
    ProviderOperationException error = await Assert.ThrowsAsync<ProviderOperationException>(() => completion.WaitAsync(TestTimeout));
    Assert.Equal(ProviderFailure.InvalidProviderResponse, error.Failure);
    Assert.Equal(["/login/device/code", "/login/oauth/access_token"], paths);
  }

  [Fact]
  public void InferenceEndpointRequiresStoredEndpointAndAccessToken() {
    var credentials = new ProviderCredentials(null, githubToken, null, null, null, []) {
      CopilotEndpoint = new Uri("https://api.business.githubcopilot.com/")
    };
    Assert.Equal(credentials.CopilotEndpoint, GitHubCopilotAuthentication.GetEndpoint(credentials));
    foreach (ProviderCredentials invalid in new[] {
      credentials with { CopilotEndpoint = null },
      credentials with { AccessToken = null },
      credentials with { AccessToken = " " },
      credentials with { CopilotEndpoint = new Uri("https://attacker.invalid/") }
    }) {
      ProviderOperationException error = Assert.Throws<ProviderOperationException>(() => GitHubCopilotAuthentication.GetEndpoint(invalid));
      Assert.Equal(ProviderFailure.InvalidProviderResponse, error.Failure);
    }
  }

  private static async Task<ProviderAuthentication> CompleteWithMetadataAsync(HttpResponseMessage metadata, int? expires = null) {
    var clock = new DeviceClock();
    var paths = new List<string>();
    using var client = Client((request, _) => {
      string path = request.RequestUri!.AbsolutePath;
      paths.Add(path);
      switch (path) {
        case "/login/device/code": return Task.FromResult(Device());
        case "/login/oauth/access_token": return Task.FromResult(OAuthToken(expires));
        case "/user":
          AssertGitHubRequest(request, "https://api.github.com/user");
          return Task.FromResult(Account());
        case "/copilot_internal/user":
          AssertGitHubRequest(request, "https://api.github.com/copilot_internal/user");
          return Task.FromResult(metadata);
        default: throw new InvalidOperationException("Unexpected request.");
      }
    });
    var authentication = new GitHubCopilotAuthentication(client, clientId, clock);
    GitHubCopilotDeviceFlow flow = await authentication.BeginAsync(CancellationToken.None);
    Task<ProviderAuthentication?> completion = authentication.CompleteAsync(flow, null, CancellationToken.None);
    await clock.WaitForDelayAsync(2);
    clock.Advance(2);
    try {
      return (await completion.WaitAsync(TestTimeout))!;
    } finally {
      Assert.Equal(["/login/device/code", "/login/oauth/access_token", "/user", "/copilot_internal/user"], paths);
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task CatalogRefreshIsJoinedAndDoesNotUndoSignIn(bool cancel) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var gate = new AsyncLock();
    var clock = new DeviceClock();
    using var client = Client((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch {
      "/login/device/code" => Device(),
      "/login/oauth/access_token" => OAuthToken(),
      "/user" => Account(),
      "/copilot_internal/user" => Metadata(),
      _ => throw new InvalidOperationException("Unexpected request.")
    }));
    Guid id = Guid.NewGuid();
    store.Providers.Add(new(id, "GitHub Copilot", ProviderRoute.GitHubCopilot, LlmApi.OpenAIChatCompletions,
        GitHubCopilotAuthentication.DefaultEndpoint, AuthenticationMethod.OAuth));
    TaskCompletionSource refreshing = Signal(), cancelled = Signal(), release = Signal();
    int refreshes = 0;
    using var unusedChatGptTransport = new DelegateHandler((_, _) => throw new InvalidOperationException("Unexpected ChatGPT request."));
    var signIns = new ProviderSignIns(store.Providers, new(unusedChatGptTransport), gate,
        () => throw new InvalidOperationException("Unexpected ChatGPT callback."), _ => Task.CompletedTask,
        new(client, clientId, clock), RefreshAsync);
    try {
      ProviderSignIn first = await signIns.BeginAsync(id).WaitAsync(TestTimeout);
      Assert.Equal(ProviderSignInStatus.Pending, first.Status);
      Assert.Equal(userCode, first.UserCode);
      Assert.DoesNotContain(deviceCode, first.ToString(), StringComparison.Ordinal);
      await clock.WaitForDelayAsync(2);
      clock.Advance(2);
      await refreshing.Task.WaitAsync(TestTimeout);

      ProviderSignIn joined = await signIns.BeginAsync(id).WaitAsync(TestTimeout);
      Assert.Equal(first.Id, joined.Id);
      Assert.Equal(ProviderSignInStatus.Completing, joined.Status);
      Assert.Null(joined.UserCode);
      Assert.Null(joined.AuthorizationUrl);
      Assert.Equal(1, refreshes);
      Assert.Equal(githubToken, store.Providers.Get(id).Credentials!.AccessToken);

      if (cancel) {
        Task<ProviderSignIn> stopping = signIns.CancelAsync(first.Id);
        await cancelled.Task.WaitAsync(TestTimeout);
        Assert.False(stopping.IsCompleted);
        release.TrySetResult();
        ProviderSignIn done = await stopping.WaitAsync(TestTimeout);
        Assert.Equal(ProviderSignInStatus.Completed, done.Status);
        Assert.False(done.CatalogRefreshFailed);
      } else {
        release.TrySetResult();
        ProviderSignIn done = await WaitForCompletionAsync(signIns, first.Id);
        Assert.Equal(ProviderSignInStatus.Completed, done.Status);
        Assert.True(done.CatalogRefreshFailed);
        Assert.Null(done.Error);
      }
      Assert.Equal(1, store.Providers.Get(id).Generation);
      Assert.Equal(githubToken, store.Providers.Get(id).Credentials!.AccessToken);
    } finally {
      release.TrySetResult();
      Task shutdown;
      using (await gate.AcquireAsync())
        shutdown = signIns.Shutdown();
      await shutdown.WaitAsync(TestTimeout);
    }

    async Task<ProviderModelCatalog> RefreshAsync(Guid connectionId, CancellationToken token) {
      Assert.Equal(id, connectionId);
      using var registration = token.Register(() => cancelled.TrySetResult());
      Interlocked.Increment(ref refreshes);
      refreshing.TrySetResult();
      await release.Task.WaitAsync(TestTimeout, CancellationToken.None);
      token.ThrowIfCancellationRequested();
      throw new ProviderOperationException(ProviderFailure.ProviderUnavailable);
    }
  }

  private static async Task<ProviderSignIn> WaitForCompletionAsync(ProviderSignIns signIns, Guid attemptId) {
    using var timeout = new CancellationTokenSource(TestTimeout);
    while (true) {
      ProviderSignIn attempt = await signIns.GetAsync(attemptId, timeout.Token);
      if (attempt.Status != ProviderSignInStatus.Completing)
        return attempt;
      await Task.Delay(10, timeout.Token);
    }
  }

  private static void AssertGitHubRequest(HttpRequestMessage request, string url) {
    Assert.Equal(HttpMethod.Get, request.Method);
    Assert.Equal(url, request.RequestUri!.AbsoluteUri);
    Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
    Assert.Equal(githubToken, request.Headers.Authorization.Parameter);
  }

  private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
      new(new DelegateHandler(respond)) { Timeout = Timeout.InfiniteTimeSpan };

  private static HttpResponseMessage Device(int expires = 60, string verification = verificationUrl) =>
      Json($$"""{"device_code":"{{deviceCode}}","user_code":"{{userCode}}","verification_uri":"{{verification}}","interval":2,"expires_in":{{expires}}}""");
  private static HttpResponseMessage OAuthToken(int? expires = null) => expires is null
      ? Json($$"""{"access_token":"{{githubToken}}","token_type":"bearer"}""")
      : Json(System.Text.Json.JsonSerializer.Serialize(new { access_token = githubToken, token_type = "bearer", expires_in = expires }));
  private static HttpResponseMessage Account() => Json("""{"id":43210,"login":"new-login"}""");
  private static HttpResponseMessage Metadata() => Json("""{"endpoints":{"api":"https://api.business.githubcopilot.com/"}}""");
  private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

  private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(request, cancellationToken);
  }

  private sealed class DeviceClock : TimeProvider {
    private readonly object sync = new();
    private readonly List<Delay> scheduled = [];
    private readonly Channel<TimeSpan> delays = Channel.CreateUnbounded<TimeSpan>();
    private long ticks;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Read(ref ticks));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
      Assert.Equal(Timeout.InfiniteTimeSpan, period);
      lock (sync) {
        var delay = new Delay(callback, state, ticks + dueTime.Ticks);
        scheduled.Add(delay);
        Assert.True(delays.Writer.TryWrite(dueTime));
        return delay;
      }
    }

    internal async Task WaitForDelayAsync(int seconds) {
      using var timeout = new CancellationTokenSource(TestTimeout);
      while (true) {
        if (await delays.Reader.ReadAsync(timeout.Token) == TimeSpan.FromSeconds(seconds))
          return;
      }
    }

    internal void Advance(int seconds) {
      Delay[] ready;
      lock (sync) {
        long now = Interlocked.Add(ref ticks, TimeSpan.FromSeconds(seconds).Ticks);
        ready = scheduled.Where(delay => delay.Deadline <= now).ToArray();
        scheduled.RemoveAll(delay => delay.Deadline <= now);
      }
      foreach (Delay delay in ready)
        delay.Fire();
    }

    private sealed class Delay(TimerCallback callback, object? state, long deadline) : ITimer {
      private TimerCallback? pending = callback;
      internal long Deadline { get; } = deadline;
      internal void Fire() => Interlocked.Exchange(ref pending, null)?.Invoke(state);
      public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
      public void Dispose() => Interlocked.Exchange(ref pending, null);
      public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
  }
}
