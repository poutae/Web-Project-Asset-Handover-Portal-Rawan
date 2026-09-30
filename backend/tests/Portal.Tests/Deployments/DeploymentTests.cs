using System.Net;
using Portal.Api.Contracts;
using Portal.Domain;
using Portal.Tests.Infrastructure;

namespace Portal.Tests.Deployments;

/// <summary>
/// Whole-system deployment tests: the API, the SQL job queue, the background worker, a real git repository,
/// a real node build, and the site served back over HTTP by hostname.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeploymentTests(PortalApiFactory factory) : IClassFixture<PortalApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string EnvUrl(ProjectDto project) => $"/api/projects/{project.Id}/environments";

    private static async Task<EnvironmentDto> CreateEnvironmentAsync(
        ApiClient lead, ProjectDto project, GitRepo repo, string name = "production", string? buildCommand = null, string? healthPath = null,
        string? token = null, bool spa = false)
    {
        using var response = await lead.PostAsync(EnvUrl(project), new
        {
            name,
            repositoryUrl = repo.Url,
            branch = "main",
            buildCommand = buildCommand ?? GitRepo.BuildCommand,
            outputDirectory = "dist",
            healthPath = healthPath ?? "/",
            spaFallback = spa,
            accessToken = token,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiClient.ReadAsync<EnvironmentDto>(response);
    }

    private static async Task<DeploymentDto> StartAsync(ApiClient client, ProjectDto project, EnvironmentDto environment, string? gitRef = null)
    {
        using var response = await client.PostAsync($"{EnvUrl(project)}/{environment.Id}/deployments", new { @ref = gitRef });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await ApiClient.ReadAsync<DeploymentDto>(response);
    }

    private static async Task<DeploymentDto> WaitForAsync(
        ApiClient client, ProjectDto project, EnvironmentDto environment, Guid deploymentId, Func<DeploymentStatus, bool> done, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            using var response = await client.GetAsync($"{EnvUrl(project)}/{environment.Id}/deployments/{deploymentId}");
            var deployment = await ApiClient.ReadAsync<DeploymentDto>(response);
            if (done(deployment.Status))
            {
                return deployment;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Deployment stuck in {deployment.Status}.");
            await Task.Delay(300, Ct);
        }
    }

    private static bool Finished(DeploymentStatus status) => status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Cancelled;

    private async Task<(HttpStatusCode Status, string Body)> VisitAsync(EnvironmentDto environment, string path, HttpMethod? method = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Host = $"{environment.SiteLabel}.{PortalApiFactory.SiteDomain}";
        using var response = await client.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> LogsAsync(ApiClient client, ProjectDto project, EnvironmentDto environment, Guid deploymentId)
    {
        var text = new System.Text.StringBuilder();
        long after = 0;
        for (var round = 0; round < 20; round++)
        {
            using var response = await client.GetAsync($"{EnvUrl(project)}/{environment.Id}/deployments/{deploymentId}/logs?after={after}");
            var page = await ApiClient.ReadAsync<DeploymentLogsDto>(response);
            foreach (var line in page.Lines)
            {
                text.AppendLine(line.Message);
            }

            after = page.Next;
            if (page.Complete)
            {
                break;
            }

            await Task.Delay(300, Ct);
        }

        return text.ToString();
    }

    [Fact]
    public async Task A_deployment_builds_the_repository_and_serves_the_site_by_hostname()
    {
        var (lead, _) = await Given.AnOrganizationAsync(factory);
        using var _lead = lead;
        using var repo = new GitRepo("hello-from-git");
        var project = await Given.AProjectAsync(lead);
        var environment = await CreateEnvironmentAsync(lead, project, repo);
        Assert.Equal($"http://{environment.SiteLabel}.{PortalApiFactory.SiteDomain}", environment.Url);
        Assert.Equal(HttpStatusCode.NotFound, (await VisitAsync(environment, "/")).Status);

        var started = await StartAsync(lead, project, environment);
        var finished = await WaitForAsync(lead, project, environment, started.Id, Finished);

        Assert.Equal(DeploymentStatus.Succeeded, finished.Status);
        Assert.Equal(repo.HeadSha, finished.CommitSha);
        Assert.True(finished.IsCurrent);
        var (status, body) = await VisitAsync(environment, "/");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("hello-from-git", body, StringComparison.Ordinal);
        Assert.Contains("about hello-from-git", (await VisitAsync(environment, "/about")).Body, StringComparison.Ordinal);

        var logs = await LogsAsync(lead, project, environment, started.Id);
        Assert.Contains("build finished: hello-from-git", logs, StringComparison.Ordinal);
        Assert.Contains("Healthy", logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redeploying_and_rolling_back_change_what_is_live_and_a_rollback_does_not_rebuild()
    {
        var (lead, _) = await Given.AnOrganizationAsync(factory);
        using var _lead = lead;
        using var repo = new GitRepo("release-one");
        var firstSha = repo.HeadSha;
        var project = await Given.AProjectAsync(lead);
        var environment = await CreateEnvironmentAsync(lead, project, repo);

        var first = await WaitForAsync(lead, project, environment, (await StartAsync(lead, project, environment)).Id, Finished);
        repo.Commit("release-two");
        var second = await WaitForAsync(lead, project, environment, (await StartAsync(lead, project, environment)).Id, Finished);
        Assert.Contains("release-two", (await VisitAsync(environment, "/")).Body, StringComparison.Ordinal);

        using var rollback = await lead.PostAsync($"{EnvUrl(project)}/{environment.Id}/deployments/{first.Id}/rollback");
        Assert.Equal(HttpStatusCode.Accepted, rollback.StatusCode);
        var rolled = await WaitForAsync(lead, project, environment, (await ApiClient.ReadAsync<DeploymentDto>(rollback)).Id, Finished);

        Assert.Equal(DeploymentStatus.Succeeded, rolled.Status);
        Assert.Equal(DeploymentTrigger.Rollback, rolled.Trigger);
        Assert.Equal(firstSha, rolled.CommitSha);
        Assert.Contains("release-one", (await VisitAsync(environment, "/")).Body, StringComparison.Ordinal);
        Assert.Contains("no rebuild", await LogsAsync(lead, project, environment, rolled.Id), StringComparison.OrdinalIgnoreCase);

        // Rolling back to what is already live is refused; redeploying rebuilds the exact commit.
        using var again = await lead.PostAsync($"{EnvUrl(project)}/{environment.Id}/deployments/{rolled.Id}/rollback");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        using var redeploy = await lead.PostAsync($"{EnvUrl(project)}/{environment.Id}/deployments/{second.Id}/redeploy");
        var redeployed = await WaitForAsync(lead, project, environment, (await ApiClient.ReadAsync<DeploymentDto>(redeploy)).Id, Finished);
        Assert.Equal(DeploymentTrigger.Redeploy, redeployed.Trigger);
        Assert.Equal(second.CommitSha, redeployed.CommitSha);
        Assert.Contains("release-two", (await VisitAsync(environment, "/")).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_build_is_reported_honestly_and_puts_nothing_live()
    {
        var (lead, _) = await Given.AnOrganizationAsync(factory);
        using var _lead = lead;
        using var repo = new GitRepo();
        var project = await Given.AProjectAsync(lead);
        var environment = await CreateEnvironmentAsync(lead, project, repo, buildCommand: "node -e \"console.error('compile error: boom'); process.exit(2)\"");

        var started = await StartAsync(lead, project, environment);
        var finished = await WaitForAsync(lead, project, environment, started.Id, Finished);

        Assert.Equal(DeploymentStatus.Failed, finished.Status);
        Assert.Contains("exit code 2", finished.FailureReason, StringComparison.Ordinal);
        Assert.False(finished.IsCurrent);
        Assert.Equal(HttpStatusCode.NotFound, (await VisitAsync(environment, "/")).Status);
        Assert.Contains("compile error: boom", await LogsAsync(lead, project, environment, started.Id), StringComparison.Ordinal);
        using var list = await lead.GetAsync(EnvUrl(project));
        Assert.Null((await ApiClient.ReadAsync<List<EnvironmentDto>>(list)).Single().CurrentDeploymentId);
    }

    [Fact]
    public async Task A_release_that_fails_its_health_check_is_reverted_to_the_previous_one()
    {
        var (lead, _) = await Given.AnOrganizationAsync(factory);
        using var _lead = lead;
        using var repo = new GitRepo("healthy-release");
        var project = await Given.AProjectAsync(lead);
        var environment = await CreateEnvironmentAsync(lead, project, repo);
        var first = await WaitForAsync(lead, project, environment, (await StartAsync(lead, project, environment)).Id, Finished);
        Assert.Equal(DeploymentStatus.Succeeded, first.Status);

        // The first deployment changed the environment (it is now live), so edit from its current version.
        using var current = await lead.GetAsync(EnvUrl(project));
        var currentVersion = (await ApiClient.ReadAsync<List<EnvironmentDto>>(current)).Single().Version;

        // From now on the site must answer /health, which the next build does not provide.
        using var update = await lead.PutAsync(
            $"{EnvUrl(project)}/{environment.Id}",
            new { name = environment.Name, repositoryUrl = repo.Url, branch = "main", buildCommand = GitRepo.BuildCommand, outputDirectory = "dist", healthPath = "/health", spaFallback = false },
            currentVersion);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        repo.Commit("broken-release");

        var second = await WaitForAsync(lead, project, environment, (await StartAsync(lead, project, environment)).Id, Finished);

        Assert.Equal(DeploymentStatus.Failed, second.Status);
        Assert.True(second.RevertedToPrevious);
        Assert.Contains("health check", second.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("healthy-release", (await VisitAsync(environment, "/")).Body, StringComparison.Ordinal);
        using var list = await lead.GetAsync(EnvUrl(project));
        Assert.Equal(first.Id, (await ApiClient.ReadAsync<List<EnvironmentDto>>(list)).Single().CurrentDeploymentId);
    }

    [Fact]
    public async Task Only_one_deployment_runs_per_environment_and_a_running_build_can_be_cancelled()
    {
        var (lead, _) = await Given.AnOrganizationAsync(factory);
        using var _lead = lead;
        using var repo = new GitRepo();
        var project = await Given.AProjectAsync(lead);
        var environment = await CreateEnvironmentAsync(lead, project, repo, buildCommand: "node -e \"setTimeout(()=>{},60000)\"");

        var started = await StartAsync(lead, project, environment);
        using var second = await lead.PostAsync($"{EnvUrl(project)}/{environment.Id}/deployments", new { });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await WaitForAsync(lead, project, environment, started.Id, s => s == DeploymentStatus.Building);
        using var cancel = await lead.PostAsync($"{EnvUrl(project)}/{environment.Id}/deployments/{started.Id}/cancel");
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);

        var finished = await WaitForAsync(lead, project, environment, started.Id, Finished, seconds: 45);
        Assert.Equal(DeploymentStatus.Cancelled, finished.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await VisitAsync(environment, "/")).Status);
    }

    [Fact]
    public async Task The_access_token_is_encrypted_at_rest_write_only_and_never_logged()
    {
        var (lead, _) = await Given.AnOrganizationAsync(factory);
        using var _lead = lead;
        using var repo = new GitRepo();
        const string token = "ghp_endtoendsecrettoken987654";
        var project = await Given.AProjectAsync(lead);
        var environment = await CreateEnvironmentAsync(lead, project, repo, token: token, buildCommand: "node -e \"console.log(process.env.GIT_CONFIG_VALUE_0)\" && node build.js");
        Assert.True(environment.HasAccessToken);

        var started = await StartAsync(lead, project, environment);
        await WaitForAsync(lead, project, environment, started.Id, Finished);

        using var list = await lead.GetAsync(EnvUrl(project));
        Assert.DoesNotContain(token, await list.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(token, await LogsAsync(lead, project, environment, started.Id), StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Portal.Infrastructure.Persistence.PortalDbContext>();
        var raw = await db.DeploymentEnvironments.IgnoreQueryFilters().Where(e => e.Id == environment.Id).Select(e => e.ProtectedAccessToken).SingleAsync(Ct);
        Assert.NotNull(raw);
        Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.com/repo.git")]
    [InlineData("https://user:pass@example.com/repo.git")]
    [InlineData("https://localhost/repo.git")]
    [InlineData("https://127.0.0.1/repo.git")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("ssh://git@example.com/repo.git")]
    [InlineData("not a url")]
    public async Task Unsafe_repository_urls_are_refused(string url)
    {
        var (lead, _) = await Given.AnOrganizationAsync(factory);
        using var _lead = lead;
        var project = await Given.AProjectAsync(lead);

        using var response = await lead.PostAsync(EnvUrl(project), new { name = "bad", repositoryUrl = url, branch = "main" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Access_rules_hold_for_clients_staff_outsiders_and_other_organizations()
    {
        var (lead, _) = await Given.AnOrganizationAsync(factory);
        var (client, clientMe) = await Given.APersonAsync(factory, lead, OrgRole.Client);
        var (contributor, contributorMe) = await Given.APersonAsync(factory, lead, OrgRole.Member);
        var (outsider, _) = await Given.APersonAsync(factory, lead, OrgRole.Member);
        var (otherOrg, _) = await Given.AnOrganizationAsync(factory);
        using var _lead = lead;
        using var _client = client;
        using var _contributor = contributor;
        using var _outsider = outsider;
        using var _other = otherOrg;
        using var repo = new GitRepo("access-check");
        var project = await Given.AProjectAsync(lead);
        using var addClient = await lead.PostAsync($"/api/projects/{project.Id}/members", new { userId = clientMe.UserId, role = ProjectRole.Client });
        using var addContributor = await lead.PostAsync($"/api/projects/{project.Id}/members", new { userId = contributorMe.UserId, role = ProjectRole.Contributor });
        var environment = await CreateEnvironmentAsync(lead, project, repo);

        // Contributors deploy but do not manage environments.
        using var contributorCreates = await contributor.PostAsync(EnvUrl(project), new { name = "x", repositoryUrl = repo.Url, branch = "main" });
        Assert.Equal(HttpStatusCode.Forbidden, contributorCreates.StatusCode);
        var deployment = await WaitForAsync(contributor, project, environment, (await StartAsync(contributor, project, environment)).Id, Finished);
        Assert.Equal(DeploymentStatus.Succeeded, deployment.Status);

        // Clients see status and the URL, not the repository, build command, or logs, and cannot deploy.
        using var clientList = await client.GetAsync(EnvUrl(project));
        var seen = (await ApiClient.ReadAsync<List<EnvironmentDto>>(clientList)).Single();
        Assert.NotNull(seen.Url);
        Assert.Null(seen.RepositoryUrl);
        Assert.Null(seen.BuildCommand);
        using var clientLogs = await client.GetAsync($"{EnvUrl(project)}/{environment.Id}/deployments/{deployment.Id}/logs");
        using var clientDeploys = await client.PostAsync($"{EnvUrl(project)}/{environment.Id}/deployments", new { });
        using var clientHistory = await client.GetAsync($"{EnvUrl(project)}/{environment.Id}/deployments");
        Assert.Equal(HttpStatusCode.NotFound, clientLogs.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, clientDeploys.StatusCode);
        Assert.Equal(HttpStatusCode.OK, clientHistory.StatusCode);

        // Outsiders and other organizations cannot even tell the environment exists.
        foreach (var stranger in new[] { outsider, otherOrg })
        {
            using var list = await stranger.GetAsync(EnvUrl(project));
            using var start = await stranger.PostAsync($"{EnvUrl(project)}/{environment.Id}/deployments", new { });
            using var logs = await stranger.GetAsync($"{EnvUrl(project)}/{environment.Id}/deployments/{deployment.Id}/logs");
            Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, logs.StatusCode);
        }
    }

    [Fact]
    public async Task Deleting_an_environment_takes_the_site_down_and_needs_the_current_version()
    {
        var (lead, _) = await Given.AnOrganizationAsync(factory);
        using var _lead = lead;
        using var repo = new GitRepo("to-be-deleted");
        var project = await Given.AProjectAsync(lead);
        var environment = await CreateEnvironmentAsync(lead, project, repo);
        var deployed = await WaitForAsync(lead, project, environment, (await StartAsync(lead, project, environment)).Id, Finished);
        Assert.Equal(DeploymentStatus.Succeeded, deployed.Status);
        using var refreshed = await lead.GetAsync(EnvUrl(project));
        var current = (await ApiClient.ReadAsync<List<EnvironmentDto>>(refreshed)).Single();

        using var withoutVersion = await lead.DeleteAsync($"{EnvUrl(project)}/{environment.Id}");
        using var stale = await lead.DeleteAsync($"{EnvUrl(project)}/{environment.Id}", environment.Version);
        using var deleted = await lead.DeleteAsync($"{EnvUrl(project)}/{environment.Id}", current.Version);

        Assert.Equal((HttpStatusCode)428, withoutVersion.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await VisitAsync(environment, "/")).Status);
    }

    [Fact]
    public async Task Sites_only_answer_get_and_head_and_hide_dotfiles()
    {
        var (lead, _) = await Given.AnOrganizationAsync(factory);
        using var _lead = lead;
        using var repo = new GitRepo("static-rules");
        var project = await Given.AProjectAsync(lead);
        var environment = await CreateEnvironmentAsync(lead, project, repo, spa: true);
        await WaitForAsync(lead, project, environment, (await StartAsync(lead, project, environment)).Id, Finished);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await VisitAsync(environment, "/", HttpMethod.Post)).Status);
        var (dotStatus, dotBody) = await VisitAsync(environment, "/.secret");
        Assert.Equal(HttpStatusCode.NotFound, dotStatus);
        Assert.DoesNotContain("hidden", dotBody, StringComparison.Ordinal);
        Assert.Contains("static-rules", (await VisitAsync(environment, "/some/client/route")).Body, StringComparison.Ordinal); // SPA fallback
        Assert.Equal(HttpStatusCode.NotFound, (await VisitAsync(environment, "/missing.js")).Status);
        var (_, script) = await VisitAsync(environment, "/app.js");
        Assert.Contains("static-rules", script, StringComparison.Ordinal);

        // A site host never reaches the portal's API.
        var (apiStatus, apiBody) = await VisitAsync(environment, "/api/auth/me");
        Assert.DoesNotContain("\"userId\"", apiBody, StringComparison.Ordinal);
        Assert.NotEqual(HttpStatusCode.Unauthorized, apiStatus);
    }
}
