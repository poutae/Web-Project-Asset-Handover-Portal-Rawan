using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Portal.Api.Concurrency;
using Portal.Domain;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Realtime;

public interface IRealtimePublisher
{
    /// <param name="staffOnly">When true, project clients are excluded (internal notes).</param>
    /// <param name="alsoNotify">Users to notify who are no longer project members, such as a removed member.</param>
    Task PublishAsync(
        string kind,
        string action,
        Guid projectId,
        Guid entityId,
        byte[]? rowVersion,
        bool staffOnly,
        CancellationToken ct,
        IReadOnlyCollection<Guid>? alsoNotify = null);
}

/// <summary>
/// Computes who may see a project at publish time (organization admins plus project members) and pushes
/// the event to them. A failure to push never fails the request that caused it.
/// </summary>
public sealed class RealtimePublisher(
    IHubContext<PortalHub> hub,
    PortalDbContext db,
    IHttpContextAccessor accessor,
    TimeProvider clock,
    ILogger<RealtimePublisher> logger) : IRealtimePublisher
{
    public const string ClientIdHeader = "X-Client-Id";

    public async Task PublishAsync(
        string kind,
        string action,
        Guid projectId,
        Guid entityId,
        byte[]? rowVersion,
        bool staffOnly,
        CancellationToken ct,
        IReadOnlyCollection<Guid>? alsoNotify = null)
    {
        try
        {
            var recipients = await RecipientsAsync(projectId, staffOnly, alsoNotify ?? [], ct);
            if (recipients.Count == 0)
            {
                return;
            }

            var origin = accessor.HttpContext?.Request.Headers[ClientIdHeader].ToString();
            var change = new RealtimeEvent(
                kind,
                action,
                projectId,
                entityId,
                rowVersion is { Length: > 0 } ? OptimisticConcurrency.ToVersion(rowVersion) : null,
                string.IsNullOrWhiteSpace(origin) ? null : origin[..Math.Min(origin.Length, 64)],
                clock.GetUtcNow());

            await hub.Clients.Users(recipients).SendAsync(PortalHub.ChangedMethod, change, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish a realtime event for project {ProjectId}", projectId);
        }
    }

    private async Task<List<string>> RecipientsAsync(
        Guid projectId, bool staffOnly, IReadOnlyCollection<Guid> alsoNotify, CancellationToken ct)
    {
        // Both queries are tenant-scoped: ProjectMembers by its query filter, Users by the explicit check.
        var organizationId = db.CurrentOrganizationId;

        var admins = await db.Users
            .Where(u => u.OrganizationId == organizationId && u.Role == OrgRole.Admin)
            .Select(u => u.Id)
            .ToListAsync(ct);

        var members = db.ProjectMembers.Where(m => m.ProjectId == projectId);
        if (staffOnly)
        {
            members = members.Where(m => m.Role != ProjectRole.Client);
        }

        var memberIds = await members.Select(m => m.UserId).ToListAsync(ct);

        return admins.Union(memberIds).Union(alsoNotify).Select(id => id.ToString()).ToList();
    }
}
