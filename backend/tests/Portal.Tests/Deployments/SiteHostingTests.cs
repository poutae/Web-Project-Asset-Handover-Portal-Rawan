using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Portal.Infrastructure.Deployments;

namespace Portal.Tests.Deployments;

/// <summary>Serving deployed sites by hostname, from prepared release folders, with no database.</summary>
public sealed class SiteHostingTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Domain = "sites.test";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"portal-sites-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _release = new('c', 32);

    public SiteHostingTests(WebApplicationFactory<Program> factory)
    {
        var release = Path.Combine(_root, "releases", "demo", _release);
        Directory.CreateDirectory(Path.Combine(release, "about"));
        File.WriteAllText(Path.Combine(release, "index.html"), "<p>home page</p>");
        File.WriteAllText(Path.Combine(release, "about", "index.html"), "<p>about page</p>");
        File.WriteAllText(Path.Combine(release, "app.js"), "console.log(1)");
        File.WriteAllText(Path.Combine(release, "404.html"), "<p>custom 404</p>");
        File.WriteAllText(Path.Combine(release, ".env"), "SECRET=1");
        File.WriteAllText(Path.Combine(_root, "outside.txt"), "outside the release");
        var layout = new DeploymentLayout(Microsoft.Extensions.Options.Options.Create(new DeployOptions { Root = _root }));
        layout.WritePointer("demo", new LivePointer(_release, SpaFallback: false));

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Deploy:Root", _root);
            builder.UseSetting("Deploy:BaseDomain", Domain);
            builder.UseSetting("Deploy:WorkerEnabled", "false");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("ConnectionStrings:Default", "Server=unreachable;Database=none");
            builder.UseSetting("DataProtection:KeysPath", Path.Combine(_root, "keys"));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private async Task<HttpResponseMessage> GetAsync(string host, string path, HttpMethod? method = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Host = host;
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_live_release_is_served_for_its_hostname()
    {
        using var home = await GetAsync($"demo.{Domain}", "/");
        using var about = await GetAsync($"demo.{Domain}", "/about");
        using var script = await GetAsync($"demo.{Domain}", "/app.js");

        Assert.Equal("<p>home page</p>", await home.Content.ReadAsStringAsync(Ct));
        Assert.Equal("<p>about page</p>", await about.Content.ReadAsStringAsync(Ct));
        Assert.Contains("javascript", script.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        Assert.Equal("nosniff", home.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Unknown_or_undeployed_sites_and_invalid_labels_are_404()
    {
        using var unknown = await GetAsync($"nothing.{Domain}", "/");
        using var nested = await GetAsync($"a.b.{Domain}", "/");
        using var bare = await GetAsync(Domain, "/");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, nested.StatusCode);
        // The bare base domain is not a site host, so it falls through to the portal itself.
        Assert.NotEqual("This site is not deployed.", await bare.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Missing_files_use_the_sites_own_404_page()
    {
        using var response = await GetAsync($"demo.{Domain}", "/nope.html");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("<p>custom 404</p>", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("/.env")]
    [InlineData("/../outside.txt")]
    [InlineData("/%2e%2e/outside.txt")]
    [InlineData("/about/../../outside.txt")]
    public async Task Hidden_files_and_path_traversal_do_not_leak_anything(string path)
    {
        using var response = await GetAsync($"demo.{Domain}", path);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.DoesNotContain("SECRET=1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("outside the release", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_get_and_head_are_allowed()
    {
        using var post = await GetAsync($"demo.{Domain}", "/", HttpMethod.Post);
        using var head = await GetAsync($"demo.{Domain}", "/", HttpMethod.Head);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_site_host_never_reaches_the_portal_api()
    {
        using var response = await GetAsync($"demo.{Domain}", "/api/health");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // the site has no such file; the portal's health endpoint is not exposed here
    }

    [Fact]
    public async Task Switching_the_pointer_switches_the_release_immediately()
    {
        var second = new string('d', 32);
        var release = Path.Combine(_root, "releases", "demo", second);
        Directory.CreateDirectory(release);
        File.WriteAllText(Path.Combine(release, "index.html"), "<p>second release</p>");
        var layout = new DeploymentLayout(Microsoft.Extensions.Options.Options.Create(new DeployOptions { Root = _root }));

        layout.WritePointer("demo", new LivePointer(second, SpaFallback: true));
        using var swapped = await GetAsync($"demo.{Domain}", "/anything/at/all");

        Assert.Equal("<p>second release</p>", await swapped.Content.ReadAsStringAsync(Ct)); // SPA fallback
    }
}
