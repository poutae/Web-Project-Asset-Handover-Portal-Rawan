using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Concurrency;
using Portal.Api.Contracts;
using Portal.Api.Documents;
using Portal.Api.Projects;
using Portal.Api.Realtime;
using Portal.Api.Security;
using Portal.Domain;
using Portal.Infrastructure.Persistence;
using Portal.Infrastructure.Storage;

namespace Portal.Api.Endpoints;

/// <summary>
/// Project documents and assets. Bytes go to <see cref="IFileStorage"/> under a random key; downloads are
/// always authorized against the project and sent as attachments, never rendered by the browser.
/// Internal documents are staff-only. Clients see and upload only client-visible ones, and manage only
/// their own. Uploaders manage their own uploads; project leads manage all.
/// </summary>
public static class DocumentEndpoints
{
    public const long DefaultMaxUploadBytes = 25L * 1024 * 1024;
    private const long MultipartOverheadBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var documents = app.MapGroup("/api/projects/{projectId:guid}/documents")
            .WithTags("Documents")
            .RequireAuthorization()
            .WithMetadata(new RequireIdempotencyKeyAttribute());

        documents.MapGet("/", List);
        documents.MapPost("/", Upload).WithMetadata(new RequireIdempotencyKeyAttribute { HashBody = false });
        documents.MapGet("/{documentId:guid}/download", Download);
        documents.MapPut("/{documentId:guid}", Update);
        documents.MapDelete("/{documentId:guid}", Delete);

