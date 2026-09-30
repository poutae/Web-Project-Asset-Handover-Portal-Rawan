using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Portal.Api;
using Portal.Api.Endpoints;
using Portal.Api.Security;
using Portal.Domain;
using Portal.Infrastructure.Identity;
using Portal.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

var isProduction = builder.Environment.IsProduction();

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddDbContext<PortalDbContext>((serviceProvider, options) =>
{
    var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Default")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Default is not configured. Set the ConnectionStrings__Default environment variable (see .env.example).");
    options.UseSqlServer(connectionString);
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<PortalExceptionHandler>();

builder.Services.AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
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

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CsrfMiddleware>();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/health/ready", async (PortalDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct)
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "database-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapAuthEndpoints();
app.MapOrganizationEndpoints();

app.Run();

public partial class Program;
