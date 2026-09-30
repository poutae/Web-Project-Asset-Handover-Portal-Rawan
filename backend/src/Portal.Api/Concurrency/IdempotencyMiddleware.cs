using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Security;
using Portal.Domain;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Concurrency;

/// <summary>Marks endpoints whose mutations must carry an <c>Idempotency-Key</c> header.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireIdempotencyKeyAttribute : Attribute
{
    /// <summary>
    /// When false the request body is not read or compared (large multipart uploads); the key is then bound
    /// to the method, path, and content length instead.
    /// </summary>
    public bool HashBody { get; init; } = true;
}

/// <summary>
/// Makes retried mutations safe. The first request with a given key runs normally and its response is
/// stored; a retry with the same key and the same request replays that response without re-running the
/// mutation. Reusing a key for a different request is rejected. Keys are scoped per organization and user.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayHeaderName = "Idempotent-Replayed";

    private const int MaxKeyLength = 128;
    private const int MaxBodyBytes = 1024 * 1024;
    private static readonly TimeSpan StaleInProgress = TimeSpan.FromMinutes(2);

    public async Task InvokeAsync(HttpContext context, CurrentUser user, TimeProvider clock)
    {
        if (!RequiresKey(context) || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var key = context.Request.Headers[HeaderName].ToString().Trim();
        if (key.Length == 0 || key.Length > MaxKeyLength || !key.All(IsKeyCharacter))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest,
                $"The {HeaderName} header is required (1-{MaxKeyLength} characters: letters, digits, '-', '_', '.').");
            return;
        }

        var hashBody = context.GetEndpoint()?.Metadata.GetMetadata<RequireIdempotencyKeyAttribute>()?.HashBody ?? true;
        byte[] body;
        if (hashBody)
        {
            var read = await ReadBodyAsync(context);
            if (read is null)
            {
                await WriteProblemAsync(context, StatusCodes.Status413PayloadTooLarge, "The request body is too large.");
                return;
            }

            body = read;
        }
        else
        {
            body = System.Text.Encoding.UTF8.GetBytes($"length:{context.Request.ContentLength}");
        }

        var hash = SHA256.HashData([
            .. System.Text.Encoding.UTF8.GetBytes($"{context.Request.Method}\n{context.Request.Path}{context.Request.QueryString}\n"),
            .. body,
        ]);

        var scopeFactory = context.RequestServices.GetRequiredService<IServiceScopeFactory>();
        var userId = user.Id;
        var now = clock.GetUtcNow();

        var claim = await TryClaimAsync(scopeFactory, userId, key, hash, now, context.RequestAborted);
        if (claim.Existing is { } existing)
        {
            await ReplayOrRejectAsync(context, existing, hash);
            return;
        }

        var recordId = claim.RecordId!.Value;
        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        catch
        {
            context.Response.Body = original;
            await ReleaseAsync(scopeFactory, recordId);
            throw;
        }

        context.Response.Body = original;

        if (context.Response.StatusCode >= 500 || buffer.Length > MaxBodyBytes)
        {
            // A failed attempt must be retryable, and an oversized response cannot be stored.
            await ReleaseAsync(scopeFactory, recordId);
        }
        else
        {
            await CompleteAsync(scopeFactory, recordId, context, buffer.ToArray());
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, context.RequestAborted);
    }

    private static bool RequiresKey(HttpContext context) =>
        !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)
            || HttpMethods.IsOptions(context.Request.Method) || HttpMethods.IsTrace(context.Request.Method))
        && context.GetEndpoint()?.Metadata.GetMetadata<RequireIdempotencyKeyAttribute>() is not null;

    private static bool IsKeyCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        context.Request.EnableBuffering(MaxBodyBytes);
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            if (stream.Length + read > MaxBodyBytes)
            {
                return null;
            }

            stream.Write(buffer, 0, read);
        }

        context.Request.Body.Position = 0;
        return stream.ToArray();
    }

    private static async Task<(Guid? RecordId, IdempotencyRecord? Existing)> TryClaimAsync(
        IServiceScopeFactory scopeFactory, Guid userId, string key, byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

            // Look first: a plain retry finds its record here and never has to trip the unique index.
            var existing = await db.IdempotencyRecords.AsNoTracking()
                .SingleOrDefaultAsync(r => r.UserId == userId && r.Key == key, ct);
            if (existing is not null)
            {
                if (existing.StatusCode is null && now - existing.CreatedAt > StaleInProgress)
                {
                    // The first attempt died without finishing. Clear it so this request can run.
                    await db.IdempotencyRecords.Where(r => r.Id == existing.Id).ExecuteDeleteAsync(ct);
                    continue;
                }

                return (null, existing);
            }

            var record = new IdempotencyRecord { UserId = userId, Key = key, RequestHash = hash, CreatedAt = now };
            db.IdempotencyRecords.Add(record);
            try
            {
                await db.SaveChangesAsync(ct);
                return (record.Id, null);
            }
            catch (DbUpdateException)
            {
                // Two requests with the same key raced; the loser looks the winner's record up next round.
            }
        }

        throw new InvalidOperationException("Could not claim the idempotency key.");
    }

    private static async Task ReplayOrRejectAsync(HttpContext context, IdempotencyRecord existing, byte[] hash)
    {
        if (!existing.RequestHash.AsSpan().SequenceEqual(hash))
        {
            await WriteProblemAsync(context, StatusCodes.Status422UnprocessableEntity,
                $"This {HeaderName} was already used for a different request.");
            return;
        }

        if (existing.StatusCode is null)
        {
            context.Response.Headers.RetryAfter = "1";
            await WriteProblemAsync(context, StatusCodes.Status409Conflict,
                "A request with this key is still being processed. Retry shortly.");
            return;
        }

        context.Response.StatusCode = existing.StatusCode.Value;
        context.Response.Headers[ReplayHeaderName] = "true";
        if (existing.ContentType is not null)
        {
            context.Response.ContentType = existing.ContentType;
        }

        if (existing.Location is not null)
        {
            context.Response.Headers.Location = existing.Location;
        }

        if (existing.ETag is not null)
        {
            context.Response.Headers.ETag = existing.ETag;
        }

        if (existing.ResponseBody is { Length: > 0 } stored)
        {
            await context.Response.Body.WriteAsync(stored, context.RequestAborted);
        }
    }

    private static async Task CompleteAsync(
        IServiceScopeFactory scopeFactory, Guid recordId, HttpContext context, byte[] responseBody)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var statusCode = context.Response.StatusCode;
        var contentType = context.Response.ContentType;
        var location = context.Response.Headers.Location.ToString();
        var etag = context.Response.Headers.ETag.ToString();

        await db.IdempotencyRecords.Where(r => r.Id == recordId).ExecuteUpdateAsync(
            set => set
                .SetProperty(r => r.StatusCode, statusCode)
                .SetProperty(r => r.ContentType, contentType)
                .SetProperty(r => r.Location, location.Length > 0 ? location : null)
                .SetProperty(r => r.ETag, etag.Length > 0 ? etag : null)
                .SetProperty(r => r.ResponseBody, responseBody.Length > 0 ? responseBody : null));
    }

    private static async Task ReleaseAsync(IServiceScopeFactory scopeFactory, Guid recordId)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        await db.IdempotencyRecords.Where(r => r.Id == recordId).ExecuteDeleteAsync();
    }

    private static Task WriteProblemAsync(HttpContext context, int status, string title)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { type = $"https://httpstatuses.com/{status}", title, status });
    }
}