        return app;
    }

    internal static long MaxUploadBytes(IConfiguration configuration) =>
        configuration.GetValue("Storage:MaxUploadBytes", DefaultMaxUploadBytes);

    private static DocumentDto ToDto(Document d, string uploaderName) => new(
        d.Id, d.ProjectId, d.Title, d.FileName, d.ContentType, d.SizeBytes, Convert.ToHexStringLower(d.Sha256),
        d.Visibility, d.UploadedByUserId, uploaderName, d.CreatedAt, d.UpdatedAt, OptimisticConcurrency.ToVersion(d.RowVersion));

    private static Task<string> UploaderNameAsync(PortalDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(ct);

    private static async Task<IResult> List(Guid projectId, ProjectAccess access, PortalDbContext db, CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        var query = db.Documents.Where(d => d.ProjectId == projectId);
        if (permissions.IsClient)
        {
            query = query.Where(d => d.Visibility == DocumentVisibility.Client);
        }

        var rows = await (
            from d in query
            join u in db.Users on d.UploadedByUserId equals u.Id
            orderby d.CreatedAt descending
            select new { Document = d, u.DisplayName }).ToListAsync(ct);

        return Results.Ok(rows.Select(r => ToDto(r.Document, r.DisplayName)));
    }

    private static async Task<IResult> Upload(
        Guid projectId,
        HttpContext context,
        ProjectAccess access,
        PortalDbContext db,
        IFileStorage storage,
        IConfiguration configuration,
        CurrentUser user,
        IRealtimePublisher realtime,
        TimeProvider clock,
        ILogger<Document> logger,
        CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        var maxBytes = MaxUploadBytes(configuration);
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes + MultipartOverheadBytes;
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.Problem("Send the file as multipart/form-data.", statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            return Results.Problem($"The upload is too large or malformed. The limit is {maxBytes / (1024 * 1024)} MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        if (form.Files.Count != 1 || form.Files["file"] is not { } file)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Send exactly one file in the 'file' field."] });
        }

        if (file.Length == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The file is empty."] });
        }

        if (file.Length > maxBytes)
        {
            return Results.Problem($"The file is larger than {maxBytes / (1024 * 1024)} MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var fileName = FileTypePolicy.SanitizeFileName(file.FileName);
        var visibility = DocumentVisibility.Internal;
        if (form.TryGetValue("visibility", out var rawVisibility) && rawVisibility.Count > 0
            && !Enum.TryParse(rawVisibility[0], ignoreCase: true, out visibility))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["visibility"] = ["visibility must be Internal or Client."] });
        }

        if (permissions.IsClient)
        {
            visibility = DocumentVisibility.Client;
        }

        var header = new byte[FileTypePolicy.HeaderLength];
        int headerLength;
        await using (var probe = file.OpenReadStream())
        {
            headerLength = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        }

        var contentType = FileTypePolicy.Validate(fileName, header, headerLength);
        if (contentType is null)
        {
            return Results.Problem(
                $"This file type is not allowed, or its contents do not match its extension. Allowed: {string.Join(", ", FileTypePolicy.AllowedExtensions.Order())}.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        var title = form["title"].ToString().Trim();
        if (title.Length == 0)
        {
            title = Path.GetFileNameWithoutExtension(fileName);
        }

        if (title.Length is 0 or > 200)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["title must be 1-200 characters."] });
        }

        var key = LocalFileStorage.NewKey();
        StoredFile stored;
        try
        {
            await using var content = file.OpenReadStream();
            stored = await storage.SaveAsync(key, content, maxBytes, ct);
        }
        catch (FileTooLargeException)
        {
            return Results.Problem($"The file is larger than {maxBytes / (1024 * 1024)} MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var now = clock.GetUtcNow();
        var document = new Document
        {
            ProjectId = projectId,
            Title = title,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = stored.SizeBytes,
            Sha256 = stored.Sha256,
            StorageKey = key,
            Visibility = visibility,
            UploadedByUserId = user.Id,
            CreatedAt = now,
            UpdatedAt = now,
        };

        try
        {
            db.Documents.Add(document);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Never leave bytes behind that no database row points to.
            await storage.DeleteAsync(key, CancellationToken.None);
            throw;
        }

        await realtime.PublishAsync(
            RealtimeKinds.Document, RealtimeActions.Created, projectId, document.Id, document.RowVersion,
            document.Visibility == DocumentVisibility.Internal, ct);
        logger.LogInformation("Document {DocumentId} uploaded to project {ProjectId} ({Size} bytes)", document.Id, projectId, stored.SizeBytes);

        context.Response.Headers.ETag = OptimisticConcurrency.ToETag(document.RowVersion);
        return Results.Created($"/api/projects/{projectId}/documents/{document.Id}", ToDto(document, await UploaderNameAsync(db, user.Id, ct)));
    }

    private static async Task<IResult> Download(
        Guid projectId,
        Guid documentId,
        ProjectAccess access,
        PortalDbContext db,
        IFileStorage storage,
        HttpResponse response,
        ILogger<Document> logger,
        CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        var document = await FindVisibleAsync(db, permissions, projectId, documentId, ct);
        if (document is null)
        {
            return Results.NotFound();
        }

        Stream stream;
        try
        {
            stream = await storage.OpenReadAsync(document.StorageKey, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogError(ex, "Document {DocumentId} has a database row but no stored file", documentId);
            return Results.Problem("The file is unavailable.", statusCode: StatusCodes.Status404NotFound);
        }

        // Always an attachment; the sandbox CSP and nosniff stop a browser from ever treating it as a page.
        response.Headers.CacheControl = "private, no-store";
        response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
        return Results.File(stream, document.ContentType, document.FileName, enableRangeProcessing: true);
    }

    private static async Task<IResult> Update(
        Guid projectId,
        Guid documentId,
        UpdateDocumentRequest request,
        ProjectAccess access,
        PortalDbContext db,
        CurrentUser user,
        IRealtimePublisher realtime,
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

        var document = await FindVisibleAsync(db, permissions, projectId, documentId, ct);
        if (document is null)
        {
            return Results.NotFound();
        }

        if (!CanModify(document, permissions, user))
        {
            return Results.Forbid();
        }

        var errors = new ValidationErrors();
        var title = errors.Required("title", request.Title, 200);
        if (!Enum.IsDefined(request.Visibility))
        {
            errors.Add("visibility", "visibility is not valid.");
        }
        else if (permissions.IsClient && request.Visibility != DocumentVisibility.Client)
        {
            errors.Add("visibility", "Clients can only use client-visible documents.");
        }

        if (!errors.IsValid)
        {
            return errors.ToResult();
        }

        if (!OptimisticConcurrency.TryGetExpectedVersion(httpRequest, out var expected, out var failure))
        {
            return failure;
        }

        var uploaderName = await UploaderNameAsync(db, document.UploadedByUserId, ct);
        var wasInternal = document.Visibility == DocumentVisibility.Internal;

        OptimisticConcurrency.Expect(db, document, expected);
        document.Title = title!;
        document.Visibility = request.Visibility;
        document.UpdatedAt = clock.GetUtcNow();

        var conflict = await OptimisticConcurrency.SaveAsync(db, document, d => ToDto(d, uploaderName), ct);
        if (conflict is not null)
        {
            return conflict;
        }

        // A document that just stopped being client-visible must still reach clients so they can drop it.
        await realtime.PublishAsync(
            RealtimeKinds.Document, RealtimeActions.Updated, projectId, documentId, document.RowVersion,
            wasInternal && document.Visibility == DocumentVisibility.Internal, ct);
        response.Headers.ETag = OptimisticConcurrency.ToETag(document.RowVersion);
        return Results.Ok(ToDto(document, uploaderName));
    }

    private static async Task<IResult> Delete(
        Guid projectId,
        Guid documentId,
        ProjectAccess access,
        PortalDbContext db,
        IFileStorage storage,
        CurrentUser user,
        IRealtimePublisher realtime,
        HttpRequest httpRequest,
        ILogger<Document> logger,
        CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        var document = await FindVisibleAsync(db, permissions, projectId, documentId, ct);
        if (document is null)
        {
            return Results.NotFound();
        }

        if (!CanModify(document, permissions, user))
        {
            return Results.Forbid();
        }

        if (!OptimisticConcurrency.TryGetExpectedVersion(httpRequest, out var expected, out var failure))
        {
            return failure;
        }

        var uploaderName = await UploaderNameAsync(db, document.UploadedByUserId, ct);
        OptimisticConcurrency.Expect(db, document, expected);
        db.Documents.Remove(document);

        var conflict = await OptimisticConcurrency.SaveAsync(db, document, d => ToDto(d, uploaderName), ct);
        if (conflict is not null)
        {
            return conflict;
        }

        try
        {
            await storage.DeleteAsync(document.StorageKey, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The row is gone, so the file is unreachable; a cleanup job can sweep it later.
            logger.LogWarning(ex, "Could not delete the stored file for document {DocumentId}", documentId);
        }

        await realtime.PublishAsync(
            RealtimeKinds.Document, RealtimeActions.Deleted, projectId, documentId, null,
            document.Visibility == DocumentVisibility.Internal, ct);
        return Results.NoContent();
    }

    /// <summary>A client cannot tell an internal document from a missing one.</summary>
    private static Task<Document?> FindVisibleAsync(
        PortalDbContext db, ProjectPermissions permissions, Guid projectId, Guid documentId, CancellationToken ct)
    {
        var query = db.Documents.Where(d => d.Id == documentId && d.ProjectId == projectId);
        if (permissions.IsClient)
        {
            query = query.Where(d => d.Visibility == DocumentVisibility.Client);
        }

        return query.SingleOrDefaultAsync(ct);
    }

    private static bool CanModify(Document document, ProjectPermissions permissions, CurrentUser user) =>
        document.UploadedByUserId == user.Id || permissions.CanManage;
}
