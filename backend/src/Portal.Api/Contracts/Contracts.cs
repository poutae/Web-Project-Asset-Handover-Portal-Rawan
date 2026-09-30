using Portal.Domain;

namespace Portal.Api.Contracts;

public sealed record RegisterRequest(string? OrganizationName, string? DisplayName, string? Email, string? Password);

public sealed record LoginRequest(string? Email, string? Password, bool RememberMe = false);

public sealed record CsrfResponse(string Token);

public sealed record OrganizationDto(Guid Id, string Name, string Slug);

public sealed record MeResponse(Guid UserId, string Email, string DisplayName, OrgRole Role, OrganizationDto Organization);

public sealed record MemberDto(Guid Id, string Email, string DisplayName, OrgRole Role);

public sealed record CreateInvitationRequest(string? Email, OrgRole Role);

public sealed record InvitationDto(
    Guid Id, string Email, OrgRole Role, InvitationStatus Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>The secret <see cref="Token"/> is returned exactly once, when the invitation is created.</summary>
public sealed record CreatedInvitationDto(
    Guid Id, string Email, OrgRole Role, DateTimeOffset ExpiresAt, string Token);

public sealed record AcceptInvitationRequest(string? Token, string? DisplayName, string? Password);
