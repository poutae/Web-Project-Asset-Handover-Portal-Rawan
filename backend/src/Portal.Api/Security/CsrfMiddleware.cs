using Microsoft.AspNetCore.Antiforgery;

namespace Portal.Api.Security;

/// <summary>
/// Requires a valid antiforgery token (<c>X-CSRF-TOKEN</c> header + cookie) on every state-changing
/// API request. Clients fetch a token from <c>GET /api/auth/csrf</c> and must refresh it after signing
/// in or out, because tokens are bound to the current identity.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.Request.Path.StartsWithSegments("/api") && RequiresValidation(context.Request.Method))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new
                {
                    type = "https://httpstatuses.com/400",
                    title = "Invalid or missing CSRF token.",
                    status = StatusCodes.Status400BadRequest,
                });
                return;
            }
        }

        await next(context);
    }

    private static bool RequiresValidation(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));
}
