using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Portal.Api.Realtime;

/// <summary>
/// Push-only hub. Clients never call it; the server sends <c>changed</c> events to exactly the users
/// who may see the affected project, addressed by user id.
/// </summary>
[Authorize]
public sealed class PortalHub : Hub
{
    public const string Path = "/hubs/portal";
    public const string ChangedMethod = "changed";
}
