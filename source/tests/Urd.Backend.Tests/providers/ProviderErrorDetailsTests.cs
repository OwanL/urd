using System.Net;
using Microsoft.Extensions.AI;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Xunit;

#pragma warning disable MEAI001

namespace Urd.Backend.Tests.Providers;

public sealed class ProviderErrorDetailsTests {
  [Fact]
  public async Task HttpProviderMessageIsRetainedAfterKnownCredentialsAreRedacted() {
    const string message = "provider echoed api-key-secret access-token-secret refresh-token-secret id-token-secret nonce-secret";
    var error = await RequestFailureAsync($"{{\"error\":{{\"message\":\"{message}\",\"code\":\"invalid_request\",\"param\":\"input\"}}}}",
        "application/json", HttpStatusCode.BadRequest);

    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Contains("HTTP 400", error.Message);
    Assert.Contains("provider echoed", error.Message);
    Assert.Contains("code=invalid_request", error.Message);
    Assert.Contains("param=input", error.Message);
    Assert.Contains("[redacted]", error.Message);
    Assert.DoesNotContain("api-key-secret", error.Message);
    Assert.DoesNotContain("access-token-secret", error.Message);
    Assert.DoesNotContain("refresh-token-secret", error.Message);
    Assert.DoesNotContain("id-token-secret", error.Message);
    Assert.DoesNotContain("nonce-secret", error.Message);
    Assert.Null(error.InnerException);
  }

  [Theory]
  [InlineData("response.failed")]
  [InlineData("error")]
  public async Task ResponsesStreamErrorMessageIsRetainedAndRedacted(string terminal) {
    string wire = OpenAIStreams.ResponseStream(terminal: terminal)
        .Replace("synthetic-private", "stream echoed api-key-secret", StringComparison.Ordinal);
    using var handler = new ScriptedOpenAIHandler(wire);
    using var http = new HttpClient(handler);

    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var _ in new OpenAIInferenceClient(http).GetStreamingResponseAsync(
          State(), [new ChatMessage(ChatRole.User, "hello")], new ChatOptions { ModelId = "fixture-model" }, default)) {
        Assert.Null(_.FinishReason);
      }
    });

    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Contains("stream echoed", error.Message);
    Assert.Contains("[redacted]", error.Message);
    Assert.DoesNotContain("api-key-secret", error.Message);
    Assert.Null(error.InnerException);
  }

  [Theory]
  [InlineData("{}", true)]
  [InlineData("{\"error\":{\"code\":\"rate_limit_exceeded\"}}", true)]
  [InlineData("{\"error\":{\"code\":\"insufficient_quota\"}}", false)]
  [InlineData("{\"error\":{\"type\":\"insufficient_quota\"}}", false)]
  [InlineData("{\"error\":{\"code\":\"billing_hard_limit_reached\"}}", false)]
  [InlineData("{\"error\":{\"type\":\"usage_limit_reached\"}}", false)]
  public async Task Http429ExcludesKnownQuotaErrors(string body, bool retryable) {
    var error = await RequestFailureAsync(body, "application/json", HttpStatusCode.TooManyRequests, "12");
    Assert.Equal(retryable ? ProviderFailure.RateLimited : ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.Equal(retryable, error.IsRetryableRateLimit);
    Assert.Equal(retryable ? TimeSpan.FromSeconds(12) : null, error.RetryAfter);
  }

  [Theory]
  [InlineData("0", 0)]
  [InlineData("17", 17)]
  [InlineData("-1", null)]
  [InlineData("not-a-date", null)]
  public async Task RetryAfterSecondsAreValidated(string header, int? seconds) {
    var error = await RequestFailureAsync("{}", "application/json", HttpStatusCode.TooManyRequests, header);
    Assert.True(error.IsRetryableRateLimit);
    Assert.Equal(seconds is null ? null : TimeSpan.FromSeconds(seconds.Value), error.RetryAfter);
  }

  [Fact]
  public async Task OversizedRetryAfterIsNotDiscardedAsAnInvalidDelay() {
    var error = await RequestFailureAsync("{}", "application/json", HttpStatusCode.TooManyRequests, "99999999999999999999");
    Assert.Equal(TimeSpan.MaxValue, error.RetryAfter);
  }

  [Theory]
  [InlineData("r")]
  [InlineData("dddd, dd-MMM-yy HH:mm:ss 'GMT'")]
  [InlineData("ddd MMM d HH:mm:ss yyyy")]
  public async Task RetryAfterHttpDateIsSupported(string format) {
    string header = DateTimeOffset.UtcNow.AddMinutes(2).ToString(format, System.Globalization.CultureInfo.InvariantCulture);
    var error = await RequestFailureAsync("{}", "application/json", HttpStatusCode.TooManyRequests, header);
    Assert.InRange(error.RetryAfter!.Value, TimeSpan.FromSeconds(110), TimeSpan.FromMinutes(2));
  }

  [Theory]
  [InlineData("response.failed")]
  [InlineData("error")]
  public async Task StreamRateLimitIsTypedButNotSafeToReplay(string terminal) {
    string wire = OpenAIStreams.ResponseStream(terminal: terminal).Replace("server_error", "rate_limit_exceeded", StringComparison.Ordinal);
    using var handler = new ScriptedOpenAIHandler(wire);
    using var http = new HttpClient(handler);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var _ in new OpenAIInferenceClient(http).GetStreamingResponseAsync(
          State(), [new ChatMessage(ChatRole.User, "hello")], new ChatOptions { ModelId = "fixture-model" }, default)) {
        Assert.Null(_.FinishReason);
      }
    });
    Assert.Equal(ProviderFailure.RateLimited, error.Failure);
    Assert.False(error.IsRetryableRateLimit);
    Assert.Null(error.RetryAfter);
  }

  private static async Task<ProviderOperationException> RequestFailureAsync(string body, string contentType, HttpStatusCode status,
      string? retryAfter = null) {
    using var handler = new ErrorHandler(body, contentType, status, retryAfter);
    using var http = new HttpClient(handler);
    return await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var _ in new OpenAIInferenceClient(http).GetStreamingResponseAsync(
          State(), [new ChatMessage(ChatRole.User, "hello")], new ChatOptions { ModelId = "fixture-model" }, default)) {
        Assert.Fail("HTTP rejection must not yield updates.");
      }
    });
  }

  private sealed class ErrorHandler(string body, string contentType, HttpStatusCode status, string? retryAfter) : HttpMessageHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
      var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new StringContent(body) };
      response.Content.Headers.ContentType = new(contentType);
      if (retryAfter is not null) response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
      return Task.FromResult(response);
    }
  }

  private static ProviderState State() => new(
      new(Guid.NewGuid(), "Fixture", ProviderRoute.Custom, LlmApi.OpenAIResponses,
          new Uri("https://fixture.invalid/v1"), AuthenticationMethod.None),
      1, 1, null,
      new ProviderCredentials("api-key-secret", "access-token-secret", "refresh-token-secret", "id-token-secret",
          null, [], Nonce: "nonce-secret"),
      false, null);
}

#pragma warning restore MEAI001
