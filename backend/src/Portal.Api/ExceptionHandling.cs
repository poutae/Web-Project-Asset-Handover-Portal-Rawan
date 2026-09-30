using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Portal.Infrastructure.Persistence;

namespace Portal.Api;

/// <summary>Maps well-known infrastructure exceptions to the right HTTP status codes.</summary>
public sealed class PortalExceptionHandler(IProblemDetailsService problems, ILogger<PortalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        (int status, string title)? mapped = exception switch
        {
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict,
                "The resource was modified by someone else. Reload it and try again."),
            TenantViolationException => (StatusCodes.Status403Forbidden, "You do not have access to this resource."),
            _ => null,
        };

        if (mapped is null)
        {
            return false;
        }

        if (exception is TenantViolationException)
        {
            logger.LogWarning(exception, "Blocked a cross-tenant write");
        }

        httpContext.Response.StatusCode = mapped.Value.status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = mapped.Value.status, Title = mapped.Value.title },
        });
    }
}
