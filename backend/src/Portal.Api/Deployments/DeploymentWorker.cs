using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Domain;
using Portal.Infrastructure.Deployments;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Deployments;

public static class JobTypes
{
    public const string Deployment = "deployment";
}

/// <summary>Wakes idle workers as soon as a job is queued instead of waiting for the next poll.</summary>
public sealed class JobSignal : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0);

    public void Notify() => _semaphore.Release();

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);

    public void Dispose() => _semaphore.Dispose();
}

/// <summary>
/// Background worker for the SQL-backed job queue. Jobs are claimed atomically (row locks, skipping rows
/// another worker holds), owned through a renewable lease, and reclaimed if a worker dies. A reclaimed
/// deployment is failed rather than re-run, because half-finished builds are not safe to repeat blindly.
/// </summary>
public sealed class DeploymentWorker(
    IServiceScopeFactory scopes,
    JobSignal signal,
    IOptions<DeployOptions> options,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<DeploymentWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"[..Math.Min(99, 30 + Environment.MachineName.Length)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Deploy:WorkerEnabled", true))
        {
            logger.LogInformation("The deployment worker is disabled (Deploy:WorkerEnabled=false)");
            return;
        }

        var loops = Enumerable.Range(0, Math.Max(1, options.Value.MaxConcurrentBuilds))
            .Select(_ => RunLoopAsync(stoppingToken));
        await Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await ClaimAsync(stoppingToken);
                if (job is null)
                {
                    await signal.WaitAsync(PollInterval, stoppingToken);
                    continue;
                }

                await RunJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The database may be starting up or briefly unavailable; keep the loop alive.
                logger.LogWarning(ex, "The deployment worker hit an error and will retry");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private sealed record ClaimedJob(Guid Id, Guid OrganizationId, Guid EntityId, int Attempts);

    private async Task<ClaimedJob?> ClaimAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH next AS (
                SELECT TOP (1) *
                FROM BackgroundJobs WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Type = @type
                  AND ((Status = 0 AND RunAfter <= @now) OR (Status = 1 AND LeaseExpiresAt < @now))
                ORDER BY RunAfter, CreatedAt)
            UPDATE next
            SET Status = 1, LockedBy = @worker, LeaseExpiresAt = @lease, Attempts = Attempts + 1
            OUTPUT inserted.Id, inserted.OrganizationId, inserted.EntityId, inserted.Attempts;
            """;
        AddParameter(command, "@type", JobTypes.Deployment);
        AddParameter(command, "@now", clock.GetUtcNow());
        AddParameter(command, "@worker", _workerId);
        AddParameter(command, "@lease", clock.GetUtcNow().Add(Lease));

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ClaimedJob(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt32(3))
            : null;
    }

    private async Task RunJobAsync(ClaimedJob job, CancellationToken stoppingToken)
    {
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = RenewLeaseAsync(job.Id, renewal.Token);
        string? error = null;
        try
        {
            if (job.Attempts > 1)
            {
                await FailInterruptedAsync(job);
                error = "The worker stopped while this job was running.";
                return;
            }

            await using var scope = scopes.CreateAsyncScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<DeploymentPipeline>();
            await pipeline.RunAsync(job.OrganizationId, job.EntityId, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Job {JobId} failed", job.Id);
            error = "The job failed unexpectedly.";
        }
        finally
        {
            await renewal.CancelAsync();
            await heartbeat;
            await CompleteAsync(job.Id, error);
        }
    }

    private async Task FailInterruptedAsync(ClaimedJob job)
    {
        await using var scope = scopes.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        using (tenant.Use(job.OrganizationId))
        {
            var now = clock.GetUtcNow();
            await db.Deployments
                .Where(d => d.Id == job.EntityId && d.Status != DeploymentStatus.Succeeded && d.Status != DeploymentStatus.Failed && d.Status != DeploymentStatus.Cancelled)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DeploymentStatus.Failed)
                    .SetProperty(d => d.FinishedAt, now)
                    .SetProperty(d => d.FailureReason, "Interrupted: the worker stopped while this deployment was running. Deploy again."));
        }
    }

    private async Task RenewLeaseAsync(Guid jobId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
                var lease = clock.GetUtcNow().Add(Lease);
                await db.BackgroundJobs.Where(j => j.Id == jobId && j.LockedBy == _workerId)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseExpiresAt, lease), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not renew the lease for job {JobId}", jobId);
            }
        }
    }

    private async Task CompleteAsync(Guid jobId, string? error)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            var now = clock.GetUtcNow();
            var status = error is null ? JobStatus.Succeeded : JobStatus.Failed;
            await db.BackgroundJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.Error, error));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the outcome of job {JobId}", jobId);
        }
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
