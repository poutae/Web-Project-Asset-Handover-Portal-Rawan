namespace Portal.Domain;

/// <summary>What a member may do inside one project.</summary>
public enum ProjectRole
{
    /// <summary>Staff who manages the project, including its member list.</summary>
    Lead = 1,

    /// <summary>Staff who works on the project.</summary>
    Contributor = 2,

    /// <summary>A client: read-only apart from client-visible notes.</summary>
    Client = 3,
}

public sealed class ProjectMember : Entity, ITenantScoped
{
    public Guid OrganizationId { get; set; }

    public Guid ProjectId { get; init; }

    public Guid UserId { get; init; }

    public ProjectRole Role { get; set; }

    public DateTimeOffset AddedAt { get; init; }
}
