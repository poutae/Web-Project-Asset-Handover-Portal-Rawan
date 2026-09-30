namespace Portal.Domain;

/// <summary>The tenant: an agency that manages projects for its clients.</summary>
public sealed class Organization : Entity
{
    public required string Name { get; set; }

    public required string Slug { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
