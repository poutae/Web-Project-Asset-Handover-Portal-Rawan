using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Portal.Infrastructure.Identity;

namespace Portal.Api.Security;

/// <summary>Adds the tenant and role claims to the auth cookie when a user signs in.</summary>
public sealed class PortalClaimsFactory(UserManager<AppUser> userManager, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser>(userManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(PortalClaims.Organization, user.OrganizationId.ToString()));
        identity.AddClaim(new Claim(PortalClaims.Role, user.Role.ToString()));
        return identity;
    }
}
