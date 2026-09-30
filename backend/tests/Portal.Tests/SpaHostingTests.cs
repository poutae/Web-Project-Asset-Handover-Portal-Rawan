using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Portal.Tests;

/// <summary>
/// The API serves the built frontend. These run without a database, and guard against static assets being
/// answered with the SPA shell (which leaves the browser with HTML where it expected JavaScript).
/// </summary>
public sealed class SpaHostingTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _dist = Path.Combine(Path.GetTempPath(), $"portal-dist-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public SpaHostingTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_dist, "assets"));
        File.WriteAllText(Path.Combine(_dist, "index.html"), "<!doctype html><title>shell</title>");
        File.WriteAllText(Path.Combine(_dist, "sw.js"), "// service worker");
        File.WriteAllText(Path.Combine(_dist, "favicon.svg"), "<svg xmlns='http://www.w3.org/2000/svg'/>");
        File.WriteAllText(Path.Combine(_dist, "assets", "app-abc123.js"), "console.log('app')");

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Frontend:DistPath", _dist);
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Default", "Server=unreachable;Database=none");
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_dist, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Assets_are_served_as_themselves_and_cached_for_a_long_time()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/assets/app-abc123.js", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        Assert.Equal("console.log('app')", await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_files_are_served_and_the_service_worker_is_never_cached()
    {
        using var client = _factory.CreateClient();

        using var icon = await client.GetAsync("/favicon.svg", Ct);
        using var worker = await client.GetAsync("/sw.js", Ct);

        Assert.Equal("image/svg+xml", icon.Content.Headers.ContentType?.MediaType);
        Assert.Equal("// service worker", await worker.Content.ReadAsStringAsync(Ct));
        Assert.Contains("no-cache", worker.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/register")]
    [InlineData("/projects/123e4567-e89b-12d3-a456-426614174000")]
    public async Task Application_routes_get_the_shell(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>shell</title>", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/hubs/nothing")]
    [InlineData("/assets/missing.js")]
    public async Task Unknown_api_hub_and_asset_paths_are_404_not_the_shell(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Liveness_still_works_alongside_the_frontend()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/health", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }
}
