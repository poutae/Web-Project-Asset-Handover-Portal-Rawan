using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Portal.Api;
using Portal.Api.Concurrency;
using Portal.Api.Deployments;
using Portal.Api.Endpoints;
using Portal.Api.Maintenance;
using Portal.Api.Projects;
using Portal.Api.Realtime;
using Portal.Api.Security;
using Portal.Domain;
using Portal.Infrastructure.Deployments;
using Portal.Infrastructure.Identity;
using Portal.Infrastructure.Persistence;
using Portal.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

var isProduction = builder.Environment.IsProduction();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ProjectAccess>();
builder.Services.AddScoped<IRealtimePublisher, RealtimePublisher>();
builder.Services.AddSignalR();

builder.Services.Configure<DeployOptions>(builder.Configuration.GetSection(DeployOptions.SectionName));
builder.Services.PostConfigure<DeployOptions>(deploy =>
{
    if (string.IsNullOrWhiteSpace(deploy.Root))
    {
        deploy.Root = Path.Combine(builder.Environment.ContentRootPath, "deployments");
    }
});
builder.Services.AddDataProtection()
    .SetApplicationName("Portal")
    .PersistKeysToFileSystem(new DirectoryInfo(
        builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keys ? keys : Path.Combine(builder.Environment.ContentRootPath, "keys")));
builder.Services.AddSingleton<IAccessTokenProtector, AccessTokenProtector>();
builder.Services.AddSingleton<DeploymentLayout>();
builder.Services.AddSingleton<IBuildSandbox>(serviceProvider =>
{
    var deploy = serviceProvider.GetRequiredService<IOptions<DeployOptions>>().Value;
    return deploy.Sandbox switch
    {
        "local-unsafe" => new LocalProcessSandbox(),
        "systemd" => new SystemdRunSandbox(deploy.SudoHelperPath),
        var other => throw new InvalidOperationException($"Deploy:Sandbox '{other}' is not supported (use 'systemd' or 'local-unsafe')."),
    };
});
builder.Services.AddHttpClient(StaticSiteDeploymentProvider.HealthClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddSingleton<IDeploymentProvider, StaticSiteDeploymentProvider>();
builder.Services.AddScoped<DeploymentPipeline>();
builder.Services.AddScoped<DeploymentContext>();
builder.Services.AddSingleton<JobSignal>();
builder.Services.AddHostedService<DeploymentWorker>();
builder.Services.Configure<CleanupOptions>(builder.Configuration.GetSection(CleanupOptions.SectionName));
builder.Services.AddSingleton<CleanupRunner>();
builder.Services.AddHostedService<CleanupWorker>();
if (builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("Frontend:DevServer:Enabled"))
{
    builder.Services.AddSingleton<IHostedService, Portal.Api.DevTools.FrontendDevServer>();
}

builder.Services.AddSingleton<IFileStorage>(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var root = configuration["Storage:Root"] is { Length: > 0 } configured
        ? configured
        : Path.Combine(serviceProvider.GetRequiredService<IHostEnvironment>().ContentRootPath, "storage");
    return new LocalFileStorage(root);
});
builder.Services.Configure<FormOptions>(options =>
    options.MultipartBodyLengthLimit = long.MaxValue); // the per-request size feature enforces the real limit
builder.Services.AddDbContext<PortalDbContext>((serviceProvider, options) =>
{
    var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Default")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Default is not configured. Set the ConnectionStrings__Default environment variable (see .env.example).");
    options.UseSqlServer(connectionString);
});

// Enums travel as their names ("Admin", "Internal") in both directions, which is what the frontend uses;
// numbers are still accepted on input.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<PortalExceptionHandler>();

builder.Services.AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        // Length-based policy (NIST SP 800-63B): long passphrases, no arbitrary composition rules.
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<PortalDbContext>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<PortalClaimsFactory>();

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies(options =>
{
    options.ApplicationCookie?.Configure(cookie =>
    {
        cookie.Cookie.Name = isProduction ? "__Host-portal.auth" : "portal.auth";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = isProduction ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
        cookie.SlidingExpiration = true;

        // An API answers 401/403 instead of redirecting to a login page.
        cookie.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        cookie.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
});

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = isProduction ? "__Host-portal.csrf" : "portal.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = isProduction ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(PortalPolicies.OrgAdmin, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(PortalClaims.Role, nameof(OrgRole.Admin)))
    .AddPolicy(PortalPolicies.OrgStaff, policy => policy
        .RequireAuthenticatedUser()
        .RequireClaim(PortalClaims.Role, nameof(OrgRole.Admin), nameof(OrgRole.Member)));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimits.Auth, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = context.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimit:AuthPerMinute", 20),
            Window = TimeSpan.FromMinutes(1),
        }));
});

// Only loopback proxies are trusted by default; configure KnownProxies when hosting behind another one.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<PortalDbContext>().Database.MigrateAsync();
}

// The unsafe development sandbox provides no isolation, so it can never run in production.
if (app.Environment.IsProduction()
    && string.Equals(app.Configuration["Deploy:Sandbox"], "local-unsafe", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Deploy:Sandbox=local-unsafe is not allowed in Production.");
}

app.UseForwardedHeaders();
app.UseExceptionHandler();

// Deployed client sites are answered here, before anything portal-specific (cookies, auth, API) runs.
app.UseMiddleware<SiteHostMiddleware>();
// Serve the built frontend (and its service worker) from the API when Frontend:DistPath is configured,
// so the whole portal is a single deployable. Static files must be served BEFORE routing: once routing
// has matched an endpoint (including the SPA fallback below) the static file middleware steps aside.
var frontendFiles = app.Configuration["Frontend:DistPath"] is { Length: > 0 } distPath && Directory.Exists(distPath)
    ? new PhysicalFileProvider(Path.GetFullPath(distPath))
    : null;
if (frontendFiles is not null)
{
    static void CacheHeaders(StaticFileResponseContext context)
    {
        var name = context.File.Name;
        context.Context.Response.Headers.CacheControl = name is "index.html" or "sw.js"
            ? "no-cache"
            : "public, max-age=31536000, immutable";
    }

    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontendFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = frontendFiles, OnPrepareResponse = CacheHeaders });
}

app.UseRouting();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<OriginCheckMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CsrfMiddleware>();
app.UseMiddleware<IdempotencyMiddleware>();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/health/ready", async (PortalDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "database-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapAuthEndpoints();
app.MapOrganizationEndpoints();
app.MapProjectEndpoints();
app.MapMilestoneEndpoints();
app.MapNoteEndpoints();
app.MapDocumentEndpoints();
app.MapDeploymentEndpoints();
app.MapHub<PortalHub>(PortalHub.Path);

// Unknown /api, /hubs, and /assets paths stay 404 instead of falling back to the SPA shell.
if (frontendFiles is not null)
{
    app.MapFallback("{*path:regex(^(?!api/|hubs/|assets/).*$)}", async context =>
    {
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(frontendFiles.GetFileInfo("index.html"));
    });
}

app.Run();

public partial class Program;
