using System.Buffers;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using Urd.Core.Sessions;

namespace Urd.Server;

internal static class SessionUserImageRoutes {
  private const int MaxImageBytes = 10 * 1024 * 1024;
  private const string ImageRoute = "/{sessionId:guid}/user-images/{imageId:guid}";

  internal static void MapSessionUserImageRoutes(this RouteGroupBuilder routes, SessionManager sessions) {
    routes.MapPut(ImageRoute, (HttpContext http, Guid sessionId, Guid imageId) =>
        SessionRoutes.WithSessionAsync(http, sessions, sessionId, session => SaveAsync(http, session, imageId)));
    routes.MapGet(ImageRoute, (HttpContext http, Guid sessionId, Guid imageId) =>
        SessionRoutes.WithSessionAsync(http, sessions, sessionId, async session => {
          DataContent? image = await session.ReadUserImageAsync(imageId, http.RequestAborted);
          return image is null ? Results.NotFound() : Results.Bytes(image.Data, image.MediaType);
        }));
  }

  private static async Task<IResult> SaveAsync(HttpContext http, PrimarySession session, Guid imageId) {
    if (imageId == Guid.Empty)
      return Results.BadRequest();
    if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var contentType)
        || (contentType.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
            && !string.Equals(contentType.MediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)))
      return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
    if (http.Request.ContentLength > MaxImageBytes)
      return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

    if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
      bodyLimit.MaxRequestBodySize = MaxImageBytes + 64 * 1024;

    using var body = new MemoryStream((int)(http.Request.ContentLength ?? 0));
    byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    try {
      int count;
      while ((count = await http.Request.Body.ReadAsync(buffer.AsMemory(), http.RequestAborted)) != 0) {
        if (body.Length + count > MaxImageBytes)
          return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        await body.WriteAsync(buffer.AsMemory(0, count), http.RequestAborted);
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
    return Results.Ok(await session.SaveUserImageAsync(imageId, body.ToArray(), http.RequestAborted));
  }
}
