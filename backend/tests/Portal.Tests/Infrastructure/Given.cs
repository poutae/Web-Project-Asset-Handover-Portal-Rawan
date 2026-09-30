using System.Net;
using Portal.Api.Contracts;

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
}
