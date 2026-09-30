using Microsoft.EntityFrameworkCore;
using Portal.Api.Concurrency;
using Portal.Api.Contracts;
using Portal.Api.Projects;
using Portal.Api.Security;
using Portal.Domain;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Endpoints;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/projects")
            .WithTags("Projects")
            .RequireAuthorization()
            .WithMetadata(new RequireIdempotencyKeyAttribute());

        projects.MapGet("/", ListProjects);
        projects.MapPost("/", CreateProject).RequireAuthorization(PortalPolicies.OrgStaff);
        projects.MapGet("/{projectId:guid}", GetProject);
        projects.MapPut("/{projectId:guid}", UpdateProject);

        projects.MapGet("/{projectId:guid}/members", ListMembers);
        projects.MapPost("/{projectId:guid}/members", AddMember);
        projects.MapPut("/{projectId:guid}/members/{userId:guid}", UpdateMember);
        projects.MapDelete("/{projectId:guid}/members/{userId:guid}", RemoveMember);

        return app;
    }

    internal static ProjectDto ToDto(Project project, ProjectRole? myRole) => new(
        project.Id, project.Name, project.Description, project.Status, project.CreatedAt, myRole,
        OptimisticConcurrency.ToVersion(project.RowVersion));

    private static IResult WithETag<T>(HttpResponse response, byte[] rowVersion, Func<T, IResult> result, T value)
    {
        response.Headers.ETag = OptimisticConcurrency.ToETag(rowVersion);
        return result(value);
    }

    private static async Task<IResult> ListProjects(PortalDbContext db, CurrentUser user, CancellationToken ct)
    {
        List<ProjectDto> result;
        if (user.Role == OrgRole.Admin)
        {
            var projects = await db.Projects.OrderBy(p => p.Name).ToListAsync(ct);
            result = projects.Select(p => ToDto(p, ProjectRole.Lead)).ToList();
        }
        else
        {
            var userId = user.Id;
            var rows = await (
                from p in db.Projects
                join m in db.ProjectMembers on p.Id equals m.ProjectId
                where m.UserId == userId
                orderby p.Name
                select new { Project = p, m.Role }).ToListAsync(ct);
            result = rows.Select(r => ToDto(r.Project, r.Role)).ToList();
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateProject(
        CreateProjectRequest request, PortalDbContext db, CurrentUser user, TimeProvider clock, HttpResponse response, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var name = errors.Required("name", request.Name, 200);
        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length > 4000)
        {
            errors.Add("description", "description must be at most 4000 characters.");
        }

        if (!errors.IsValid)
        {
            return errors.ToResult();
        }

        var now = clock.GetUtcNow();
        var project = new Project { Name = name!, Description = description, CreatedAt = now, CreatedByUserId = user.Id };
        db.Projects.Add(project);
        db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = project.Id,
            UserId = user.Id,
            Role = ProjectRole.Lead,
            AddedAt = now,
        });
        await db.SaveChangesAsync(ct);

        response.Headers.ETag = OptimisticConcurrency.ToETag(project.RowVersion);
        return Results.Created($"/api/projects/{project.Id}", ToDto(project, ProjectRole.Lead));
    }

    private static async Task<IResult> GetProject(Guid projectId, ProjectAccess access, HttpResponse response, CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        return WithETag(response, permissions.Project.RowVersion, Results.Ok, ToDto(permissions.Project, permissions.Role));
    }

    private static async Task<IResult> UpdateProject(
        Guid projectId,
        UpdateProjectRequest request,
        ProjectAccess access,
        PortalDbContext db,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        if (!permissions.CanManage)
        {
            return Results.Forbid();
        }

        var errors = new ValidationErrors();
        var name = errors.Required("name", request.Name, 200);
        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length > 4000)
        {
            errors.Add("description", "description must be at most 4000 characters.");
        }

        if (!Enum.IsDefined(request.Status))
        {
            errors.Add("status", "status is not valid.");
        }

        if (!errors.IsValid)
        {
            return errors.ToResult();
        }

        if (!OptimisticConcurrency.TryGetExpectedVersion(httpRequest, out var expected, out var failure))
        {
            return failure;
        }

        var project = permissions.Project;
        OptimisticConcurrency.Expect(db, project, expected);
        project.Name = name!;
        project.Description = description;
        project.Status = request.Status;

        var conflict = await OptimisticConcurrency.SaveAsync(db, project, p => ToDto(p, permissions.Role), ct);
        return conflict ?? WithETag(response, project.RowVersion, Results.Ok, ToDto(project, permissions.Role));
    }

    private static async Task<IResult> ListMembers(Guid projectId, ProjectAccess access, PortalDbContext db, CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        var members = await (
            from m in db.ProjectMembers
            join u in db.Users on m.UserId equals u.Id
            where m.ProjectId == projectId
            orderby u.DisplayName
            select new { Member = m, User = u }).ToListAsync(ct);

        return Results.Ok(members.Select(x => ToMemberDto(x.Member, x.User.DisplayName, x.User.Email!)));
    }

    private static async Task<IResult> AddMember(
        Guid projectId,
        AddProjectMemberRequest request,
        ProjectAccess access,
        PortalDbContext db,
        ITenantContext tenant,
        TimeProvider clock,
        CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        if (!permissions.CanManage)
        {
            return Results.Forbid();
        }

        if (!Enum.IsDefined(request.Role))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["role is not valid."] });
        }

        // Users are not tenant-filtered, so the organization check here is explicit and mandatory.
        var organizationId = tenant.OrganizationId;
        var target = await db.Users.SingleOrDefaultAsync(u => u.Id == request.UserId && u.OrganizationId == organizationId, ct);
        if (target is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["userId"] = ["No such user in your organization."] });
        }

        if (!ProjectAccess.IsRoleAllowed(target.Role, request.Role))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["role"] = [target.Role == OrgRole.Client ? "Clients can only join as Client." : "Staff can only join as Lead or Contributor."],
            });
        }

        if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == request.UserId, ct))
        {
            return Results.Problem("This user is already a member of the project.", statusCode: StatusCodes.Status409Conflict);
        }

        var member = new ProjectMember { ProjectId = projectId, UserId = target.Id, Role = request.Role, AddedAt = clock.GetUtcNow() };
        db.ProjectMembers.Add(member);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/projects/{projectId}/members/{target.Id}", ToMemberDto(member, target.DisplayName, target.Email!));
    }

    private static async Task<IResult> UpdateMember(
        Guid projectId,
        Guid userId,
        UpdateProjectMemberRequest request,
        ProjectAccess access,
        PortalDbContext db,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        if (!permissions.CanManage)
        {
            return Results.Forbid();
        }

        var member = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        if (member is null)
        {
            return Results.NotFound();
        }

        var target = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (!Enum.IsDefined(request.Role) || !ProjectAccess.IsRoleAllowed(target.Role, request.Role))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["That role is not allowed for this user."] });
        }

        if (!OptimisticConcurrency.TryGetExpectedVersion(httpRequest, out var expected, out var failure))
        {
            return failure;
        }

        OptimisticConcurrency.Expect(db, member, expected);
        member.Role = request.Role;

        var conflict = await OptimisticConcurrency.SaveAsync(db, member, m => ToMemberDto(m, target.DisplayName, target.Email!), ct);
        return conflict ?? WithETag(response, member.RowVersion, Results.Ok, ToMemberDto(member, target.DisplayName, target.Email!));
    }

    private static async Task<IResult> RemoveMember(
        Guid projectId,
        Guid userId,
        ProjectAccess access,
        PortalDbContext db,
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        if (!permissions.CanManage)
        {
            return Results.Forbid();
        }

        var member = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, ct);
        if (member is null)
        {
            return Results.NotFound();
        }

        if (!OptimisticConcurrency.TryGetExpectedVersion(httpRequest, out var expected, out var failure))
        {
            return failure;
        }

        OptimisticConcurrency.Expect(db, member, expected);
        db.ProjectMembers.Remove(member);

        var target = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var conflict = await OptimisticConcurrency.SaveAsync(db, member, m => ToMemberDto(m, target.DisplayName, target.Email!), ct);
        return conflict ?? Results.NoContent();
    }

    private static ProjectMemberDto ToMemberDto(ProjectMember member, string displayName, string email) => new(
        member.UserId, displayName, email, member.Role, OptimisticConcurrency.ToVersion(member.RowVersion));
}
