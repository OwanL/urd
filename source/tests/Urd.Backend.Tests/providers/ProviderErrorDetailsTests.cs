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

  [Fact]
  public async Task HttpTopLevelDetailIsRetained() {
    var error = await RequestFailureAsync("{\"detail\":\"System messages are not allowed\"}",
        "application/json", HttpStatusCode.BadRequest);

    Assert.Contains("HTTP 400", error.Message);
    Assert.Contains("System messages are not allowed", error.Message);
  }

  [Fact]
  public async Task MalformedHttpBodyStillReportsStatus() {
    var error = await RequestFailureAsync("not valid json", "application/json", HttpStatusCode.BadGateway);

    Assert.Contains("HTTP 502", error.Message);
    Assert.DoesNotContain("not valid json", error.Message);
  }

  [Fact]
  public async Task LongHttpMessageIsBounded() {
    string body = System.Text.Json.JsonSerializer.Serialize(new { error = new { message = new string('x', 5000) } });
    var error = await RequestFailureAsync(body, "application/json", HttpStatusCode.BadRequest);

    Assert.Contains("HTTP 400", error.Message);
    Assert.True(error.Message.Length <= 1024);
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
      await foreach (var _ in new ProviderModelClient(http).GetStreamingResponseAsync(
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

  private static async Task<ProviderOperationException> RequestFailureAsync(string body, string contentType, HttpStatusCode status) {
    using var handler = new ScriptedOpenAIHandler(body, contentType, status);
    using var http = new HttpClient(handler);
    return await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      await foreach (var _ in new ProviderModelClient(http).GetStreamingResponseAsync(
          State(), [new ChatMessage(ChatRole.User, "hello")], new ChatOptions { ModelId = "fixture-model" }, default)) {
        Assert.Fail("HTTP rejection must not yield updates.");
      }
    });
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
