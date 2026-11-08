using System.Net;
using System.Text.Json;
using static Urd.Backend.Tests.Support.OpenAIStreams;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Providers;

// These fixtures use the real OpenAI SDK and an in-memory HTTP handler, never an account or socket.
public sealed class OpenAIProviderClientTests {
  [Fact]
  public async Task PlanCatalogUsesModelsSlugsVisibilityAndServerOrder() {
    using var handler = new ScriptedOpenAIHandler("""
        {"models":[
          {"slug":"z-model","display_name":"Zed","visibility":"list"},
          {"slug":"hidden","display_name":"Hidden","visibility":"hide"},
          {"slug":"a-model","display_name":"Alpha","visibility":"list"},
          {"slug":"unnamed","visibility":"list"}
        ],"data":[{"id":"not-the-plan-catalog"}]}
        """, "application/json");
    using var http = new HttpClient(handler);
    var connection = Connection(ProviderRoute.ChatGpt, authentication: AuthenticationMethod.OAuth);

    var models = await new OpenAIProviderClient(http).GetModelsAsync(connection, Credentials(), default);

    Assert.Equal([new("z-model", "Zed"), new("a-model", "Alpha"), new ProviderModel("unnamed", "unnamed")], models);
    var request = Assert.Single(handler.Requests);
    Assert.Equal(HttpMethod.Get, request.Method);
    Assert.Equal("/prefix/v1/models", request.Uri.AbsolutePath);
    Assert.Equal("Bearer synthetic-access", request.Authorization);
  }

  [Theory]
  [InlineData(ProviderRoute.OpenAI, AuthenticationMethod.ApiKey)]
  [InlineData(ProviderRoute.Custom, AuthenticationMethod.None)]
  public async Task OrdinaryCatalogUsesTypedDataAndRetainsEndpointPrefix(ProviderRoute route, AuthenticationMethod authentication) {
    using var handler = new ScriptedOpenAIHandler("""
        {"object":"list","data":[
          {"id":"z-model","object":"model","created":1700000000,"owned_by":"fixture"},
          {"id":"a-model","object":"model","created":1700000000,"owned_by":"fixture"}
        ]}
        """, "application/json");
    using var http = new HttpClient(handler);
    var connection = Connection(route, authentication: authentication);
    var client = new OpenAIProviderClient(http);

    var models = await client.GetModelsAsync(connection, Credentials(), default);
    // The SDK adapter borrows rather than disposes the caller's client.
    await client.GetModelsAsync(connection, Credentials(), default);

    Assert.Equal([new("z-model", "z-model"), new ProviderModel("a-model", "a-model")], models);
    Assert.Equal(2, handler.Requests.Count);
    Assert.All(handler.Requests, request => {
      Assert.Equal("/prefix/v1/models", request.Uri.AbsolutePath);
      Assert.Equal(authentication == AuthenticationMethod.None ? null : "Bearer synthetic-key", request.Authorization);
    });
  }

