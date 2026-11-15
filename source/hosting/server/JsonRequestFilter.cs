using Microsoft.AspNetCore.Http.Features;

namespace Urd.Server;

// Enforces body and JSON content-type rules before route-specific request handling.
internal sealed class JsonRequestFilter : IEndpointFilter {
  public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) {
    HttpContext http = context.HttpContext;
    if (HttpMethods.IsGet(http.Request.Method)
        && http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
      return ValueTask.FromResult<object?>(Results.BadRequest());
    if (HttpMethods.IsPost(http.Request.Method) && !HttpJson.TryPrepare(http.Request))
      return ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
    return next(context);
  }
}
