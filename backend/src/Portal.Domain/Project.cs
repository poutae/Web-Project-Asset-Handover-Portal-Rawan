namespace Portal.Domain;

public enum ProjectStatus
{
    Active = 1,
    OnHold = 2,
    Completed = 3,
    Archived = 4,
}

public sealed class Project : Entity, ITenantScoped
{
    public Guid OrganizationId { get; set; }

    public required string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    public ProjectStatus Status { get; set; } = ProjectStatus.Active;

    public DateTimeOffset CreatedAt { get; init; }

    public Guid CreatedByUserId { get; init; }
}
