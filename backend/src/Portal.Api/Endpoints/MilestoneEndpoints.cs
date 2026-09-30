using Microsoft.EntityFrameworkCore;
using Portal.Api.Concurrency;
using Portal.Api.Contracts;
using Portal.Api.Projects;
using Portal.Domain;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Endpoints;

public static class MilestoneEndpoints
{
    public static IEndpointRouteBuilder MapMilestoneEndpoints(this IEndpointRouteBuilder app)
    {
        var milestones = app.MapGroup("/api/projects/{projectId:guid}/milestones")
            .WithTags("Milestones")
            .RequireAuthorization()
            .WithMetadata(new RequireIdempotencyKeyAttribute());

        milestones.MapGet("/", List);
        milestones.MapPost("/", Create);
        milestones.MapPut("/{milestoneId:guid}", Update);
        milestones.MapDelete("/{milestoneId:guid}", Delete);

        return app;
    }

    internal static MilestoneDto ToDto(Milestone m) => new(
        m.Id, m.ProjectId, m.Title, m.Description, m.DueDate, m.Status, m.CreatedAt, OptimisticConcurrency.ToVersion(m.RowVersion));

    private static async Task<IResult> List(Guid projectId, ProjectAccess access, PortalDbContext db, CancellationToken ct)
    {
        if (await access.FindAsync(projectId, ct) is null)
        {
            return Results.NotFound();
        }

        var milestones = await db.Milestones
            .Where(m => m.ProjectId == projectId)
            .OrderBy(m => m.DueDate == null)
            .ThenBy(m => m.DueDate)
            .ThenBy(m => m.CreatedAt)
            .ToListAsync(ct);
        return Results.Ok(milestones.Select(ToDto));
    }

    private static async Task<IResult> Create(
        Guid projectId,
        SaveMilestoneRequest request,
        ProjectAccess access,
        PortalDbContext db,
        TimeProvider clock,
        HttpResponse response,
        CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        if (!permissions.CanEditContent)
        {
            return Results.Forbid();
        }

        var (title, description, errors) = Validate(request);
        if (!errors.IsValid)
        {
            return errors.ToResult();
        }

        var milestone = new Milestone
        {
            ProjectId = projectId,
            Title = title!,
            Description = description,
            DueDate = request.DueDate,
            Status = request.Status,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Milestones.Add(milestone);
        await db.SaveChangesAsync(ct);

        response.Headers.ETag = OptimisticConcurrency.ToETag(milestone.RowVersion);
        return Results.Created($"/api/projects/{projectId}/milestones/{milestone.Id}", ToDto(milestone));
    }

    private static async Task<IResult> Update(
        Guid projectId,
        Guid milestoneId,
        SaveMilestoneRequest request,
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

        if (!permissions.CanEditContent)
        {
            return Results.Forbid();
        }

        var milestone = await db.Milestones.SingleOrDefaultAsync(m => m.Id == milestoneId && m.ProjectId == projectId, ct);
        if (milestone is null)
        {
            return Results.NotFound();
        }

        var (title, description, errors) = Validate(request);
        if (!errors.IsValid)
        {
            return errors.ToResult();
        }

        if (!OptimisticConcurrency.TryGetExpectedVersion(httpRequest, out var expected, out var failure))
        {
            return failure;
        }

        OptimisticConcurrency.Expect(db, milestone, expected);
        milestone.Title = title!;
        milestone.Description = description;
        milestone.DueDate = request.DueDate;
        milestone.Status = request.Status;

        var conflict = await OptimisticConcurrency.SaveAsync(db, milestone, ToDto, ct);
        if (conflict is not null)
        {
            return conflict;
        }

        response.Headers.ETag = OptimisticConcurrency.ToETag(milestone.RowVersion);
        return Results.Ok(ToDto(milestone));
    }

    private static async Task<IResult> Delete(
        Guid projectId,
        Guid milestoneId,
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

        if (!permissions.CanEditContent)
        {
            return Results.Forbid();
        }

        var milestone = await db.Milestones.SingleOrDefaultAsync(m => m.Id == milestoneId && m.ProjectId == projectId, ct);
        if (milestone is null)
        {
            return Results.NotFound();
        }

        if (!OptimisticConcurrency.TryGetExpectedVersion(httpRequest, out var expected, out var failure))
        {
            return failure;
        }

        OptimisticConcurrency.Expect(db, milestone, expected);
        db.Milestones.Remove(milestone);

        return await OptimisticConcurrency.SaveAsync(db, milestone, ToDto, ct) ?? Results.NoContent();
    }

    private static (string? Title, string Description, ValidationErrors Errors) Validate(SaveMilestoneRequest request)
    {
        var errors = new ValidationErrors();
        var title = errors.Required("title", request.Title, 200);
        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length > 4000)
        {
            errors.Add("description", "description must be at most 4000 characters.");
        }

        if (!Enum.IsDefined(request.Status))
        {
            errors.Add("status", "status is not valid.");
        }

        return (title, description, errors);
    }
}
