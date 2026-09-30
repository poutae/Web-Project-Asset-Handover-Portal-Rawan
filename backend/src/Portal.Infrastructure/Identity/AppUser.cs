using Microsoft.AspNetCore.Identity;
using Portal.Domain;

namespace Portal.Infrastructure.Identity;

public sealed class AppUser : IdentityUser<Guid>
{
    public Guid OrganizationId { get; set; }

    public OrgRole Role { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
