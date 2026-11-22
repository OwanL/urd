using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Urd.Backend.Tests.Support;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

public sealed class HttpErrorLoggingTests {
  [Fact]
  public async Task LogsErrorResponseWithTrustedRouteAndFixedSurfaceOnly() {
    using var dataRoot = new TemporaryDataRoot();
    var log = new ServerErrorLog(dataRoot.Path);
    var context = new DefaultHttpContext();
    context.Request.Path = "/api/sessions/private-path-value";
    context.Request.QueryString = new QueryString("?secret=private-query-value");
    context.Request.Headers["Cookie"] = "private-cookie-value";
    context.Request.Headers["X-Secret"] = "private-header-value";
    context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("private-payload-value"));
    SetRoute(context);
    var middleware = new HttpErrorLogging(_ => {
      context.Response.StatusCode = StatusCodes.Status403Forbidden;
      return Task.CompletedTask;
    }, log, lan: true);

    await middleware.InvokeAsync(context);

    string json = await ReadEntryAsync(dataRoot.Path);
    using var document = JsonDocument.Parse(json);
    JsonElement entry = document.RootElement;
    JsonElement details = entry.GetProperty("details");
    Assert.Equal("http-response-error", entry.GetProperty("event").GetString());
    Assert.Equal("lan", details.GetProperty("surface").GetString());
    Assert.Equal("/api/sessions/{sessionId}", details.GetProperty("route").GetString());
    Assert.Equal(StatusCodes.Status403Forbidden, details.GetProperty("statusCode").GetInt32());
    Assert.DoesNotContain("private-path-value", json);
    Assert.DoesNotContain("private-query-value", json);
    Assert.DoesNotContain("private-cookie-value", json);
    Assert.DoesNotContain("private-header-value", json);
    Assert.DoesNotContain("private-payload-value", json);
    Assert.DoesNotContain("display name", json);
  }

  [Fact]
  public async Task LogsThrownExceptionTypeAndServerStatusWithoutItsMessageThenRethrows() {
    using var dataRoot = new TemporaryDataRoot();
    var log = new ServerErrorLog(dataRoot.Path);
    var context = new DefaultHttpContext();
    context.Request.Path = "/private-path-value";
    SetRoute(context);
    var middleware = new HttpErrorLogging(_ => throw new InvalidOperationException("private-exception-message"), log, lan: false);

    InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
        () => middleware.InvokeAsync(context));

    Assert.Equal("private-exception-message", failure.Message);
    string json = await ReadEntryAsync(dataRoot.Path);
    using var document = JsonDocument.Parse(json);
    JsonElement entry = document.RootElement;
    JsonElement details = entry.GetProperty("details");
    Assert.Equal("http-exception", entry.GetProperty("event").GetString());
    Assert.Equal("local", details.GetProperty("surface").GetString());
    Assert.Equal("/api/sessions/{sessionId}", details.GetProperty("route").GetString());
    Assert.Equal(StatusCodes.Status500InternalServerError, details.GetProperty("statusCode").GetInt32());
    Assert.Equal(nameof(InvalidOperationException), details.GetProperty("exceptionType").GetString());
    Assert.DoesNotContain("private-exception-message", json);
    Assert.DoesNotContain("private-path-value", json);
  }

  [Fact]
  public async Task DoesNotLogExpectedCanceledDisconnectsOrSuccessfulResponses() {
    using var dataRoot = new TemporaryDataRoot();
    var log = new ServerErrorLog(dataRoot.Path);
    using var cancellation = new CancellationTokenSource();
    await cancellation.CancelAsync();
    var canceled = new DefaultHttpContext { RequestAborted = cancellation.Token };
    var canceledMiddleware = new HttpErrorLogging(
        _ => throw new OperationCanceledException(cancellation.Token), log, lan: false);

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledMiddleware.InvokeAsync(canceled));

    var canceledResponse = new DefaultHttpContext { RequestAborted = cancellation.Token };
    var canceledResponseMiddleware = new HttpErrorLogging(_ => {
      canceledResponse.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
      return Task.CompletedTask;
    }, log, lan: false);
    await canceledResponseMiddleware.InvokeAsync(canceledResponse);

    var successful = new DefaultHttpContext();
    var successfulMiddleware = new HttpErrorLogging(_ => {
      successful.Response.StatusCode = StatusCodes.Status204NoContent;
      return Task.CompletedTask;
    }, log, lan: false);
    await successfulMiddleware.InvokeAsync(successful);

    Assert.False(File.Exists(Path.Combine(dataRoot.Path, "errors.jsonl")));
  }

  [Fact]
  public async Task UsesNullRouteAndLocalSurfaceWhenNoRouteEndpointWasSelected() {
    using var dataRoot = new TemporaryDataRoot();
    var log = new ServerErrorLog(dataRoot.Path);
    var context = new DefaultHttpContext();
    context.Request.Path = "/unmatched-private-path";
    var middleware = new HttpErrorLogging(_ => {
      context.Response.StatusCode = StatusCodes.Status404NotFound;
      return Task.CompletedTask;
    }, log, lan: false);

    await middleware.InvokeAsync(context);

    string json = await ReadEntryAsync(dataRoot.Path);
    using var document = JsonDocument.Parse(json);
    JsonElement details = document.RootElement.GetProperty("details");
    Assert.Equal("local", details.GetProperty("surface").GetString());
    Assert.Equal(JsonValueKind.Null, details.GetProperty("route").ValueKind);
    Assert.DoesNotContain("unmatched-private-path", json);
  }

  [Fact]
  public async Task RunningServerLogsRejectedRequestWithRouteTemplate() {
    using var database = new TemporaryDatabase("urd-http-error-log");
    ServerOptions options = RunningServer.OptionsFor(database);
    await using var server = new RunningServer(options);
    await server.WaitForReadyAsync();
    using var response = await server.Client.GetAsync($"/api/sessions/{Guid.NewGuid()}/snapshot?private=value", CancellationToken.None);
    Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    server.Cancel();
    await server.WaitForCompletionAsync();

    string json = await ReadEntryAsync(options.DataRoot);
    using var document = JsonDocument.Parse(json);
    JsonElement details = document.RootElement.GetProperty("details");
    Assert.Equal("/api/sessions/{sessionId}/snapshot", details.GetProperty("route").GetString());
    Assert.Equal(403, details.GetProperty("statusCode").GetInt32());
    Assert.DoesNotContain("private", json);
  }

  private static void SetRoute(HttpContext context) => context.SetEndpoint(new RouteEndpoint(
      _ => Task.CompletedTask,
      RoutePatternFactory.Parse("/api/sessions/{sessionId}"),
      0,
      EndpointMetadataCollection.Empty,
      "private display name"));

  private static Task<string> ReadEntryAsync(string dataRoot) =>
      File.ReadAllTextAsync(Path.Combine(dataRoot, "errors.jsonl"), CancellationToken.None);

  private sealed class TemporaryDataRoot : IDisposable {
    internal string Path { get; } = Directory.CreateTempSubdirectory("urd-http-errors-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
  }
}
