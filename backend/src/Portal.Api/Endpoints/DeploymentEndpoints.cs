using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Api.Concurrency;
using Portal.Api.Contracts;
using Portal.Api.Deployments;
using Portal.Api.Projects;
using Portal.Api.Realtime;
using Portal.Api.Security;
using Portal.Domain;
using Portal.Infrastructure.Deployments;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Endpoints;

/// <summary>
/// "Deploy on Our Platform". Everyone on a project can see its environments and deployment status; staff
/// can deploy, redeploy, roll back, and cancel; project leads manage environments. Only staff can read
/// build logs and repository details. Deploying is online-only: a build is started on the server, not queued
/// on the device.
/// </summary>
public static partial class DeploymentEndpoints
{
    private const string DefaultBuildCommand = "npm ci && npm run build";
    private const int ActiveStatusMax = (int)DeploymentStatus.HealthChecking;

    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects/{projectId:guid}/environments")
            .WithTags("Deployments")
            .RequireAuthorization()
            .WithMetadata(new RequireIdempotencyKeyAttribute());

        group.MapGet("/", ListEnvironments);
        group.MapPost("/", CreateEnvironment);
        group.MapPut("/{environmentId:guid}", UpdateEnvironment);
        group.MapDelete("/{environmentId:guid}", DeleteEnvironment);

        group.MapGet("/{environmentId:guid}/deployments", ListDeployments);
        group.MapPost("/{environmentId:guid}/deployments", StartDeployment);
        group.MapGet("/{environmentId:guid}/deployments/{deploymentId:guid}", GetDeployment);
        group.MapGet("/{environmentId:guid}/deployments/{deploymentId:guid}/logs", GetLogs);
        group.MapPost("/{environmentId:guid}/deployments/{deploymentId:guid}/redeploy", Redeploy);
        group.MapPost("/{environmentId:guid}/deployments/{deploymentId:guid}/rollback", Rollback);
        group.MapPost("/{environmentId:guid}/deployments/{deploymentId:guid}/cancel", Cancel);

