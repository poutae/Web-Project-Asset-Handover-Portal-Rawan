namespace Portal.Domain;

/// <summary>Who may read a note.</summary>
public enum NoteVisibility
{
    /// <summary>Agency staff only.</summary>
    Internal = 1,

    /// <summary>Staff and the project's clients.</summary>
    Client = 2,
}

public sealed class Note : Entity, ITenantScoped
{
    public Guid OrganizationId { get; set; }

    public Guid ProjectId { get; init; }

    public Guid AuthorUserId { get; init; }

    public required string Body { get; set; }

    public NoteVisibility Visibility { get; set; } = NoteVisibility.Internal;

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
