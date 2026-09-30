using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Contracts;
using Portal.Api.Security;
using Portal.Domain;
using Portal.Infrastructure.Identity;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Endpoints;

public static class OrganizationEndpoints
{
    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var org = app.MapGroup("/api/org").WithTags("Organization");

        org.MapGet("/members", ListMembers).RequireAuthorization(PortalPolicies.OrgStaff);

        var invitations = org.MapGroup("/invitations").RequireAuthorization(PortalPolicies.OrgAdmin);
        invitations.MapGet("/", ListInvitations);
        invitations.MapPost("/", CreateInvitation);
        invitations.MapDelete("/{id:guid}", RevokeInvitation);

        app.MapPost("/api/invitations/accept", AcceptInvitation)
            .WithTags("Organization")
            .RequireRateLimiting(RateLimits.Auth);

        return app;
    }

    private static async Task<IResult> ListMembers(PortalDbContext db, ITenantContext tenant, CancellationToken ct)
    {
        // Users are looked up cross-tenant by ASP.NET Identity, so this query filters explicitly.
        var organizationId = tenant.OrganizationId;
        var members = await db.Users
            .Where(u => u.OrganizationId == organizationId)
            .OrderBy(u => u.DisplayName)
            .Select(u => new MemberDto(u.Id, u.Email!, u.DisplayName, u.Role))
            .ToListAsync(ct);
        return Results.Ok(members);
    }

    private static async Task<IResult> ListInvitations(PortalDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var rows = await db.Invitations.OrderByDescending(i => i.CreatedAt).Take(200).ToListAsync(ct);
        return Results.Ok(rows.Select(i =>
            new InvitationDto(i.Id, i.Email, i.Role, i.GetStatus(now), i.CreatedAt, i.ExpiresAt)));
    }

    private static async Task<IResult> CreateInvitation(
        CreateInvitationRequest request,
        PortalDbContext db,
        UserManager<AppUser> users,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var email = errors.Email("email", request.Email);
        if (request.Role is not (OrgRole.Member or OrgRole.Client))
        {
            errors.Add("role", "role must be Member or Client.");
        }

        if (!errors.IsValid)
        {
            return errors.ToResult();
        }

        var normalizedEmail = users.NormalizeEmail(email)!;
        var now = clock.GetUtcNow();

        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, ct))
        {
            return Results.Problem("An account with this email already exists.", statusCode: StatusCodes.Status409Conflict);
        }

        var hasPending = await db.Invitations.AnyAsync(
            i => i.NormalizedEmail == normalizedEmail && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, ct);
        if (hasPending)
        {
            return Results.Problem(
                "A pending invitation for this email already exists. Revoke it first.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var secret = RandomNumberGenerator.GetBytes(32);
        var invitation = new Invitation
        {
            Email = email!,
            NormalizedEmail = normalizedEmail,
            Role = request.Role,
            TokenHash = SHA256.HashData(secret),
            CreatedAt = now,
            ExpiresAt = now.Add(InvitationLifetime),
            InvitedByUserId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!),
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/org/invitations/{invitation.Id}",
            new CreatedInvitationDto(invitation.Id, invitation.Email, invitation.Role, invitation.ExpiresAt, WebEncoders.Base64UrlEncode(secret)));
    }

    private static async Task<IResult> RevokeInvitation(Guid id, PortalDbContext db, TimeProvider clock, CancellationToken ct)
    {
        // Tenant-filtered: another organization's invitation is indistinguishable from a missing one.
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == id, ct);
        if (invitation is null)
        {
            return Results.NotFound();
        }

        if (invitation.AcceptedAt is not null)
        {
            return Results.Problem("This invitation was already accepted.", statusCode: StatusCodes.Status409Conflict);
        }

        if (invitation.RevokedAt is null)
        {
            invitation.RevokedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> AcceptInvitation(
        AcceptInvitationRequest request,
        PortalDbContext db,
        UserManager<AppUser> users,
        SignInManager<AppUser> signInManager,
        ITenantContext tenant,
        TimeProvider clock,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var displayName = errors.Required("displayName", request.DisplayName, 200);
        if (string.IsNullOrEmpty(request.Password))
        {
            errors.Add("password", "password is required.");
        }

        if (!errors.IsValid)
        {
            return errors.ToResult();
        }

        var invalid = Results.Problem("This invitation is invalid or has expired.", statusCode: StatusCodes.Status400BadRequest);
        if (string.IsNullOrWhiteSpace(request.Token) || !TryDecodeToken(request.Token, out var secret))
        {
            return invalid;
        }

        var hash = SHA256.HashData(secret);
        var now = clock.GetUtcNow();

        // The token is the credential here, so the lookup deliberately bypasses the tenant filter.
        var invitation = await db.Invitations.IgnoreQueryFilters().SingleOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null || invitation.GetStatus(now) != InvitationStatus.Pending)
        {
            return invalid;
        }

        if (await db.Users.AnyAsync(u => u.NormalizedEmail == invitation.NormalizedEmail, ct))
        {
            return Results.Problem("An account with this email already exists.", statusCode: StatusCodes.Status409Conflict);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Marking the invitation accepted first means a concurrent redemption fails on the row version.
        using (tenant.Use(invitation.OrganizationId))
        {
            invitation.AcceptedAt = now;
            await db.SaveChangesAsync(ct);
        }

        var user = AuthEndpoints.NewUser(invitation.Email, displayName!, invitation.OrganizationId, invitation.Role, now);
        var created = await users.CreateAsync(user, request.Password!);
        if (!created.Succeeded)
        {
            return Results.ValidationProblem(AuthEndpoints.ToErrorMap(created));
        }

        await transaction.CommitAsync(ct);
        await signInManager.SignInAsync(user, isPersistent: false);

        var organization = await db.Organizations.IgnoreQueryFilters().SingleAsync(o => o.Id == user.OrganizationId, ct);
        return Results.Created("/api/auth/me", AuthEndpoints.BuildMe(user, organization));
    }

    private static bool TryDecodeToken(string token, out byte[] secret)
    {
        try
        {
            secret = WebEncoders.Base64UrlDecode(token.Trim());
            return secret.Length == 32;
        }
        catch (FormatException)
        {
            secret = [];
            return false;
        }
    }
}
