namespace Portal.Api.Security;

public static class PortalClaims
{
    public const string Organization = "portal:org";

    public const string Role = "portal:role";
}

public static class PortalPolicies
{
    /// <summary>Organization administrators only.</summary>
    public const string OrgAdmin = "OrgAdmin";

    /// <summary>Agency staff: administrators and members (not clients).</summary>
    public const string OrgStaff = "OrgStaff";
}
