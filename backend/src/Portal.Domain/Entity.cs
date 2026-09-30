namespace Portal.Domain;

/// <summary>Base for all persisted entities. <see cref="RowVersion"/> drives optimistic concurrency.</summary>
public abstract class Entity
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// Marks an entity as owned by exactly one organization (tenant). The persistence layer applies a
/// global query filter and rejects cross-tenant writes for every implementer.
/// </summary>
public interface ITenantScoped
{
    Guid OrganizationId { get; set; }
}
