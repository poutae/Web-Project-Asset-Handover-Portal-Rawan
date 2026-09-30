using Portal.Infrastructure.Persistence;

namespace Portal.Api.Security;

/// <summary>
/// Resolves the tenant from the authenticated principal's claim. The organization id is read only
/// from the server-issued cookie, never from request data.
/// </summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    private Guid? _explicit;

    public Guid? OrganizationId => _explicit ?? FromClaims();

    public IDisposable Use(Guid organizationId)
    {
        var previous = _explicit;
        _explicit = organizationId;
        return new Restore(this, previous);
    }

    private Guid? FromClaims()
    {
        var value = accessor.HttpContext?.User.FindFirst(PortalClaims.Organization)?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private sealed class Restore(HttpTenantContext owner, Guid? previous) : IDisposable
    {
        public void Dispose() => owner._explicit = previous;
    }
}
