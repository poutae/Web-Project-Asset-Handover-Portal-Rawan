using System.Net;
using Portal.Api.Contracts;
using Portal.Domain;

namespace Portal.Tests.Infrastructure;

public static class Given
{
    public const string Password = "correct horse battery staple";

    /// <summary>Registers a brand-new organization and returns a client signed in as its admin.</summary>
    public static async Task<(ApiClient Client, MeResponse Me)> AnOrganizationAsync(PortalApiFactory factory, string? name = null)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var client = new ApiClient(factory);
        using var response = await client.PostAsync("/api/auth/register", new
        {
            organizationName = name ?? $"Agency {unique}",
            displayName = $"Admin {unique}",
            email = $"admin-{unique}@example.test",
            password = Password,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, await ApiClient.ReadAsync<MeResponse>(response));
    }

    /// <summary>Invites a person into the admin's organization and returns a client signed in as them.</summary>
    public static async Task<(ApiClient Client, MeResponse Me)> APersonAsync(PortalApiFactory factory, ApiClient admin, OrgRole role)
    {
        using var invite = await admin.PostAsync("/api/org/invitations", new
        {
            email = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@example.test",
            role,
        });
        Assert.Equal(HttpStatusCode.Created, invite.StatusCode);
        var invitation = await ApiClient.ReadAsync<CreatedInvitationDto>(invite);

        var client = new ApiClient(factory);
        using var accept = await client.PostAsync("/api/invitations/accept", new
        {
            token = invitation.Token,
            displayName = $"{role} {invitation.Id.ToString("N")[..6]}",
            password = Password,
        });
        Assert.Equal(HttpStatusCode.Created, accept.StatusCode);
        return (client, await ApiClient.ReadAsync<MeResponse>(accept));
    }

    /// <summary>Creates a project as the given (staff) client.</summary>
    public static async Task<ProjectDto> AProjectAsync(ApiClient staff, string name = "Website redesign")
    {
        using var response = await staff.PostAsync("/api/projects", new { name, description = "Kick-off" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiClient.ReadAsync<ProjectDto>(response);
    }
}
