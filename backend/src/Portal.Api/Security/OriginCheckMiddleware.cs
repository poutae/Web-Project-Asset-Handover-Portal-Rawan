namespace Portal.Api.Security;

/// <summary>
/// Rejects realtime connections that come from a different origin than the one serving the API, which
/// blocks cross-site WebSocket hijacking. Non-browser clients send no Origin header and are unaffected;
/// they still need a valid auth cookie.
/// </summary>
public sealed class OriginCheckMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/hubs")
            && context.Request.Headers.Origin.ToString() is { Length: > 0 } origin
            && !IsSameOrigin(origin, context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static bool IsSameOrigin(string origin, HttpRequest request) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && string.Equals(uri.Authority, request.Host.ToString(), StringComparison.OrdinalIgnoreCase);
}
