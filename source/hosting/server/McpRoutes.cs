using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace Urd.Server;

internal sealed record McpConnectRequest(string Url, string Authentication, string? HeaderName, string? Credential) {
  public override string ToString() => "[MCP connection request]";
}

internal static class McpRoutes {
  internal static void MapMcpConnections(this IEndpointRouteBuilder app, McpConnections connections) {
    RouteGroupBuilder routes = app.MapGroup("/api");
    routes.AddEndpointFilter(RequireLocalAsync);
    routes.AddEndpointFilter<JsonRequestFilter>();
    routes.AddEndpointFilter(HandleRequestAsync);

    routes.MapGet("/mcp-servers", () => Results.Ok(connections.List()));
    routes.MapPost("/mcp-servers", async (HttpContext http) => {
      McpConnectRequest request = await http.Request.ReadFromJsonAsync<McpConnectRequest>(http.RequestAborted)
          ?? throw new ArgumentException("A JSON object is required.");
      return Results.Ok(new { id = connections.Start(request) });
    });
    routes.MapPost("/mcp-servers/{id:guid}/reconnect", async (HttpContext http, Guid id) => {
      await HttpJson.RequireEmptyAsync(http.Request, http.RequestAborted);
      return Results.Ok(new { id = connections.Reconnect(id) });
    });
    routes.MapGet("/mcp-operations/{id:guid}", IResult (Guid id) => {
      object? operation = connections.Get(id);
      return operation is null ? Results.NotFound() : Results.Ok(operation);
    });
    routes.MapDelete("/mcp-operations/{id:guid}", IResult (Guid id) =>
        connections.Cancel(id) ? Results.Ok(new { }) : Results.NotFound());
    routes.MapDelete("/mcp-servers/{id:guid}", IResult (Guid id) =>
        connections.Remove(id) ? Results.Ok(new { }) : Results.NotFound());

    app.MapGet("/mcp/auth/callback", (HttpContext http) => CompleteCallback(http, connections));
  }

  private static ValueTask<object?> RequireLocalAsync(
      EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) {
    if (!IsLocal(invocation.HttpContext))
      return ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
    return next(invocation);
  }

  private static bool IsLocal(HttpContext http) {
    IPAddress? peer = http.Connection.RemoteIpAddress;
    if (peer?.IsIPv4MappedToIPv6 == true)
      peer = peer.MapToIPv4();
    return peer is not null && IPAddress.IsLoopback(peer)
        && string.Equals(http.Request.Host.Host, "127.0.0.1", StringComparison.Ordinal);
  }

  private static async ValueTask<object?> HandleRequestAsync(
      EndpointFilterInvocationContext invocation, EndpointFilterDelegate next) {
    try {
      return await next(invocation);
    } catch (KeyNotFoundException) {
      return Results.NotFound();
    } catch (JsonException) {
      return Results.BadRequest();
    } catch (BadHttpRequestException failure) {
      return Results.StatusCode(failure.StatusCode);
    } catch (ArgumentException) {
      return Results.BadRequest();
    } catch (InvalidOperationException) {
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
  }

  private static IResult CompleteCallback(HttpContext http, McpConnections connections) {
    http.Response.Headers.CacheControl = "no-store";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (!IsLocal(http))
      return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
        || http.Request.Query.Any(pair => pair.Value.Count != 1)
        || !http.Request.Query.TryGetValue("state", out var state) || string.IsNullOrEmpty(state[0])
        || !http.Request.Query.TryGetValue("code", out var code) || string.IsNullOrEmpty(code[0]))
      return Results.BadRequest();
    string? issuer = http.Request.Query.TryGetValue("iss", out var iss) ? iss[0] : null;
    try {
      return connections.CompleteCallback(state[0]!, code[0]!, issuer)
          ? Results.Text("Sign-in received. You can close this page.")
          : Results.BadRequest();
    } catch (ArgumentException) {
      return Results.BadRequest();
    }
  }
}
