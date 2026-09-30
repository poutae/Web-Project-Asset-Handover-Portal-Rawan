using Microsoft.EntityFrameworkCore;
using Portal.Api.Concurrency;
using Portal.Api.Contracts;
using Portal.Api.Projects;
using Portal.Api.Security;
using Portal.Domain;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Endpoints;

/// <summary>
/// Project notes. Internal notes are staff-only. Clients see and write only client-visible notes, and may
/// edit or delete only their own. Staff may edit their own notes; project leads may edit or delete any.
/// </summary>
public static class NoteEndpoints
{
    public static IEndpointRouteBuilder MapNoteEndpoints(this IEndpointRouteBuilder app)
    {
        var notes = app.MapGroup("/api/projects/{projectId:guid}/notes")
            .WithTags("Notes")
            .RequireAuthorization()
            .WithMetadata(new RequireIdempotencyKeyAttribute());

        notes.MapGet("/", List);
        notes.MapPost("/", Create);
        notes.MapPut("/{noteId:guid}", Update);
        notes.MapDelete("/{noteId:guid}", Delete);

        return app;
    }

    private static NoteDto ToDto(Note n, string authorName) => new(
        n.Id, n.ProjectId, n.AuthorUserId, authorName, n.Body, n.Visibility, n.CreatedAt, n.UpdatedAt,
        OptimisticConcurrency.ToVersion(n.RowVersion));

    private static async Task<IResult> List(Guid projectId, ProjectAccess access, PortalDbContext db, CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        var query = db.Notes.Where(n => n.ProjectId == projectId);
        if (permissions.IsClient)
        {
            query = query.Where(n => n.Visibility == NoteVisibility.Client);
        }

        var rows = await (
            from n in query
            join u in db.Users on n.AuthorUserId equals u.Id
            orderby n.CreatedAt descending
            select new { Note = n, u.DisplayName }).ToListAsync(ct);

        return Results.Ok(rows.Select(r => ToDto(r.Note, r.DisplayName)));
    }

    private static async Task<IResult> Create(
        Guid projectId,
        SaveNoteRequest request,
        ProjectAccess access,
        PortalDbContext db,
        CurrentUser user,
        TimeProvider clock,
        HttpResponse response,
        CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        var (body, errors) = Validate(request, permissions);
        if (!errors.IsValid)
        {
            return errors.ToResult();
        }

        var now = clock.GetUtcNow();
        var note = new Note
        {
            ProjectId = projectId,
            AuthorUserId = user.Id,
            Body = body!,
            Visibility = request.Visibility,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);

        var authorName = await db.Users.Where(u => u.Id == user.Id).Select(u => u.DisplayName).SingleAsync(ct);
        response.Headers.ETag = OptimisticConcurrency.ToETag(note.RowVersion);
        return Results.Created($"/api/projects/{projectId}/notes/{note.Id}", ToDto(note, authorName));
    }

    private static async Task<IResult> Update(
        Guid projectId,
        Guid noteId,
        SaveNoteRequest request,
        ProjectAccess access,
        PortalDbContext db,
        CurrentUser user,
        TimeProvider clock,
        HttpRequest httpRequest,
        HttpResponse response,
        CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        var note = await FindVisibleNoteAsync(db, permissions, projectId, noteId, ct);
        if (note is null)
        {
            return Results.NotFound();
        }

        if (!CanModify(note, permissions, user))
        {
            return Results.Forbid();
        }

        var (body, errors) = Validate(request, permissions);
        if (!errors.IsValid)
        {
            return errors.ToResult();
        }

        if (!OptimisticConcurrency.TryGetExpectedVersion(httpRequest, out var expected, out var failure))
        {
            return failure;
        }

        var authorName = await db.Users.Where(u => u.Id == note.AuthorUserId).Select(u => u.DisplayName).SingleAsync(ct);

        OptimisticConcurrency.Expect(db, note, expected);
        note.Body = body!;
        note.Visibility = request.Visibility;
        note.UpdatedAt = clock.GetUtcNow();

        var conflict = await OptimisticConcurrency.SaveAsync(db, note, n => ToDto(n, authorName), ct);
        if (conflict is not null)
        {
            return conflict;
        }

        response.Headers.ETag = OptimisticConcurrency.ToETag(note.RowVersion);
        return Results.Ok(ToDto(note, authorName));
    }

    private static async Task<IResult> Delete(
        Guid projectId,
        Guid noteId,
        ProjectAccess access,
        PortalDbContext db,
        CurrentUser user,
        HttpRequest httpRequest,
        CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        var note = await FindVisibleNoteAsync(db, permissions, projectId, noteId, ct);
        if (note is null)
        {
            return Results.NotFound();
        }

        if (!CanModify(note, permissions, user))
        {
            return Results.Forbid();
        }

        if (!OptimisticConcurrency.TryGetExpectedVersion(httpRequest, out var expected, out var failure))
        {
            return failure;
        }

        var authorName = await db.Users.Where(u => u.Id == note.AuthorUserId).Select(u => u.DisplayName).SingleAsync(ct);

        OptimisticConcurrency.Expect(db, note, expected);
        db.Notes.Remove(note);

        return await OptimisticConcurrency.SaveAsync(db, note, n => ToDto(n, authorName), ct) ?? Results.NoContent();
    }

    /// <summary>A client cannot tell an internal note from a missing one.</summary>
    private static Task<Note?> FindVisibleNoteAsync(
        PortalDbContext db, ProjectPermissions permissions, Guid projectId, Guid noteId, CancellationToken ct)
    {
        var query = db.Notes.Where(n => n.Id == noteId && n.ProjectId == projectId);
        if (permissions.IsClient)
        {
            query = query.Where(n => n.Visibility == NoteVisibility.Client);
        }

        return query.SingleOrDefaultAsync(ct);
    }

    private static bool CanModify(Note note, ProjectPermissions permissions, CurrentUser user) =>
        note.AuthorUserId == user.Id || permissions.CanManage;

    private static (string? Body, ValidationErrors Errors) Validate(SaveNoteRequest request, ProjectPermissions permissions)
    {
        var errors = new ValidationErrors();
        var body = errors.Required("body", request.Body, 10000);
        if (!Enum.IsDefined(request.Visibility))
        {
            errors.Add("visibility", "visibility is not valid.");
        }
        else if (permissions.IsClient && request.Visibility != NoteVisibility.Client)
        {
            errors.Add("visibility", "Clients can only write client-visible notes.");
        }

        return (body, errors);
    }
}
