using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Contracts;
using Portal.Api.Security;
using Portal.Domain;
using Portal.Infrastructure.Identity;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Endpoints;

public static partial class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapGet("/csrf", GetCsrfToken);
        group.MapPost("/register", Register).RequireRateLimiting(RateLimits.Auth);
        group.MapPost("/login", Login).RequireRateLimiting(RateLimits.Auth);
        group.MapPost("/logout", Logout).RequireAuthorization();
        group.MapGet("/me", Me).RequireAuthorization();

        return app;
    }

    private static IResult GetCsrfToken(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new CsrfResponse(tokens.RequestToken!));
    }

    private static async Task<IResult> Register(
        RegisterRequest request,
        PortalDbContext db,
        UserManager<AppUser> users,
        SignInManager<AppUser> signInManager,
        TimeProvider clock,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var organizationName = errors.Required("organizationName", request.OrganizationName, 200);
        var displayName = errors.Required("displayName", request.DisplayName, 200);
        var email = errors.Email("email", request.Email);
        if (string.IsNullOrEmpty(request.Password))
        {
            errors.Add("password", "password is required.");
        }

        if (!errors.IsValid)
        {
            return errors.ToResult();
        }

        var normalizedEmail = users.NormalizeEmail(email)!;
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, ct))
        {
            return Results.Problem("An account with this email already exists.", statusCode: StatusCodes.Status409Conflict);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var now = clock.GetUtcNow();
        var organization = new Organization
        {
            Name = organizationName!,
            Slug = await CreateUniqueSlugAsync(db, organizationName!, ct),
            CreatedAt = now,
        };
        db.Organizations.Add(organization);
        await db.SaveChangesAsync(ct);

        var user = NewUser(email!, displayName!, organization.Id, OrgRole.Admin, now);
        var created = await users.CreateAsync(user, request.Password!);
        if (!created.Succeeded)
        {
            return Results.ValidationProblem(ToErrorMap(created));
        }

        await transaction.CommitAsync(ct);
        await signInManager.SignInAsync(user, isPersistent: false);

        return Results.Created("/api/auth/me", BuildMe(user, organization));
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        PortalDbContext db,
        UserManager<AppUser> users,
        SignInManager<AppUser> signInManager,
        CancellationToken ct)
    {
        var invalid = Results.Problem("Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);

        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            return invalid;
        }

        var normalizedEmail = users.NormalizeEmail(request.Email.Trim())!;
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, ct);
        if (user is null)
        {
            // Spend comparable time so response timing does not reveal whether the account exists.
            users.PasswordHasher.HashPassword(new AppUser(), request.Password);
            return invalid;
        }

        if (await users.IsLockedOutAsync(user))
        {
            return invalid;
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return invalid;
        }

        await users.ResetAccessFailedCountAsync(user);
        await signInManager.SignInAsync(user, request.RememberMe);

        var organization = await db.Organizations.IgnoreQueryFilters().SingleAsync(o => o.Id == user.OrganizationId, ct);
        return Results.Ok(BuildMe(user, organization));
    }

    private static async Task<IResult> Logout(SignInManager<AppUser> signInManager)
    {
        await signInManager.SignOutAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> Me(
        ClaimsPrincipal principal, PortalDbContext db, UserManager<AppUser> users, CancellationToken ct)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        // Tenant-filtered query: the organization is only visible for the caller's own tenant.
        var organization = await db.Organizations.SingleOrDefaultAsync(o => o.Id == user.OrganizationId, ct);
        return organization is null ? Results.Unauthorized() : Results.Ok(BuildMe(user, organization));
    }

    internal static AppUser NewUser(string email, string displayName, Guid organizationId, OrgRole role, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        UserName = email,
        Email = email,
        DisplayName = displayName,
        OrganizationId = organizationId,
        Role = role,
        CreatedAt = now,
    };

    internal static Dictionary<string, string[]> ToErrorMap(IdentityResult result) =>
        result.Errors
            .GroupBy(e => e.Code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "account")
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());

    internal static MeResponse BuildMe(AppUser user, Organization organization) => new(
        user.Id,
        user.Email!,
        user.DisplayName,
        user.Role,
        new OrganizationDto(organization.Id, organization.Name, organization.Slug));

    private static async Task<string> CreateUniqueSlugAsync(PortalDbContext db, string name, CancellationToken ct)
    {
        var baseSlug = NonSlugCharacters().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (baseSlug.Length == 0)
        {
            baseSlug = "org";
        }

        baseSlug = baseSlug[..Math.Min(baseSlug.Length, 80)];

        var slug = baseSlug;
        while (await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Slug == slug, ct))
        {
            slug = $"{baseSlug}-{Guid.NewGuid().ToString("N")[..6]}";
        }

        return slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();
}
