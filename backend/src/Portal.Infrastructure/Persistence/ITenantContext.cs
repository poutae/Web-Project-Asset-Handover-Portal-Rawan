namespace Portal.Infrastructure.Persistence;

/// <summary>Identifies the organization the current operation runs for. Null means "no tenant".</summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }

    /// <summary>
    /// Runs the current scope as the given organization until the returned handle is disposed. Only for
    /// trusted server-side flows that have already authenticated the tenant by other means (for example
    /// redeeming an invitation token, or a background job that belongs to a tenant).
    /// </summary>
    IDisposable Use(Guid organizationId);
}

/// <summary>Design-time only: there is never a tenant.</summary>
public sealed class NullTenantContext : ITenantContext
{
    public Guid? OrganizationId => null;

    public IDisposable Use(Guid organizationId) =>
        throw new NotSupportedException("The null tenant context cannot switch tenants.");
}
