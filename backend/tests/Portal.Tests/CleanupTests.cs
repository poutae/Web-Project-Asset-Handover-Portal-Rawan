using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Portal.Api.Contracts;
using Portal.Api.Maintenance;
using Portal.Domain;
using Portal.Infrastructure.Persistence;
using Portal.Infrastructure.Storage;
using Portal.Tests.Infrastructure;

namespace Portal.Tests;

[Trait("Category", "Integration")]
public sealed class CleanupTests(PortalApiFactory factory) : IClassFixture<PortalApiFactory>
{
    private static readonly byte[] Pdf = [.. "%PDF-1.7\n"u8, .. RandomNumberGenerator.GetBytes(500)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CleanupRunner Runner(CleanupOptions? settings = null) => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(),
        factory.Services.GetRequiredService<IFileStorage>(),
        Options.Create(settings ?? new CleanupOptions()),
        NullLogger<CleanupRunner>.Instance);

    private async Task<List<string>> StoredKeysAsync()
    {
        var keys = new List<string>();
        await foreach (var file in factory.Services.GetRequiredService<IFileStorage>().ListAsync(Ct))
        {
            keys.Add(file.Key);
        }

        return keys;
    }

    private async Task<T> InDbAsync<T>(Func<PortalDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<PortalDbContext>());
    }

    private async Task<string> StoreOrphanAsync() =>
        await StoreOrphanAsync(LocalFileStorage.NewKey());

    private async Task<string> StoreOrphanAsync(string key)
    {
        await factory.Services.GetRequiredService<IFileStorage>().SaveAsync(key, new MemoryStream(Pdf), maxBytes: 1_000_000, Ct);
        return key;
    }

    private static async Task<DocumentDto> UploadAsync(ApiClient client, ProjectDto project)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "brief.pdf");
        using var response = await client.SendContentAsync(HttpMethod.Post, $"/api/projects/{project.Id}/documents", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiClient.ReadAsync<DocumentDto>(response);
    }

    [Fact]
    public async Task Idempotency_records_are_kept_for_the_retention_period_and_then_removed()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        await Given.AProjectAsync(admin);
        var before = await InDbAsync(db => db.IdempotencyRecords.IgnoreQueryFilters().CountAsync(Ct));
        Assert.True(before > 0);

        var now = DateTimeOffset.UtcNow;
        var early = await Runner().RunOnceAsync(now.AddDays(29), Ct);
        Assert.Equal(0, early.IdempotencyRecords);
        Assert.Equal(before, await InDbAsync(db => db.IdempotencyRecords.IgnoreQueryFilters().CountAsync(Ct)));

        var late = await Runner().RunOnceAsync(now.AddDays(31), Ct);
        Assert.Equal(before, late.IdempotencyRecords);
        Assert.Equal(0, await InDbAsync(db => db.IdempotencyRecords.IgnoreQueryFilters().CountAsync(Ct)));
    }

    [Fact]
    public async Task Only_finished_jobs_past_the_retention_period_are_removed()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now.AddDays(-40);
        BackgroundJob Job(JobStatus status, DateTimeOffset? finished) => new()
        {
            Type = "cleanup-test",
            Status = status,
            CreatedAt = old,
            RunAfter = old,
            FinishedAt = finished,
        };

        var oldSucceeded = Job(JobStatus.Succeeded, old);
        var oldFailed = Job(JobStatus.Failed, old);
        var recentSucceeded = Job(JobStatus.Succeeded, now.AddDays(-1));
        var oldPending = Job(JobStatus.Pending, null);
        var oldRunning = Job(JobStatus.Running, null);
        await InDbAsync(async db =>
        {
            db.BackgroundJobs.AddRange(oldSucceeded, oldFailed, recentSucceeded, oldPending, oldRunning);
            return await db.SaveChangesAsync(Ct);
        });

        var result = await Runner().RunOnceAsync(now, Ct);

        Assert.True(result.Jobs >= 2);
        var remaining = await InDbAsync(db => db.BackgroundJobs.Where(j => j.Type == "cleanup-test").Select(j => j.Id).ToListAsync(Ct));
        Assert.DoesNotContain(oldSucceeded.Id, remaining);
        Assert.DoesNotContain(oldFailed.Id, remaining);
        Assert.Contains(recentSucceeded.Id, remaining);
        Assert.Contains(oldPending.Id, remaining);
        Assert.Contains(oldRunning.Id, remaining);
    }

    [Fact]
    public async Task An_orphaned_file_is_removed_after_the_grace_period_and_documents_files_are_kept()
    {
        var (admin, _) = await Given.AnOrganizationAsync(factory);
        using var _admin = admin;
        var project = await Given.AProjectAsync(admin);
        var document = await UploadAsync(admin, project);
        var keysWithDocument = await StoredKeysAsync();
        var orphan = await StoreOrphanAsync();
        var now = DateTimeOffset.UtcNow;

        var early = await Runner().RunOnceAsync(now, Ct);
        Assert.Equal(0, early.OrphanedFiles); // too new: it could be an upload that is still being saved
        Assert.Contains(orphan, await StoredKeysAsync());

        var late = await Runner().RunOnceAsync(now.AddDays(2), Ct);
        Assert.Equal(1, late.OrphanedFiles);
        Assert.Equal(keysWithDocument.Order(), (await StoredKeysAsync()).Order()); // only the orphan is gone
        using var download = await admin.GetAsync($"/api/projects/{project.Id}/documents/{document.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task A_file_that_belongs_to_a_document_of_another_organization_is_never_treated_as_an_orphan()
    {
        var (first, _) = await Given.AnOrganizationAsync(factory);
        var (second, _) = await Given.AnOrganizationAsync(factory);
        using var _first = first;
        using var _second = second;
        var firstProject = await Given.AProjectAsync(first);
        var secondProject = await Given.AProjectAsync(second);
        await UploadAsync(first, firstProject);
        await UploadAsync(second, secondProject);
        var before = (await StoredKeysAsync()).Count;

        var result = await Runner().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2), Ct);

        Assert.Equal(0, result.OrphanedFiles);
        Assert.Equal(before, (await StoredKeysAsync()).Count);
    }

    [Fact]
    public async Task At_most_the_configured_number_of_orphans_is_removed_per_pass_and_the_pass_says_so()
    {
        var orphans = new[] { await StoreOrphanAsync(), await StoreOrphanAsync(), await StoreOrphanAsync() };
        var runner = Runner(new CleanupOptions { MaxOrphansPerRun = 2 });
        var now = DateTimeOffset.UtcNow.AddDays(2);

        var first = await runner.RunOnceAsync(now, Ct);
        Assert.True(first.OrphanLimitReached);
        Assert.Equal(2, first.OrphanedFiles);
        Assert.Equal(1, (await StoredKeysAsync()).Count(orphans.Contains));

        var second = await runner.RunOnceAsync(now, Ct);
        Assert.False(second.OrphanLimitReached);
        Assert.Equal(1, second.OrphanedFiles);
        Assert.DoesNotContain((await StoredKeysAsync()), orphans.Contains);
    }

    [Fact]
    public async Task Half_written_uploads_are_removed_once_they_are_old_enough()
    {
        var key = LocalFileStorage.NewKey();
        var directory = Path.Combine(factory.StorageRoot, key[..2]);
        Directory.CreateDirectory(directory);
        var leftover = Path.Combine(directory, $"{key}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(leftover, "interrupted upload", Ct);

        var early = await Runner().RunOnceAsync(DateTimeOffset.UtcNow, Ct);
        Assert.Equal(0, early.AbandonedUploads);
        Assert.True(File.Exists(leftover));

        var late = await Runner().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(2), Ct);
        Assert.Equal(1, late.AbandonedUploads);
        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public async Task A_grace_period_below_one_hour_is_raised_to_one_hour()
    {
        var orphan = await StoreOrphanAsync();
        var runner = Runner(new CleanupOptions { OrphanGracePeriod = TimeSpan.Zero });

        var result = await runner.RunOnceAsync(DateTimeOffset.UtcNow.AddMinutes(5), Ct);

        Assert.Equal(0, result.OrphanedFiles);
        Assert.Contains(orphan, await StoredKeysAsync());
        await factory.Services.GetRequiredService<IFileStorage>().DeleteAsync(orphan, Ct); // keep the other tests' counts exact
    }
}
