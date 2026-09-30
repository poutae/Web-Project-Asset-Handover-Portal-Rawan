using System.Security.Claims;
using Portal.Domain;

namespace Portal.Api.Security;

/// <summary>The authenticated caller, read from the server-issued auth cookie.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor)
{
    private ClaimsPrincipal Principal => accessor.HttpContext?.User ?? new ClaimsPrincipal();

    public Guid Id => Guid.Parse(Principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("No authenticated user."));

    public OrgRole Role => Enum.Parse<OrgRole>(Principal.FindFirstValue(PortalClaims.Role)
        ?? throw new InvalidOperationException("No authenticated user."));

    public bool IsStaff => Role is OrgRole.Admin or OrgRole.Member;
}