  [Theory]
  [InlineData(ProviderRoute.ChatGpt, "{\"data\":[]}")]
  [InlineData(ProviderRoute.Custom, "{\"models\":[]}")]
  [InlineData(ProviderRoute.Custom, "{\"data\":[{\"object\":\"model\"}]}")]
  [InlineData(ProviderRoute.Custom, "{\"data\":[null]}")]
  public async Task WrongCatalogShapeIsNotASuccessfulEmptyCatalog(ProviderRoute route, string body) {
    using var handler = new ScriptedOpenAIHandler(body, "application/json");
    using var http = new HttpClient(handler);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() =>
        new OpenAIProviderClient(http).GetModelsAsync(Connection(route), null, default));
    Assert.Equal(ProviderFailure.InvalidProviderResponse, error.Failure);
    Assert.Single(handler.Requests);
  }

  [Fact]
  public async Task ResponsesDiagnosticUsesOnePrivateDataFreeStreamAndActualUsage() {
    var usage = new { input_tokens = 8, output_tokens = 2, total_tokens = 10, input_tokens_details = new { cached_tokens = 3 } };
    using var handler = new ScriptedOpenAIHandler(ResponseStream(usage: usage));
    using var http = new HttpClient(handler);
    var connection = Connection(ProviderRoute.ChatGpt, authentication: AuthenticationMethod.OAuth);

    var result = await new OpenAIProviderClient(http).TestModelAsync(connection, Credentials(), "fixture-model", default);

    Assert.Equal(connection.Id, result.ConnectionId);
    Assert.Equal("fixture-model", result.ModelId);
    Assert.Equal("hello", result.Text);
    Assert.False(result.Truncated);
    Assert.Equal(new ProviderUsage(8, 2, 10, 3), result.Usage);
    Assert.Equal(1, handler.StreamDisposals);
    var request = Assert.Single(handler.Requests);
    Assert.Equal(HttpMethod.Post, request.Method);
    Assert.Equal("/prefix/v1/responses", request.Uri.AbsolutePath);
    Assert.Equal("Bearer synthetic-access", request.Authorization);
    using var document = JsonDocument.Parse(request.Body);
    var body = document.RootElement;
    Assert.Equal("fixture-model", body.GetProperty("model").GetString());
    Assert.True(body.GetProperty("stream").GetBoolean());
    Assert.False(body.GetProperty("store").GetBoolean());
    var input = Assert.Single(body.GetProperty("input").EnumerateArray());
    Assert.Equal("user", input.GetProperty("role").GetString());
    Assert.Equal("Reply with one short greeting.", input.GetProperty("content")[0].GetProperty("text").GetString());
    foreach (string absent in new[] { "max_output_tokens", "tools", "previous_response_id", "conversation" })
      Assert.False(body.TryGetProperty(absent, out _));
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task KeylessDiagnosticsSendNoDummyCredential(LlmApi api) {
    using var handler = new ScriptedOpenAIHandler(api == LlmApi.OpenAIResponses ? ResponseStream() : ChatStream());
    using var http = new HttpClient(handler);

    var result = await new OpenAIProviderClient(http).TestModelAsync(Connection(api: api), Credentials(), "fixture-model", default);

    Assert.Equal("hello", result.Text);
    Assert.Null(result.Usage);
    var request = Assert.Single(handler.Requests);
    Assert.Null(request.Authorization);
    Assert.Equal(api == LlmApi.OpenAIResponses ? "/prefix/v1/responses" : "/prefix/v1/chat/completions", request.Uri.AbsolutePath);
    using var document = JsonDocument.Parse(request.Body);
    Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
    Assert.False(document.RootElement.GetProperty("store").GetBoolean());
    Assert.Equal(1, handler.StreamDisposals);
  }

  [Theory]
  [InlineData(null, ProviderFailure.IncompleteResponse)]
  [InlineData("response.failed", ProviderFailure.ProviderUnavailable)]
  [InlineData("response.incomplete", ProviderFailure.IncompleteResponse)]
  [InlineData("error", ProviderFailure.ProviderUnavailable)]
  public async Task PartialResponseAndNonSuccessTerminalsNeverSucceed(string? terminal, ProviderFailure failure) {
    using var handler = new ScriptedOpenAIHandler(ResponseStream(terminal));
    using var http = new HttpClient(handler);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() =>
        new OpenAIProviderClient(http).TestModelAsync(Connection(), null, "fixture-model", default));
    Assert.Equal(failure, error.Failure);
    Assert.Equal(failure.ToString(), error.Message);
    Assert.Null(error.InnerException);
    Assert.Equal(1, handler.StreamDisposals);
  }

  [Fact]
  public async Task CompletedEventMustHaveCompletedStatus() {
    using var handler = new ScriptedOpenAIHandler(ResponseStream(status: "in_progress"));
    using var http = new HttpClient(handler);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() =>
        new OpenAIProviderClient(http).TestModelAsync(Connection(), null, "fixture-model", default));
    Assert.Equal(ProviderFailure.InvalidProviderResponse, error.Failure);
  }

  [Theory]
  [InlineData("response.completed", ProviderFailure.InvalidProviderResponse)]
  [InlineData("response.failed", ProviderFailure.ProviderUnavailable)]
  public async Task FirstCompletedEventDoesNotBypassTheRestOfTheStream(string secondTerminal, ProviderFailure failure) {
    using var handler = new ScriptedOpenAIHandler(ResponseStream() + ResponseStream(secondTerminal));
    using var http = new HttpClient(handler);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() =>
        new OpenAIProviderClient(http).TestModelAsync(Connection(), null, "fixture-model", default));
    Assert.Equal(failure, error.Failure);
    Assert.Equal(1, handler.StreamDisposals);
  }

  [Fact]
  public async Task PreviewIsBoundedButStillConsumesTheTerminalAndCleansUp() {
    using var handler = new ScriptedOpenAIHandler(ResponseStream(text: new string('x', 7000)));
    using var http = new HttpClient(handler);
    var result = await new OpenAIProviderClient(http).TestModelAsync(Connection(), null, "fixture-model", default);
    Assert.Equal(new string('x', 4096), result.Text);
    Assert.True(result.Truncated);
    Assert.True(handler.ReachedEnd.Task.IsCompleted);
    Assert.Equal(1, handler.StreamDisposals);
  }

  [Fact]
  public async Task FullPreviewCannotHideIncompleteTerminal() {
    using var handler = new ScriptedOpenAIHandler(ResponseStream("response.incomplete", new string('x', 7000)));
    using var http = new HttpClient(handler);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() =>
        new OpenAIProviderClient(http).TestModelAsync(Connection(), null, "fixture-model", default));
    Assert.Equal(ProviderFailure.IncompleteResponse, error.Failure);
  }

  [Fact]
  public async Task UsagePreservesMissingCountsInsteadOfSdkDefaultZeros() {
    using var handler = new ScriptedOpenAIHandler(ResponseStream(usage: new { output_tokens = 2, input_tokens_details = new { } }));
    using var http = new HttpClient(handler);
    var result = await new OpenAIProviderClient(http).TestModelAsync(Connection(), null, "fixture-model", default);
    Assert.Equal(new ProviderUsage(null, 2, null, null), result.Usage);
  }

  [Fact]
  public async Task ChatCompletionsReadsTrailingReportedUsage() {
    using var handler = new ScriptedOpenAIHandler(ChatStream(usage: new {
      prompt_tokens = 7, completion_tokens = 2, total_tokens = 9, prompt_tokens_details = new { cached_tokens = 0 }
    }));
    using var http = new HttpClient(handler);
    var result = await new OpenAIProviderClient(http).TestModelAsync(
        Connection(api: LlmApi.OpenAIChatCompletions, authentication: AuthenticationMethod.ApiKey), Credentials(), "fixture-model", default);
    Assert.Equal("hello", result.Text);
    Assert.Equal(new ProviderUsage(7, 2, 9, 0), result.Usage);
    Assert.Equal("Bearer synthetic-key", Assert.Single(handler.Requests).Authorization);
  }

  [Fact]
  public async Task ChatCompletionsAlsoPreservesMissingUsageCounts() {
    using var handler = new ScriptedOpenAIHandler(ChatStream(usage: new { completion_tokens = 2 }));
    using var http = new HttpClient(handler);
    var result = await new OpenAIProviderClient(http).TestModelAsync(
        Connection(api: LlmApi.OpenAIChatCompletions), null, "fixture-model", default);
    Assert.Equal(new ProviderUsage(null, 2, null, null), result.Usage);
  }

  [Theory]
  [InlineData(null, false)]
  [InlineData(null, true)]
  [InlineData("length", true)]
  [InlineData("content_filter", true)]
  [InlineData("tool_calls", true)]
  public async Task ChatRequiresAPositiveSuccessfulFinish(string? finish, bool done) {
    using var handler = new ScriptedOpenAIHandler(ChatStream(finish, done: done));
    using var http = new HttpClient(handler);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() => new OpenAIProviderClient(http).TestModelAsync(
        Connection(api: LlmApi.OpenAIChatCompletions), null, "fixture-model", default));
    Assert.Equal(ProviderFailure.IncompleteResponse, error.Failure);
    Assert.Equal(1, handler.StreamDisposals);
  }

  [Theory]
  [InlineData(true, LlmApi.OpenAIResponses)]
  [InlineData(false, LlmApi.OpenAIResponses)]
  [InlineData(false, LlmApi.OpenAIChatCompletions)]
  public async Task ServiceUnavailableMakesExactlyOneRequestAndSanitizesErrors(bool discovery, LlmApi api) {
    using var handler = new ScriptedOpenAIHandler(
        "{\"error\":{\"message\":\"synthetic-secret-body\",\"type\":\"server_error\"}}", "application/json", HttpStatusCode.ServiceUnavailable);
    using var http = new HttpClient(handler);
    var client = new OpenAIProviderClient(http);
    var connection = Connection(api: api);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(async () => {
      if (discovery) await client.GetModelsAsync(connection, null, default);
      else await client.TestModelAsync(connection, null, "fixture-model", default);
    });
    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.DoesNotContain("synthetic-secret", error.ToString());
    Assert.Null(error.InnerException);
    Assert.Single(handler.Requests);
  }

  [Theory]
  [InlineData(HttpStatusCode.Unauthorized, ProviderFailure.AuthenticationRequired)]
  [InlineData(HttpStatusCode.Forbidden, ProviderFailure.PlanAccessDenied)]
  [InlineData(HttpStatusCode.NotFound, ProviderFailure.DiscoveryUnsupported)]
  public async Task DiscoveryClassifiesProviderErrorsWithoutFallback(HttpStatusCode status, ProviderFailure failure) {
    using var handler = new ScriptedOpenAIHandler("{\"error\":{\"message\":\"synthetic-private\"}}", "application/json", status);
    using var http = new HttpClient(handler);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() =>
        new OpenAIProviderClient(http).GetModelsAsync(Connection(), null, default));
    Assert.Equal(failure, error.Failure);
    Assert.Single(handler.Requests);
    Assert.Null(error.InnerException);
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task CancellationAfterPositiveTerminalStillCancelsAndDisposes(LlmApi api) {
    string body = api == LlmApi.OpenAIResponses ? ResponseStream() : ChatStream(done: false);
    using var handler = new ScriptedOpenAIHandler(body) { HoldAtEnd = true };
    using var http = new HttpClient(handler);
    using var cancellation = new CancellationTokenSource();
    Task<ProviderModelTestResult> request = new OpenAIProviderClient(http).TestModelAsync(
        Connection(api: api), null, "fixture-model", cancellation.Token);
    await handler.ReachedEnd.Task.WaitAsync(TestTimeout);
    Assert.False(request.IsCompleted);
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TestTimeout));
    Assert.Single(handler.Requests);
    Assert.Equal(1, handler.StreamDisposals);
  }

  [Theory]
  [InlineData(LlmApi.OpenAIResponses)]
  [InlineData(LlmApi.OpenAIChatCompletions)]
  public async Task TransportFailureAfterPositiveTerminalIsNotSuccess(LlmApi api) {
    string body = api == LlmApi.OpenAIResponses ? ResponseStream() : ChatStream(done: false);
    using var handler = new ScriptedOpenAIHandler(body) { FailAtEnd = true };
    using var http = new HttpClient(handler);
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() => new OpenAIProviderClient(http).TestModelAsync(
        Connection(api: api), null, "fixture-model", default));
    Assert.Equal(ProviderFailure.ProviderUnavailable, error.Failure);
    Assert.DoesNotContain("private", error.ToString());
    Assert.True(handler.ReachedEnd.Task.IsCompleted);
    Assert.Equal(1, handler.StreamDisposals);
  }

  [Fact]
  public async Task CancellationAndMissingCredentialsDoNotSendRequests() {
    using var handler = new ScriptedOpenAIHandler(ResponseStream());
    using var http = new HttpClient(handler);
    var client = new OpenAIProviderClient(http);
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
        client.TestModelAsync(Connection(), null, "fixture-model", cancellation.Token));
    var error = await Assert.ThrowsAsync<ProviderOperationException>(() => client.GetModelsAsync(
        Connection(authentication: AuthenticationMethod.ApiKey), null, default));
    Assert.Equal(ProviderFailure.AuthenticationRequired, error.Failure);
    Assert.Empty(handler.Requests);
  }

  private static ProviderConnection Connection(ProviderRoute route = ProviderRoute.Custom,
      LlmApi api = LlmApi.OpenAIResponses, AuthenticationMethod authentication = AuthenticationMethod.None) =>
      new(Guid.NewGuid(), "Fixture", route, api, new Uri("https://fixture.invalid/prefix/v1"), authentication);

  private static ProviderCredentials Credentials() => new("synthetic-key", "synthetic-access", null, null, null, []);

}