        return app;
    }

    // ---- environments -------------------------------------------------------------------------------

    private static EnvironmentDto ToDto(DeploymentEnvironment e, ProjectPermissions permissions, DeployOptions options)
    {
        var staff = !permissions.IsClient;
        return new EnvironmentDto(
            e.Id,
            e.Name,
            e.SiteLabel,
            string.IsNullOrWhiteSpace(options.BaseDomain) ? null : $"{options.PublicScheme}://{e.SiteLabel}.{options.BaseDomain.Trim().TrimStart('.')}",
            staff ? e.RepositoryUrl : null,
            staff ? e.Branch : null,
            staff ? e.BuildCommand : null,
            staff ? e.OutputDirectory : null,
            e.HealthPath,
            e.SpaFallback,
            staff && e.ProtectedAccessToken is not null,
            e.CurrentDeploymentId,
            OptimisticConcurrency.ToVersion(e.RowVersion));
    }

    private static async Task<IResult> ListEnvironments(
        Guid projectId, ProjectAccess access, PortalDbContext db, IOptions<DeployOptions> options, CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return Results.NotFound();
        }

        var environments = await db.DeploymentEnvironments.Where(e => e.ProjectId == projectId).OrderBy(e => e.Name).ToListAsync(ct);
        return Results.Ok(environments.Select(e => ToDto(e, permissions, options.Value)));
    }

    private static async Task<IResult> CreateEnvironment(
        Guid projectId,
        SaveEnvironmentRequest request,
        ProjectAccess access,
        PortalDbContext db,
        IAccessTokenProtector protector,
        IOptions<DeployOptions> options,
        TimeProvider clock,
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

        var (values, errors) = Validate(request, options.Value);
        if (values is null)
        {
            return errors.ToResult();
        }

        if (await db.DeploymentEnvironments.AnyAsync(e => e.ProjectId == projectId && e.Name == values.Name, ct))
        {
            return Results.Problem("This project already has an environment with that name.", statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.GetUtcNow();
        var environment = new DeploymentEnvironment
        {
            ProjectId = projectId,
            Name = values.Name,
            SiteLabel = NewSiteLabel(permissions.Project.Name, values.Name),
            RepositoryUrl = values.RepositoryUrl,
            Branch = values.Branch,
            BuildCommand = values.BuildCommand,
            OutputDirectory = values.OutputDirectory,
            HealthPath = values.HealthPath,
            SpaFallback = request.SpaFallback,
            ProtectedAccessToken = string.IsNullOrEmpty(request.AccessToken) ? null : protector.Protect(request.AccessToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.DeploymentEnvironments.Add(environment);
        await db.SaveChangesAsync(ct);

        response.Headers.ETag = OptimisticConcurrency.ToETag(environment.RowVersion);
        return Results.Created($"/api/projects/{projectId}/environments/{environment.Id}", ToDto(environment, permissions, options.Value));
    }

    private static async Task<IResult> UpdateEnvironment(
        Guid projectId,
        Guid environmentId,
        SaveEnvironmentRequest request,
        ProjectAccess access,
        PortalDbContext db,
        IAccessTokenProtector protector,
        IOptions<DeployOptions> options,
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

        if (!permissions.CanManage)
        {
            return Results.Forbid();
        }

        var environment = await db.DeploymentEnvironments.SingleOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId, ct);
        if (environment is null)
        {
            return Results.NotFound();
        }

        var (values, errors) = Validate(request, options.Value);
        if (values is null)
        {
            return errors.ToResult();
        }

        if (await db.DeploymentEnvironments.AnyAsync(e => e.ProjectId == projectId && e.Name == values.Name && e.Id != environmentId, ct))
        {
            return Results.Problem("This project already has an environment with that name.", statusCode: StatusCodes.Status409Conflict);
        }

        if (!OptimisticConcurrency.TryGetExpectedVersion(httpRequest, out var expected, out var failure))
        {
            return failure;
        }

        OptimisticConcurrency.Expect(db, environment, expected);
        environment.Name = values.Name;
        environment.RepositoryUrl = values.RepositoryUrl;
        environment.Branch = values.Branch;
        environment.BuildCommand = values.BuildCommand;
        environment.OutputDirectory = values.OutputDirectory;
        environment.HealthPath = values.HealthPath;
        environment.SpaFallback = request.SpaFallback;
        environment.UpdatedAt = clock.GetUtcNow();
        if (request.ClearAccessToken)
        {
            environment.ProtectedAccessToken = null;
        }
        else if (!string.IsNullOrEmpty(request.AccessToken))
        {
            environment.ProtectedAccessToken = protector.Protect(request.AccessToken);
        }

        var conflict = await OptimisticConcurrency.SaveAsync(db, environment, e => ToDto(e, permissions, options.Value), ct);
        if (conflict is not null)
        {
            return conflict;
        }

        response.Headers.ETag = OptimisticConcurrency.ToETag(environment.RowVersion);
        return Results.Ok(ToDto(environment, permissions, options.Value));
    }

    private static async Task<IResult> DeleteEnvironment(
        Guid projectId,
        Guid environmentId,
        ProjectAccess access,
        PortalDbContext db,
        IDeploymentProvider provider,
        IOptions<DeployOptions> options,
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

        var environment = await db.DeploymentEnvironments.SingleOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId, ct);
        if (environment is null)
        {
            return Results.NotFound();
        }

        if (!OptimisticConcurrency.TryGetExpectedVersion(httpRequest, out var expected, out var failure))
        {
            return failure;
        }

        if (await db.Deployments.AnyAsync(d => d.EnvironmentId == environmentId && (int)d.Status <= ActiveStatusMax, ct))
        {
            return Results.Problem("A deployment is in progress. Cancel it or wait for it to finish first.", statusCode: StatusCodes.Status409Conflict);
        }

        // Deleting the history and the environment happens together, or not at all.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Deployments.Where(d => d.EnvironmentId == environmentId).ExecuteDeleteAsync(ct);
        OptimisticConcurrency.Expect(db, environment, expected);
        db.DeploymentEnvironments.Remove(environment);
        var conflict = await OptimisticConcurrency.SaveAsync(db, environment, e => ToDto(e, permissions, options.Value), ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await transaction.CommitAsync(ct);

        var site = new SiteTarget(environment.SiteLabel, environment.SpaFallback);
        await provider.DeactivateAsync(site, CancellationToken.None);
        foreach (var release in provider.ListReleases(environment.SiteLabel))
        {
            await provider.RemoveReleaseAsync(environment.SiteLabel, release, CancellationToken.None);
        }

        return Results.NoContent();
    }

    // ---- deployments --------------------------------------------------------------------------------

    private static async Task<DeploymentDto> ToDtoAsync(
        Deployment d, DeploymentEnvironment environment, PortalDbContext db, IDeploymentProvider provider, CancellationToken ct)
    {
        var requestedBy = await db.Users.Where(u => u.Id == d.RequestedByUserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct) ?? "Unknown";
        return Map(d, environment, requestedBy, provider);
    }

    private static DeploymentDto Map(Deployment d, DeploymentEnvironment environment, string requestedBy, IDeploymentProvider provider) => new(
        d.Id,
        d.EnvironmentId,
        d.Status,
        d.Trigger,
        d.Ref,
        d.CommitSha,
        d.SourceDeploymentId,
        requestedBy,
        d.CreatedAt,
        d.StartedAt,
        d.FinishedAt,
        d.FailureReason,
        d.RevertedToPrevious,
        environment.CurrentDeploymentId == d.Id,
        d is { Status: DeploymentStatus.Succeeded, ReleaseKey: not null }
            && environment.CurrentDeploymentId != d.Id
            && provider.ReleaseExists(environment.SiteLabel, d.ReleaseKey));

    private static async Task<IResult> ListDeployments(
        Guid projectId, Guid environmentId, ProjectAccess access, PortalDbContext db, IDeploymentProvider provider, CancellationToken ct)
    {
        var (environment, _) = await FindEnvironmentAsync(projectId, environmentId, access, db, ct);
        if (environment is null)
        {
            return Results.NotFound();
        }

        var rows = await (
            from d in db.Deployments
            join u in db.Users on d.RequestedByUserId equals u.Id
            where d.EnvironmentId == environmentId
            orderby d.CreatedAt descending
            select new { Deployment = d, u.DisplayName }).Take(50).ToListAsync(ct);
        return Results.Ok(rows.Select(r => Map(r.Deployment, environment, r.DisplayName, provider)));
    }

    private static async Task<IResult> GetDeployment(
        Guid projectId, Guid environmentId, Guid deploymentId, ProjectAccess access, PortalDbContext db, IDeploymentProvider provider, CancellationToken ct)
    {
        var (environment, _) = await FindEnvironmentAsync(projectId, environmentId, access, db, ct);
        var deployment = environment is null ? null : await db.Deployments.SingleOrDefaultAsync(d => d.Id == deploymentId && d.EnvironmentId == environmentId, ct);
        return deployment is null ? Results.NotFound() : Results.Ok(await ToDtoAsync(deployment, environment!, db, provider, ct));
    }

    private static async Task<IResult> GetLogs(
        Guid projectId, Guid environmentId, Guid deploymentId, long? after, ProjectAccess access, PortalDbContext db, CancellationToken ct)
    {
        var (environment, permissions) = await FindEnvironmentAsync(projectId, environmentId, access, db, ct);
        if (environment is null || permissions is null || permissions.IsClient)
        {
            return Results.NotFound(); // logs can reveal repository details, so clients cannot see them
        }

        var deployment = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deploymentId && d.EnvironmentId == environmentId, ct);
        if (deployment is null)
        {
            return Results.NotFound();
        }

        var cursor = after ?? 0;
        var lines = await db.DeploymentLogLines
            .Where(l => l.DeploymentId == deploymentId && l.Id > cursor)
            .OrderBy(l => l.Id)
            .Take(500)
            .Select(l => new LogLineDto(l.Id, l.At, l.Channel, l.Message))
            .ToListAsync(ct);

        var next = lines.Count > 0 ? lines[^1].Id : cursor;
        var finished = deployment.Status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Cancelled;
        var more = finished && await db.DeploymentLogLines.AnyAsync(l => l.DeploymentId == deploymentId && l.Id > next, ct);
        return Results.Ok(new DeploymentLogsDto(lines, next, finished && !more));
    }

    private static Task<IResult> StartDeployment(
        Guid projectId, Guid environmentId, StartDeploymentRequest request, DeploymentContext c, CancellationToken ct) =>
        CreateDeploymentAsync(projectId, environmentId, c, DeploymentTrigger.Manual, request.Ref, sourceId: null, ct);

    private static async Task<IResult> Redeploy(
        Guid projectId, Guid environmentId, Guid deploymentId, DeploymentContext c, CancellationToken ct)
    {
        var source = await c.Db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deploymentId && d.EnvironmentId == environmentId, ct);
        if (source is null)
        {
            return Results.NotFound();
        }

        if (source.CommitSha is null)
        {
            return Results.Problem("That deployment never fetched a commit, so there is nothing exact to rebuild.", statusCode: StatusCodes.Status409Conflict);
        }

        return await CreateDeploymentAsync(projectId, environmentId, c, DeploymentTrigger.Redeploy, source.CommitSha, source.Id, ct);
    }

    private static async Task<IResult> Rollback(
        Guid projectId, Guid environmentId, Guid deploymentId, DeploymentContext c, CancellationToken ct)
    {
        var (environment, _) = await FindEnvironmentAsync(projectId, environmentId, c.Access, c.Db, ct);
        var source = environment is null ? null : await c.Db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deploymentId && d.EnvironmentId == environmentId, ct);
        if (environment is null || source is null)
        {
            return Results.NotFound();
        }

        if (source.Status != DeploymentStatus.Succeeded || source.ReleaseKey is null)
        {
            return Results.Problem("Only a deployment that succeeded can be rolled back to.", statusCode: StatusCodes.Status409Conflict);
        }

        if (environment.CurrentDeploymentId == source.Id)
        {
            return Results.Problem("That deployment is already live.", statusCode: StatusCodes.Status409Conflict);
        }

        if (!c.Provider.ReleaseExists(environment.SiteLabel, source.ReleaseKey))
        {
            return Results.Problem("The stored build for that deployment has been removed. Redeploy its commit instead.", statusCode: StatusCodes.Status409Conflict);
        }

        return await CreateDeploymentAsync(projectId, environmentId, c, DeploymentTrigger.Rollback, source.CommitSha ?? environment.Branch, source.Id, ct);
    }

    private static async Task<IResult> Cancel(
        Guid projectId, Guid environmentId, Guid deploymentId, DeploymentContext c, CancellationToken ct)
    {
        var (environment, permissions) = await FindEnvironmentAsync(projectId, environmentId, c.Access, c.Db, ct);
        if (environment is null || permissions is null)
        {
            return Results.NotFound();
        }

        if (!permissions.CanEditContent)
        {
            return Results.Forbid();
        }

        var deployment = await c.Db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deploymentId && d.EnvironmentId == environmentId, ct);
        if (deployment is null)
        {
            return Results.NotFound();
        }

        var now = c.Clock.GetUtcNow();
        var cancelledQueued = await c.Db.Deployments
            .Where(d => d.Id == deploymentId && d.Status == DeploymentStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DeploymentStatus.Cancelled)
                .SetProperty(d => d.CancelRequested, true)
                .SetProperty(d => d.FinishedAt, now)
                .SetProperty(d => d.FailureReason, "Cancelled before it started."), ct);
        var requestedRunning = cancelledQueued == 0
            && await c.Db.Deployments
                .Where(d => d.Id == deploymentId && d.Status == DeploymentStatus.Building)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.CancelRequested, true), ct) > 0;

        if (cancelledQueued == 0 && !requestedRunning)
        {
            return Results.Problem("Only a queued or building deployment can be cancelled.", statusCode: StatusCodes.Status409Conflict);
        }

        await c.Realtime.PublishAsync(RealtimeKinds.Deployment, RealtimeActions.Updated, projectId, deploymentId, null, false, ct);
        return Results.Accepted();
    }

    private static async Task<IResult> CreateDeploymentAsync(
        Guid projectId,
        Guid environmentId,
        DeploymentContext c,
        DeploymentTrigger trigger,
        string? requestedRef,
        Guid? sourceId,
        CancellationToken ct)
    {
        var (environment, permissions) = await FindEnvironmentAsync(projectId, environmentId, c.Access, c.Db, ct);
        if (environment is null || permissions is null)
        {
            return Results.NotFound();
        }

        if (!permissions.CanEditContent)
        {
            return Results.Forbid();
        }

        if (string.IsNullOrWhiteSpace(c.Options.Value.BaseDomain))
        {
            return Results.Problem("Site hosting is not configured on this server (Deploy:BaseDomain).", statusCode: StatusCodes.Status409Conflict);
        }

        var gitRef = (string.IsNullOrWhiteSpace(requestedRef) ? environment.Branch : requestedRef).Trim();
        if (!StaticSiteDeploymentProvider.IsValidRef(gitRef))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["ref"] = ["The branch, tag, or commit is not valid."] });
        }

        var now = c.Clock.GetUtcNow();
        var deployment = new Deployment
        {
            ProjectId = projectId,
            EnvironmentId = environmentId,
            Trigger = trigger,
            Ref = gitRef,
            SourceDeploymentId = sourceId,
            RequestedByUserId = c.User.Id,
            CreatedAt = now,
        };
        c.Db.Deployments.Add(deployment);
        c.Db.BackgroundJobs.Add(new BackgroundJob
        {
            Type = JobTypes.Deployment,
            OrganizationId = environment.OrganizationId,
            EntityId = deployment.Id,
            RunAfter = now,
            CreatedAt = now,
        });

        try
        {
            await c.Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The filtered unique index allows only one active deployment per environment.
            return Results.Problem("A deployment is already in progress for this environment.", statusCode: StatusCodes.Status409Conflict);
        }

        c.Signal.Notify();
        await c.Realtime.PublishAsync(RealtimeKinds.Deployment, RealtimeActions.Created, projectId, deployment.Id, null, false, ct);
        var dto = await ToDtoAsync(deployment, environment, c.Db, c.Provider, ct);
        return Results.Accepted($"/api/projects/{projectId}/environments/{environmentId}/deployments/{deployment.Id}", dto);
    }

    private static async Task<(DeploymentEnvironment? Environment, ProjectPermissions? Permissions)> FindEnvironmentAsync(
        Guid projectId, Guid environmentId, ProjectAccess access, PortalDbContext db, CancellationToken ct)
    {
        var permissions = await access.FindAsync(projectId, ct);
        if (permissions is null)
        {
            return (null, null);
        }

        var environment = await db.DeploymentEnvironments.SingleOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId, ct);
        return (environment, permissions);
    }

    // ---- validation ---------------------------------------------------------------------------------

    private sealed record EnvironmentValues(
        string Name, string RepositoryUrl, string Branch, string BuildCommand, string OutputDirectory, string HealthPath);

    private static (EnvironmentValues? Values, ValidationErrors Errors) Validate(SaveEnvironmentRequest request, DeployOptions options)
    {
        var errors = new ValidationErrors();
        var name = errors.Required("name", request.Name, 100);
        var repositoryUrl = request.RepositoryUrl?.Trim();
        if (RepositoryUrlPolicy.ValidateSyntax(repositoryUrl, options.AllowLocalRepositories) is { } urlProblem)
        {
            errors.Add("repositoryUrl", urlProblem);
        }

        var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch.Trim();
        if (!StaticSiteDeploymentProvider.IsValidRef(branch))
        {
            errors.Add("branch", "The branch name is not valid.");
        }

        var buildCommand = string.IsNullOrWhiteSpace(request.BuildCommand) ? DefaultBuildCommand : request.BuildCommand.Trim();
        if (buildCommand.Length > 1000 || buildCommand.Any(c => c is '\r' or '\n' or '\0'))
        {
            errors.Add("buildCommand", "The build command must be a single line of at most 1000 characters.");
        }

        var output = string.IsNullOrWhiteSpace(request.OutputDirectory) ? "dist" : request.OutputDirectory.Trim().Replace('\\', '/').Trim('/');
        if (output.Length is 0 or > 200 || Path.IsPathRooted(output) || output.Split('/').Any(part => part is ".." or "." or "") || output.Any(char.IsControl))
        {
            errors.Add("outputDirectory", "The output directory must be a folder inside the repository, such as dist.");
        }

        var healthPath = string.IsNullOrWhiteSpace(request.HealthPath) ? "/" : request.HealthPath.Trim();
        if (!HealthPathPattern().IsMatch(healthPath))
        {
            errors.Add("healthPath", "The health path must start with / and contain no spaces.");
        }

        if (request.AccessToken is { Length: > 500 })
        {
            errors.Add("accessToken", "The access token is too long.");
        }

        return errors.IsValid
            ? (new EnvironmentValues(name!, repositoryUrl!, branch, buildCommand, output, healthPath), errors)
            : (null, errors);
    }

    private static string NewSiteLabel(string projectName, string environmentName) =>
        $"{Slugify(projectName, 24)}-{Slugify(environmentName, 16)}-{Guid.NewGuid().ToString("N")[..6]}";

    private static string Slugify(string value, int maxLength)
    {
        var slug = NonLabelCharacters().Replace(value.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
        {
            slug = "site";
        }

        return slug[..Math.Min(slug.Length, maxLength)].Trim('-') is { Length: > 0 } trimmed ? trimmed : "site";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonLabelCharacters();

    [GeneratedRegex(@"^/[^\s?#]*(\?[^\s#]*)?$")]
    private static partial Regex HealthPathPattern();
}

/// <summary>The services every deployment action needs, bundled so handlers stay readable.</summary>
public sealed class DeploymentContext(
    ProjectAccess access,
    PortalDbContext db,
    IDeploymentProvider provider,
    IRealtimePublisher realtime,
    JobSignal signal,
    IOptions<DeployOptions> options,
    CurrentUser user,
    TimeProvider clock)
{
    public ProjectAccess Access { get; } = access;

    public PortalDbContext Db { get; } = db;

    public IDeploymentProvider Provider { get; } = provider;

    public IRealtimePublisher Realtime { get; } = realtime;

    public JobSignal Signal { get; } = signal;

    public IOptions<DeployOptions> Options { get; } = options;

    public CurrentUser User { get; } = user;

    public TimeProvider Clock { get; } = clock;
}
