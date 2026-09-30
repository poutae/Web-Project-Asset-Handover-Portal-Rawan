using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Portal.Infrastructure.Deployments;

namespace Portal.Api.Deployments;

/// <summary>
/// Serves deployed client sites. A request whose host is <c>{site-label}.{BaseDomain}</c> is answered here,
/// straight from that site's live release folder, and never reaches the portal's API, cookies, or pages.
/// Nothing on this path touches the database, so live sites keep working if the database is down.
/// </summary>
public sealed class SiteHostMiddleware(RequestDelegate next, IOptions<DeployOptions> options, DeploymentLayout layout)
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly string _suffix = string.IsNullOrWhiteSpace(options.Value.BaseDomain) ? string.Empty : $".{options.Value.BaseDomain.Trim().TrimStart('.').ToLowerInvariant()}";

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host.ToLowerInvariant();
        if (_suffix.Length == 0 || !host.EndsWith(_suffix, StringComparison.Ordinal) || host.Length == _suffix.Length)
        {
            await next(context);
            return;
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        var label = host[..^_suffix.Length];
        var live = DeploymentLayout.IsValidSiteLabel(label) ? layout.ReadPointer(label) : null;
        if (live is null)
        {
            await NotFoundAsync(context, "This site is not deployed.");
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        using var files = new PhysicalFileProvider(layout.ReleaseDirectory(label, live.ReleaseKey));
        var requested = context.Request.Path.Value ?? "/";
        var file = Resolve(files, requested);
        var status = StatusCodes.Status200OK;

        if (file is null && live.SpaFallback && !Path.HasExtension(requested))
        {
            file = files.GetFileInfo("index.html");
        }

        if (file is not { Exists: true, IsDirectory: false })
        {
            file = files.GetFileInfo("404.html");
            status = StatusCodes.Status404NotFound;
            if (file is not { Exists: true })
            {
                await NotFoundAsync(context, "Not found.");
                return;
            }
        }

        if (!ContentTypes.TryGetContentType(file.Name, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = file.Length;
        context.Response.Headers.CacheControl = contentType.StartsWith("text/html", StringComparison.Ordinal) ? "no-cache" : "public, max-age=300";
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.SendFileAsync(file, context.RequestAborted);
        }
    }

    private static IFileInfo? Resolve(PhysicalFileProvider files, string requested)
    {
        var path = requested.EndsWith('/') ? requested + "index.html" : requested;
        var file = files.GetFileInfo(path);
        if (file.Exists && !file.IsDirectory)
        {
            return file;
        }

        // /about → /about/index.html for multi-page sites.
        var index = files.GetFileInfo(path.TrimEnd('/') + "/index.html");
        return index.Exists ? index : null;
    }

    private static Task NotFoundAsync(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(message);
    }
}
