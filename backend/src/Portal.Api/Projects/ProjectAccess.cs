using Microsoft.EntityFrameworkCore;
using Portal.Api.Security;
using Portal.Domain;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Projects;

/// <summary>What the caller may do with one project.</summary>
public sealed record ProjectPermissions(Project Project, ProjectRole Role)
{
    /// <summary>Change the project itself and its member list.</summary>
    public bool CanManage => Role == ProjectRole.Lead;

    /// <summary>Change project content such as milestones (staff only).</summary>
    public bool CanEditContent => Role is ProjectRole.Lead or ProjectRole.Contributor;

    public bool IsClient => Role == ProjectRole.Client;
}

/// <summary>
/// The single place that decides who can see a project. Organization admins can reach every project in
/// their tenant; everyone else needs an explicit membership. A project the caller cannot reach is reported
/// as missing, never as forbidden, so its existence is not disclosed.
/// </summary>
public sealed class ProjectAccess(PortalDbContext db, CurrentUser user)
{
    public async Task<ProjectPermissions?> FindAsync(Guid projectId, CancellationToken ct)
    {
        // Tenant-filtered: projects of other organizations never come back.
        var project = await db.Projects.SingleOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
        {
            return null;
        }

        if (user.Role == OrgRole.Admin)
        {
            return new ProjectPermissions(project, ProjectRole.Lead);
        }

        var userId = user.Id;
        var membership = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        return membership is null ? null : new ProjectPermissions(project, membership.Role);
    }

    /// <summary>The project role a given organization role may hold.</summary>
    public static bool IsRoleAllowed(OrgRole orgRole, ProjectRole projectRole) => orgRole == OrgRole.Client
        ? projectRole == ProjectRole.Client
        : projectRole is ProjectRole.Lead or ProjectRole.Contributor;
}
