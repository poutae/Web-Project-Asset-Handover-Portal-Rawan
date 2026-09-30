using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Portal.Tests;

/// <summary>Liveness needs no database, so this test runs anywhere.</summary>
public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_ok_without_a_database()
    {
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Default", "Server=unreachable;Database=none");
        });
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/api/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
