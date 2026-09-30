using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Api.Realtime;
using Portal.Domain;
using Portal.Infrastructure.Deployments;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Deployments;

/// <summary>
/// Runs one deployment from queued to a definite end: build (or reuse a stored build for a rollback),
/// make it live, then ask the live URL for a real response. If the new build fails that check, the previous
/// release is put back. A deployment only ever ends as Succeeded when the site actually answered.
/// </summary>
public sealed class DeploymentPipeline(
    PortalDbContext db,
    ITenantContext tenant,
    IDeploymentProvider provider,
    IAccessTokenProtector protector,
    IRealtimePublisher realtime,
    IOptions<DeployOptions> options,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<DeploymentPipeline> logger)
{
    private readonly DeployOptions _options = options.Value;

    public async Task RunAsync(Guid organizationId, Guid deploymentId, CancellationToken hostStopping)
    {
        using var tenantScope = tenant.Use(organizationId);

        var deployment = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == deploymentId, CancellationToken.None);
        if (deployment is null || deployment.Status != DeploymentStatus.Queued)
        {
            return;
        }

        var environment = await db.DeploymentEnvironments.AsNoTracking().SingleOrDefaultAsync(e => e.Id == deployment.EnvironmentId, CancellationToken.None);
        if (environment is null)
        {
            await FinishAsync(deployment, DeploymentStatus.Failed, "The environment no longer exists.");
            return;
        }

        if (deployment.CancelRequested)
        {
            await FinishAsync(deployment, DeploymentStatus.Cancelled, "Cancelled before it started.");
            return;
        }

        var token = protector.Unprotect(environment.ProtectedAccessToken);
        await using var log = new DeploymentLogSink(scopes, organizationId, deploymentId, new LogRedactor(token), clock);

        using var buildCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
        await using var watcher = new CancellationWatcher(scopes, organizationId, deploymentId, buildCancellation);
        var site = new SiteTarget(environment.SiteLabel, environment.SpaFallback);
        var activated = false;

        try
        {
            await SetStatusAsync(deployment, DeploymentStatus.Building, startedAt: clock.GetUtcNow());
            log.System($"Deployment {deploymentId.ToString("N")[..8]} started ({deployment.Trigger}).");

            string releaseKey;
            string? commitSha;
            if (deployment.Trigger == DeploymentTrigger.Rollback)
            {
                (releaseKey, commitSha) = await PrepareRollbackAsync(deployment, environment, log);
            }
            else
            {
                (releaseKey, commitSha) = await BuildAsync(deployment, environment, token, log, buildCancellation.Token);
            }

            await db.Deployments.Where(d => d.Id == deploymentId).ExecuteUpdateAsync(
                s => s.SetProperty(d => d.ReleaseKey, releaseKey).SetProperty(d => d.CommitSha, commitSha), CancellationToken.None);

            // Past this point the site is being changed, so a cancel request no longer applies.
            await watcher.StopAsync();
            await SetStatusAsync(deployment, DeploymentStatus.Activating);
            var previous = await PreviousLiveAsync(environment);
            log.System("Making the new release live.");
            await provider.ActivateAsync(site, releaseKey, CancellationToken.None);
            activated = true;
            await SetCurrentAsync(environment.Id, deploymentId);

            await SetStatusAsync(deployment, DeploymentStatus.HealthChecking);
            log.System($"Checking {environment.HealthPath} on the live site.");
            var health = await provider.CheckHealthAsync(site, new HealthCheckSpec(environment.HealthPath, _options.HealthCheckAttempts), CancellationToken.None);

            if (health.Healthy)
            {
                log.System($"Healthy: {health.Detail}");
                await FinishAsync(deployment, DeploymentStatus.Succeeded, null);
                await PruneAsync(environment, log);
                return;
            }

            log.System($"Health check failed: {health.Detail}");
            var restored = await RestorePreviousAsync(environment, site, previous, log);
            await db.Deployments.Where(d => d.Id == deploymentId).ExecuteUpdateAsync(
                s => s.SetProperty(d => d.RevertedToPrevious, restored), CancellationToken.None);
            await FinishAsync(
                deployment,
                DeploymentStatus.Failed,
                $"The new release failed its health check ({health.Detail}). " +
                (restored ? "The previous release was restored." : "There was no previous release, so the site is offline."));
        }
        catch (OperationCanceledException) when (buildCancellation.IsCancellationRequested)
        {
            if (hostStopping.IsCancellationRequested)
            {
                await FinishAsync(deployment, DeploymentStatus.Failed, "Interrupted: the portal was stopped during this deployment.");
            }
            else
            {
                log.System("Cancelled.");
                await FinishAsync(deployment, DeploymentStatus.Cancelled, "Cancelled by a user.");
            }
        }
        catch (DeploymentFailedException ex)
        {
            log.System($"Failed: {ex.Message}");
            if (activated)
            {
                // A problem after the switch (for example while restoring) must not leave a half-live site.
                logger.LogWarning(ex, "Deployment {DeploymentId} failed after activation", deploymentId);
            }

            await FinishAsync(deployment, DeploymentStatus.Failed, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deployment {DeploymentId} crashed", deploymentId);
            log.System("Failed: an unexpected error occurred. The details are in the server log.");
            await FinishAsync(deployment, DeploymentStatus.Failed, "An unexpected error occurred. See the server log.");
        }
    }

    private async Task<(string ReleaseKey, string? CommitSha)> BuildAsync(
        Deployment deployment, DeploymentEnvironment environment, string? token, DeploymentLogSink log, CancellationToken ct)
    {
        var problem = await RepositoryUrlPolicy.ValidateAsync(environment.RepositoryUrl, _options.AllowLocalRepositories, ct);
        if (problem is not null)
        {
            throw new DeploymentFailedException(problem);
        }

        var request = new DeploymentRequest(
            deployment.Id,
            environment.SiteLabel,
            environment.RepositoryUrl,
            token,
            deployment.Ref,
            environment.BuildCommand,
            environment.OutputDirectory);
        var output = await provider.BuildAsync(request, log, ct);
        return (output.ReleaseKey, output.CommitSha);
    }

    private async Task<(string ReleaseKey, string? CommitSha)> PrepareRollbackAsync(
        Deployment deployment, DeploymentEnvironment environment, DeploymentLogSink log)
    {
        var source = deployment.SourceDeploymentId is { } id
            ? await db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, CancellationToken.None)
            : null;
        if (source?.ReleaseKey is null || !provider.ReleaseExists(environment.SiteLabel, source.ReleaseKey))
        {
            throw new DeploymentFailedException("The stored build to roll back to is no longer available. Redeploy that commit instead.");
        }

        log.System($"Rolling back to the build from commit {source.CommitSha?[..Math.Min(12, source.CommitSha.Length)] ?? "unknown"}; no rebuild is needed.");
        return (source.ReleaseKey, source.CommitSha);
    }

    private async Task<Deployment?> PreviousLiveAsync(DeploymentEnvironment environment)
    {
        var currentId = await db.DeploymentEnvironments.Where(e => e.Id == environment.Id).Select(e => e.CurrentDeploymentId).SingleAsync(CancellationToken.None);
        return currentId is { } id ? await db.Deployments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, CancellationToken.None) : null;
    }

    private async Task<bool> RestorePreviousAsync(
        DeploymentEnvironment environment, SiteTarget site, Deployment? previous, DeploymentLogSink log)
    {
        if (previous?.ReleaseKey is { } key && provider.ReleaseExists(environment.SiteLabel, key))
        {
            await provider.ActivateAsync(site, key, CancellationToken.None);
            await SetCurrentAsync(environment.Id, previous.Id);
            log.System("Restored the previous release.");
            return true;
        }

        await provider.DeactivateAsync(site, CancellationToken.None);
        await SetCurrentAsync(environment.Id, null);
        log.System("There is no previous release to restore; the site is now offline.");
        return false;
    }

    private async Task PruneAsync(DeploymentEnvironment environment, DeploymentLogSink log)
    {
        var keep = await db.Deployments.AsNoTracking()
            .Where(d => d.EnvironmentId == environment.Id && d.Status == DeploymentStatus.Succeeded && d.ReleaseKey != null)
            .OrderByDescending(d => d.FinishedAt)
            .Select(d => d.ReleaseKey!)
            .Take(Math.Max(2, _options.KeepReleases))
            .ToListAsync(CancellationToken.None);

        foreach (var release in provider.ListReleases(environment.SiteLabel).Where(r => !keep.Contains(r)))
        {
            await provider.RemoveReleaseAsync(environment.SiteLabel, release, CancellationToken.None);
            log.System($"Removed old release {release[..8]}.");
        }
    }

    private async Task SetCurrentAsync(Guid environmentId, Guid? deploymentId) =>
        await db.DeploymentEnvironments.Where(e => e.Id == environmentId).ExecuteUpdateAsync(
            s => s.SetProperty(e => e.CurrentDeploymentId, deploymentId), CancellationToken.None);

    private async Task SetStatusAsync(Deployment deployment, DeploymentStatus status, DateTimeOffset? startedAt = null)
    {
        await db.Deployments.Where(d => d.Id == deployment.Id).ExecuteUpdateAsync(
            s => s.SetProperty(d => d.Status, status).SetProperty(d => d.StartedAt, d => startedAt ?? d.StartedAt), CancellationToken.None);
        await NotifyAsync(deployment);
    }

    private async Task FinishAsync(Deployment deployment, DeploymentStatus status, string? reason)
    {
        var now = clock.GetUtcNow();
        await db.Deployments.Where(d => d.Id == deployment.Id).ExecuteUpdateAsync(
            s => s.SetProperty(d => d.Status, status).SetProperty(d => d.FinishedAt, now).SetProperty(d => d.FailureReason, reason),
            CancellationToken.None);
        await NotifyAsync(deployment);
    }

    private Task NotifyAsync(Deployment deployment) =>
        realtime.PublishAsync(RealtimeKinds.Deployment, RealtimeActions.Updated, deployment.ProjectId, deployment.Id, null, false, CancellationToken.None);

    /// <summary>Polls for a user's cancel request while a build runs, and stops the build when one arrives.</summary>
    private sealed class CancellationWatcher : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public CancellationWatcher(IServiceScopeFactory scopes, Guid organizationId, Guid deploymentId, CancellationTokenSource cancel)
        {
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token);
                        await using var scope = scopes.CreateAsyncScope();
                        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
                        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
                        using (tenant.Use(organizationId))
                        {
                            if (await db.Deployments.AnyAsync(d => d.Id == deploymentId && d.CancelRequested, _stop.Token))
                            {
                                await cancel.CancelAsync();
                                return;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A failed poll only delays cancellation; it must not disturb the build.
                    }
                }
            });
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            await _loop;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _stop.Dispose();
        }
    }
}
