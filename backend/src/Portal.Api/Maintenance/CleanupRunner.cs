using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Domain;
using Portal.Infrastructure.Persistence;
using Portal.Infrastructure.Storage;

namespace Portal.Api.Maintenance;

/// <summary>What one cleanup pass removed.</summary>
public sealed record CleanupResult(
    int IdempotencyRecords, int Jobs, int OrphanedFiles, int AbandonedUploads, bool OrphanLimitReached);

/// <summary>
/// One housekeeping pass: old idempotency records, finished queue rows, stored files that no document
/// refers to, and half-written uploads. Every step only deletes data that nothing can use any more, is
/// safe to repeat, and is safe to run from several servers at once. A failing step does not stop the others.
/// </summary>
public sealed class CleanupRunner(
    IServiceScopeFactory scopes,
    IFileStorage storage,
    IOptions<CleanupOptions> options,
    ILogger<CleanupRunner> logger)
{
    private const int DeleteBatchSize = 5_000;
    private const int LookupBatchSize = 500;
    private static readonly TimeSpan MinimumGrace = TimeSpan.FromHours(1);

    public async Task<CleanupResult> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var settings = options.Value;
        var grace = settings.OrphanGracePeriod < MinimumGrace ? MinimumGrace : settings.OrphanGracePeriod;

        var idempotency = await StepAsync("idempotency records", () => DeleteOldIdempotencyRecordsAsync(now - settings.IdempotencyRetention, ct));
        var jobs = await StepAsync("finished jobs", () => DeleteOldJobsAsync(now - settings.JobRetention, ct));
        var abandoned = await StepAsync("abandoned uploads", () => storage.DeleteAbandonedUploadsAsync(now - grace, ct));
        var (orphans, limitReached) = await StepAsync("orphaned files", () => DeleteOrphanedFilesAsync(now - grace, Math.Max(0, settings.MaxOrphansPerRun), ct));

        if (limitReached)
        {
            logger.LogWarning(
                "Cleanup removed {Count} orphaned files and stopped at the limit of {Limit}. If that is unexpected, check that the app points at the right database.",
                orphans, settings.MaxOrphansPerRun);
        }

        if (idempotency + jobs + orphans + abandoned > 0)
        {
            logger.LogInformation(
                "Cleanup removed {Idempotency} idempotency records, {Jobs} finished jobs, {Orphans} orphaned files, and {Abandoned} abandoned uploads",
                idempotency, jobs, orphans, abandoned);
        }

        return new CleanupResult(idempotency, jobs, orphans, abandoned, limitReached);
    }

    private async Task<T> StepAsync<T>(string name, Func<Task<T>> step)
    {
        try
        {
            return await step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Cleanup step '{Step}' failed and will be retried on the next pass", name);
            return default!;
        }
    }

    private async Task<int> DeleteOldIdempotencyRecordsAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        // Batches keep each transaction (and its locks) small however many rows have piled up.
        var total = 0;
        int affected;
        do
        {
            affected = await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE TOP ({DeleteBatchSize}) FROM IdempotencyRecords WHERE CreatedAt < {cutoff}", ct);
            total += affected;
        }
        while (affected == DeleteBatchSize);

        return total;
    }

    private async Task<int> DeleteOldJobsAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        return await db.BackgroundJobs
            .Where(j => (j.Status == JobStatus.Succeeded || j.Status == JobStatus.Failed) && j.FinishedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    private async Task<(int Removed, bool LimitReached)> DeleteOrphanedFilesAsync(DateTimeOffset cutoff, int limit, CancellationToken ct)
    {
        var removed = 0;
        var candidates = new List<string>(LookupBatchSize);

        async Task<bool> FlushAsync()
        {
            if (candidates.Count == 0)
            {
                return true;
            }

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

            // Documents are tenant-filtered; the sweeper is a system component and must see all of them.
            var referenced = (await db.Documents.IgnoreQueryFilters()
                .Where(d => candidates.Contains(d.StorageKey))
                .Select(d => d.StorageKey)
                .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

            foreach (var key in candidates.Where(key => !referenced.Contains(key)))
            {
                if (removed >= limit)
                {
                    candidates.Clear();
                    return false;
                }

                await storage.DeleteAsync(key, ct);
                removed++;
            }

            candidates.Clear();
            return true;
        }

        await foreach (var file in storage.ListAsync(ct))
        {
            if (file.LastModified >= cutoff)
            {
                continue; // possibly still being uploaded
            }

            candidates.Add(file.Key);
            if (candidates.Count >= LookupBatchSize && !await FlushAsync())
            {
                return (removed, true);
            }
        }

        var completed = await FlushAsync();
        return (removed, !completed);
    }
}
