namespace Portal.Domain;

/// <summary>
/// An invitation to join an organization. Only a hash of the secret token is stored, so the token
/// cannot be recovered from the database.
/// </summary>
public sealed class Invitation : Entity, ITenantScoped
{
    public Guid OrganizationId { get; set; }

    public required string Email { get; init; }

    public required string NormalizedEmail { get; init; }

    public OrgRole Role { get; init; }

    public required byte[] TokenHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public Guid InvitedByUserId { get; init; }

    public DateTimeOffset? AcceptedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public InvitationStatus GetStatus(DateTimeOffset now) =>
        AcceptedAt is not null ? InvitationStatus.Accepted
        : RevokedAt is not null ? InvitationStatus.Revoked
        : ExpiresAt <= now ? InvitationStatus.Expired
        : InvitationStatus.Pending;
}

public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
    Expired,
}
