using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Portal.Tests;

public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_ok()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
