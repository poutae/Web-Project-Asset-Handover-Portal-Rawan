using Portal.Infrastructure.Deployments;

namespace Portal.Tests.Deployments;

/// <summary>
/// The real thing, with no database: git fetches a real repository, node really runs its build, and the
/// output really is copied into a release. Only the sandbox is the unisolated local one.
/// </summary>
public sealed class StaticSiteProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Builds_a_repository_and_stores_the_output_as_a_release()
    {
        using var repo = new GitRepo("first-build");
        using var fixture = new ProviderFixture();
        var log = new CapturingLog();
        var request = ProviderFixture.Request(repo);

        var output = await fixture.Provider.BuildAsync(request, log, Ct);

        Assert.Equal(repo.HeadSha, output.CommitSha);
        Assert.Equal(request.DeploymentId.ToString("N"), output.ReleaseKey);
        Assert.True(fixture.Provider.ReleaseExists(request.SiteLabel, output.ReleaseKey));
        var release = fixture.Layout.ReleaseDirectory(request.SiteLabel, output.ReleaseKey);
        Assert.Contains("first-build", File.ReadAllText(Path.Combine(release, "index.html")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(release, "about", "index.html")));
        Assert.Contains("build finished: first-build", log.Text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.Layout.WorkDirectory(request.DeploymentId)), "the build workspace must be removed");
    }

    [Fact]
    public async Task Builds_the_exact_commit_that_was_asked_for()
    {
        using var repo = new GitRepo("old-version");
        var oldSha = repo.HeadSha;
        repo.Commit("new-version");
        using var fixture = new ProviderFixture();

        var output = await fixture.Provider.BuildAsync(ProviderFixture.Request(repo, gitRef: oldSha), new CapturingLog(), Ct);

        Assert.Equal(oldSha, output.CommitSha);
        var release = fixture.Layout.ReleaseDirectory("demo-site", output.ReleaseKey);
        Assert.Contains("old-version", File.ReadAllText(Path.Combine(release, "index.html")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_build_is_reported_as_failed_and_leaves_no_release()
    {
        using var repo = new GitRepo();
        using var fixture = new ProviderFixture();
        var request = ProviderFixture.Request(repo, command: "node -e \"console.error('compile error'); process.exit(3)\"");
        var log = new CapturingLog();

        var failure = await Assert.ThrowsAsync<DeploymentFailedException>(() => fixture.Provider.BuildAsync(request, log, Ct));

        Assert.Contains("exit code 3", failure.Message, StringComparison.Ordinal);
        Assert.Contains("[err] compile error", log.Text, StringComparison.Ordinal);
        Assert.Empty(fixture.Provider.ListReleases(request.SiteLabel));
        Assert.False(Directory.Exists(fixture.Layout.WorkDirectory(request.DeploymentId)));
    }

    [Fact]
    public async Task A_build_that_produces_no_site_does_not_count_as_success()
    {
        using var repo = new GitRepo();
        using var fixture = new ProviderFixture();

        var missing = await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            fixture.Provider.BuildAsync(ProviderFixture.Request(repo, output: "nowhere"), new CapturingLog(), Ct));
        var noIndex = await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            fixture.Provider.BuildAsync(ProviderFixture.Request(repo, command: "node -e \"require('fs').mkdirSync('dist',{recursive:true})\""), new CapturingLog(), Ct));

        Assert.Contains("did not produce", missing.Message, StringComparison.Ordinal);
        Assert.Contains("no index.html", noIndex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../elsewhere")]
    [InlineData(".")]
    public async Task The_output_folder_cannot_escape_the_repository(string output)
    {
        using var repo = new GitRepo();
        using var fixture = new ProviderFixture();

        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            fixture.Provider.BuildAsync(ProviderFixture.Request(repo, output: output), new CapturingLog(), Ct));
    }

    [Fact]
    public async Task Symbolic_links_in_the_build_output_are_refused()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating symlinks needs elevated rights on Windows; the check itself is platform independent
        }

        using var repo = new GitRepo();
        using var fixture = new ProviderFixture();
        var command = "node -e \"const fs=require('fs');fs.mkdirSync('dist');fs.writeFileSync('dist/index.html','x');fs.symlinkSync('/etc/passwd','dist/leak')\"";

        var failure = await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            fixture.Provider.BuildAsync(ProviderFixture.Request(repo, command: command), new CapturingLog(), Ct));

        Assert.Contains("symbolic link", failure.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Provider.ListReleases("demo-site"));
    }

    [Fact]
    public async Task A_build_that_runs_too_long_is_stopped()
    {
        using var repo = new GitRepo();
        using var fixture = new ProviderFixture(configure: o => o.BuildTimeoutSeconds = 2);

        var failure = await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            fixture.Provider.BuildAsync(ProviderFixture.Request(repo, command: "node -e \"setTimeout(()=>{},60000)\""), new CapturingLog(), Ct));

        Assert.Contains("Timed out", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_stops_the_build()
    {
        using var repo = new GitRepo();
        using var fixture = new ProviderFixture();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var started = DateTime.UtcNow;

        var build = fixture.Provider.BuildAsync(ProviderFixture.Request(repo, command: "node -e \"setTimeout(()=>{},60000)\""), new CapturingLog(), cancel.Token);
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task The_access_token_never_reaches_the_log_and_is_not_on_the_command_line()
    {
        using var repo = new GitRepo();
        using var fixture = new ProviderFixture();
        const string token = "ghp_supersecrettoken123456";
        var log = new CapturingLog();
        var command = "node -e \"console.log(process.env.GIT_CONFIG_VALUE_0 || 'none'); console.log('token is ghp_supersecrettoken123456')\" && node build.js";

        await fixture.Provider.BuildAsync(ProviderFixture.Request(repo, command: command, token: token), log, Ct);

        Assert.DoesNotContain(token, log.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"x-access-token:{token}")), log.Text, StringComparison.Ordinal);
        Assert.Contains("***", log.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_code_does_not_see_the_portals_environment()
    {
        using var repo = new GitRepo();
        using var fixture = new ProviderFixture();
        Environment.SetEnvironmentVariable("PORTAL_TEST_SECRET", "must-not-leak");
        try
        {
            var log = new CapturingLog();
            await fixture.Provider.BuildAsync(
                ProviderFixture.Request(repo, command: "node -e \"console.log('seen=' + (process.env.PORTAL_TEST_SECRET || 'nothing'))\" && node build.js"), log, Ct);

            Assert.Contains("seen=nothing", log.Text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PORTAL_TEST_SECRET", null);
        }
    }

    [Theory]
    [InlineData("--upload-pack=evil")]
    [InlineData("-x")]
    [InlineData("a b")]
    [InlineData("branch;rm -rf")]
    public async Task Dangerous_refs_are_refused_before_anything_runs(string gitRef)
    {
        using var repo = new GitRepo();
        using var fixture = new ProviderFixture();

        await Assert.ThrowsAsync<DeploymentFailedException>(() =>
            fixture.Provider.BuildAsync(ProviderFixture.Request(repo, gitRef: gitRef), new CapturingLog(), Ct));
        Assert.False(Directory.Exists(fixture.Layout.WorkRoot) && Directory.EnumerateFileSystemEntries(fixture.Layout.WorkRoot).Any());
    }

    [Fact]
    public async Task Activating_switches_the_live_release_atomically_and_deactivating_takes_the_site_down()
    {
        using var repo = new GitRepo("one");
        using var fixture = new ProviderFixture();
        var first = await fixture.Provider.BuildAsync(ProviderFixture.Request(repo), new CapturingLog(), Ct);
        repo.Commit("two");
        var second = await fixture.Provider.BuildAsync(ProviderFixture.Request(repo), new CapturingLog(), Ct);
        var site = new SiteTarget("demo-site", SpaFallback: true);

        await fixture.Provider.ActivateAsync(site, first.ReleaseKey, Ct);
        Assert.Equal(first.ReleaseKey, fixture.Layout.ReadPointer("demo-site")?.ReleaseKey);
        await fixture.Provider.ActivateAsync(site, second.ReleaseKey, Ct);
        Assert.Equal(new LivePointer(second.ReleaseKey, true), fixture.Layout.ReadPointer("demo-site"));

        await fixture.Provider.DeactivateAsync(site, Ct);
        Assert.Null(fixture.Layout.ReadPointer("demo-site"));
        await Assert.ThrowsAsync<DeploymentFailedException>(() => fixture.Provider.ActivateAsync(site, new string('0', 32), Ct));
    }

    [Fact]
    public async Task Releases_can_be_listed_and_removed()
    {
        using var repo = new GitRepo();
        using var fixture = new ProviderFixture();
        var output = await fixture.Provider.BuildAsync(ProviderFixture.Request(repo), new CapturingLog(), Ct);
        Assert.Equal([output.ReleaseKey], fixture.Provider.ListReleases("demo-site"));

        await fixture.Provider.RemoveReleaseAsync("demo-site", output.ReleaseKey, Ct);

        Assert.Empty(fixture.Provider.ListReleases("demo-site"));
        Assert.False(fixture.Provider.ReleaseExists("demo-site", output.ReleaseKey));
    }
}
