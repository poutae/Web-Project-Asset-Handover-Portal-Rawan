namespace Portal.Domain;

public enum MilestoneStatus
{
    Planned = 1,
    InProgress = 2,
    Done = 3,
    Blocked = 4,
}

public sealed class Milestone : Entity, ITenantScoped
{
    public Guid OrganizationId { get; set; }

    public Guid ProjectId { get; init; }

    public required string Title { get; set; }

    public string Description { get; set; } = string.Empty;

    public DateOnly? DueDate { get; set; }

    public MilestoneStatus Status { get; set; } = MilestoneStatus.Planned;

    public DateTimeOffset CreatedAt { get; init; }
}
