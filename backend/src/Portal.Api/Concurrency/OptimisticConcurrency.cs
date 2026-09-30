using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Portal.Domain;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Concurrency;

/// <summary>
/// Optimistic concurrency over HTTP. Every entity is exposed with a strong <c>ETag</c> (its SQL Server
/// <c>rowversion</c>). Updates and deletes must send it back in <c>If-Match</c>; a stale value yields
/// <c>409 Conflict</c> carrying the current server state so the client can present a conflict UI.
/// Last-write-wins is never possible: a missing header is <c>428 Precondition Required</c>.
/// </summary>
public static class OptimisticConcurrency
{
    public static string ToVersion(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static string ToETag(byte[] rowVersion) => $"\"{ToVersion(rowVersion)}\"";

    /// <summary>Reads the <c>If-Match</c> header. On failure, <paramref name="failure"/> is the response to return.</summary>
    public static bool TryGetExpectedVersion(HttpRequest request, out byte[] version, [NotNullWhen(false)] out IResult? failure)
    {
        version = [];
        failure = null;

        var header = request.Headers.IfMatch.ToString().Trim();
        if (header.Length == 0)
        {
            failure = Results.Problem(
                "This request must include an If-Match header with the resource's current version.",
                statusCode: StatusCodes.Status428PreconditionRequired);
            return false;
        }

        if (header.StartsWith("W/", StringComparison.Ordinal))
        {
            header = header[2..];
        }

        header = header.Trim('"');
        try
        {
            version = Convert.FromBase64String(header);
        }
        catch (FormatException)
        {
            version = [];
        }

        if (version.Length == 0)
        {
            failure = Results.Problem("The If-Match header is not a valid version.", statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        return true;
    }

    /// <summary>Tells EF which version the caller believes is current, so a stale write fails.</summary>
    public static void Expect<TEntity>(PortalDbContext db, TEntity entity, byte[] expected)
        where TEntity : Entity =>
        db.Entry(entity).Property(e => e.RowVersion).OriginalValue = expected;

    /// <summary>
    /// Saves changes. Returns null on success; on a concurrency failure returns a 409 (or 404 if the row was
    /// deleted meanwhile) that includes the current state.
    /// </summary>
    public static async Task<IResult?> SaveAsync<TEntity, TDto>(
        PortalDbContext db, TEntity entity, Func<TEntity, TDto> toDto, CancellationToken ct)
        where TEntity : Entity
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            var entry = db.Entry(entity);
            await entry.ReloadAsync(ct);

            if (entry.State == EntityState.Detached)
            {
                return Results.Problem("The resource no longer exists.", statusCode: StatusCodes.Status404NotFound);
            }

            return new ConflictResult<TDto>(toDto(entity), entity.RowVersion);
        }
    }

    public static IResult Conflict<TDto>(TDto current, byte[] rowVersion) => new ConflictResult<TDto>(current, rowVersion);

    public sealed record ConflictBody<TDto>(string Type, string Title, int Status, TDto Current);

    private sealed class ConflictResult<TDto>(TDto current, byte[] rowVersion) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ETag = ToETag(rowVersion);
            var body = new ConflictBody<TDto>(
                "https://httpstatuses.com/409",
                "The resource was modified by someone else. Review the current version and try again.",
                StatusCodes.Status409Conflict,
                current);
            await TypedResults.Json(body, statusCode: StatusCodes.Status409Conflict, contentType: "application/problem+json")
                .ExecuteAsync(httpContext);
        }
    }
}
