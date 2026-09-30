using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Portal.Infrastructure.Deployments;

/// <summary>
/// "Deploy on Our Platform" for static sites: fetch a Git repository, build it in the sandbox, store the
/// output as an immutable release, and serve the live release by hostname. Nothing here reports success
/// unless the build ran, produced a real site, and the live URL answered a real HTTP request.
/// </summary>
public sealed partial class StaticSiteDeploymentProvider(
    IOptions<DeployOptions> options,
    DeploymentLayout layout,
    IBuildSandbox sandbox,
    IHttpClientFactory httpClients,
    IServiceProvider services,
    ILogger<StaticSiteDeploymentProvider> logger) : IDeploymentProvider
{
    public const string HealthClientName = "deploy-health";

    private readonly DeployOptions _options = options.Value;

    public string Name => "platform-static";

    public static bool IsValidRef(string value) => RefPattern().IsMatch(value) && !value.Contains("..", StringComparison.Ordinal);

    public async Task<BuildOutput> BuildAsync(DeploymentRequest request, IDeploymentLog log, CancellationToken ct)
    {
        if (!IsValidRef(request.Ref))
        {
            throw new DeploymentFailedException("The branch, tag, or commit is not valid.");
        }

        var work = layout.WorkDirectory(request.DeploymentId);
        var repo = Path.Combine(work, "repo");
        Directory.CreateDirectory(repo);

        var redactor = new LogRedactor(request.AccessToken);
        void Forward(string line, bool isError) => log.Output(redactor.Redact(line), isError);
        var timeout = TimeSpan.FromSeconds(_options.BuildTimeoutSeconds);
        var environment = GitEnvironment(request.AccessToken);

        try
        {
            log.System($"Fetching {request.RepositoryUrl} at '{request.Ref}' using the '{sandbox.Name}' sandbox.");
            var fileProtocol = _options.AllowLocalRepositories ? "always" : "never";
            string[] git = ["-c", "protocol.ext.allow=never", "-c", $"protocol.file.allow={fileProtocol}", "-c", "core.fsmonitor=false", "-c", "advice.detachedHead=false"];

            await RunGitAsync(["init", "-q", "--template="], "initialise the repository");
            await RunGitAsync(["remote", "add", "origin", request.RepositoryUrl], "add the remote");
            await RunGitAsync(["fetch", "--depth", "1", "--no-tags", "origin", request.Ref], "fetch the source (check the URL, branch, and access token)");
            await RunGitAsync(["checkout", "--detach", "FETCH_HEAD"], "check out the source");
            var revision = await RunStepAsync(SandboxCommand.Exec(repo, "git", ["rev-parse", "HEAD"], environment, timeout), "read the commit");
            var commitSha = revision.OutputLines.LastOrDefault(l => CommitPattern().IsMatch(l.Trim()))?.Trim()
                ?? throw new DeploymentFailedException("Could not determine the commit that was built.");
            log.System($"Building commit {commitSha[..Math.Min(12, commitSha.Length)]}.");

            log.System($"Running: {request.BuildCommand}");
            await RunStepAsync(SandboxCommand.Shell(repo, request.BuildCommand, environment, timeout), "run the build command");

            log.System($"Collecting '{request.OutputDirectory}'.");
            var output = ResolveOutputDirectory(repo, request.OutputDirectory);
            var release = StoreRelease(request, output, log, commitSha);
            return release;

            async Task RunGitAsync(string[] arguments, string purpose) =>
                await RunStepAsync(SandboxCommand.Exec(repo, "git", [.. git, .. arguments], environment, timeout), purpose);
        }
        finally
        {
            TryDeleteDirectory(work);
        }

        async Task<SandboxResult> RunStepAsync(SandboxCommand command, string purpose)
        {
            var result = await sandbox.RunAsync(command, Forward, ct);
            if (result.TimedOut)
            {
                throw new DeploymentFailedException($"Timed out after {_options.BuildTimeoutSeconds} seconds while trying to {purpose}.");
            }

            if (result.ExitCode != 0)
            {
                throw new DeploymentFailedException($"Could not {purpose} (exit code {result.ExitCode}).");
            }

            return result;
        }
    }

    public bool ReleaseExists(string siteLabel, string releaseKey) =>
        DeploymentLayout.IsValidSiteLabel(siteLabel) && DeploymentLayout.IsValidReleaseKey(releaseKey)
        && Directory.Exists(layout.ReleaseDirectory(siteLabel, releaseKey));

    public Task ActivateAsync(SiteTarget site, string releaseKey, CancellationToken ct)
    {
        if (!ReleaseExists(site.SiteLabel, releaseKey))
        {
            throw new DeploymentFailedException("The stored build for this release is no longer available.");
        }

        layout.WritePointer(site.SiteLabel, new LivePointer(releaseKey, site.SpaFallback));
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(SiteTarget site, CancellationToken ct)
    {
        layout.DeletePointer(site.SiteLabel);
        return Task.CompletedTask;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(SiteTarget site, HealthCheckSpec spec, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseDomain))
        {
            return new HealthCheckResult(false, "Site serving is not configured (Deploy:BaseDomain is empty).");
        }

        var baseUrl = ResolveBaseUrl();
        var detail = "no attempt was made";
        for (var attempt = 1; attempt <= Math.Max(1, spec.Attempts); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = httpClients.CreateClient(HealthClientName);
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), spec.Path));
                request.Headers.Host = $"{site.SiteLabel}.{_options.BaseDomain}";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (status is >= 200 and < 400)
                {
                    return new HealthCheckResult(true, $"GET {spec.Path} returned {status}.");
                }

                detail = $"GET {spec.Path} returned {status}.";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                detail = $"GET {spec.Path} failed: {ex.Message}";
            }

            if (attempt < spec.Attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }

        return new HealthCheckResult(false, detail);
    }

    public Task RemoveReleaseAsync(string siteLabel, string releaseKey, CancellationToken ct)
    {
        var path = layout.ReleaseDirectory(siteLabel, releaseKey);
        TryDeleteDirectory(path);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> ListReleases(string siteLabel)
    {
        var directory = layout.SiteDirectory(siteLabel);
        return Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory).Select(Path.GetFileName).OfType<string>().Where(DeploymentLayout.IsValidReleaseKey).ToList()
            : [];
    }

    private static Dictionary<string, string> GitEnvironment(string? accessToken)
    {
        var environment = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["npm_config_update_notifier"] = "false",
        };

        if (!string.IsNullOrEmpty(accessToken))
        {
            // Passed through the environment, never the command line, so it cannot show up in a process list.
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{accessToken}"));
            environment["GIT_CONFIG_COUNT"] = "1";
            environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            environment["GIT_CONFIG_VALUE_0"] = $"Authorization: Basic {credentials}";
        }

        return environment;
    }

    private static string ResolveOutputDirectory(string repo, string outputDirectory)
    {
        var root = Path.GetFullPath(repo) + Path.DirectorySeparatorChar;
        var output = Path.GetFullPath(Path.Combine(repo, outputDirectory));
        if (!(output + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.Ordinal) || output + Path.DirectorySeparatorChar == root)
        {
            throw new DeploymentFailedException("The output directory must be a folder inside the repository.");
        }

        var directory = new DirectoryInfo(output);
        if (!directory.Exists)
        {
            throw new DeploymentFailedException($"The build finished but did not produce the '{outputDirectory}' folder.");
        }

        if (directory.LinkTarget is not null)
        {
            throw new DeploymentFailedException("The output directory must not be a symbolic link.");
        }

        if (!File.Exists(Path.Combine(output, "index.html")))
        {
            throw new DeploymentFailedException($"The '{outputDirectory}' folder has no index.html, so there is no site to serve.");
        }

        return output;
    }

    private BuildOutput StoreRelease(DeploymentRequest request, string output, IDeploymentLog log, string commitSha)
    {
        var releaseKey = request.DeploymentId.ToString("N");
        var siteDirectory = layout.SiteDirectory(request.SiteLabel);
        var final = layout.ReleaseDirectory(request.SiteLabel, releaseKey);
        var partial = $"{final}.partial";
        Directory.CreateDirectory(siteDirectory);
        TryDeleteDirectory(partial);
        Directory.CreateDirectory(partial);

        long totalBytes = 0;
        var fileCount = 0;
        try
        {
            var root = new DirectoryInfo(output);
            foreach (var entry in root.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                // Build code controls this folder. A link could point anywhere on the server, so none are copied.
                if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new DeploymentFailedException($"The build output contains a symbolic link ('{Path.GetRelativePath(output, entry.FullName)}'), which is not allowed.");
                }

                var relative = Path.GetRelativePath(output, entry.FullName);
                var target = Path.Combine(partial, relative);
                if (entry is DirectoryInfo)
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                var file = (FileInfo)entry;
                fileCount++;
                totalBytes += file.Length;
                if (fileCount > _options.MaxArtifactFiles || totalBytes > _options.MaxArtifactBytes)
                {
                    throw new DeploymentFailedException($"The build output is too large (limit {_options.MaxArtifactFiles} files / {_options.MaxArtifactBytes / (1024 * 1024)} MB).");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                file.CopyTo(target);
            }

            Directory.Move(partial, final);
        }
        catch
        {
            TryDeleteDirectory(partial);
            throw;
        }

        log.System($"Stored release {releaseKey[..8]} ({fileCount} files, {totalBytes / 1024} KB).");
        return new BuildOutput(releaseKey, commitSha, totalBytes, fileCount);
    }

    private string ResolveBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(_options.HealthCheckBaseUrl))
        {
            return _options.HealthCheckBaseUrl.TrimEnd('/') + "/";
        }

        var address = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        if (address is null)
        {
            return "http://localhost/";
        }

        // Kestrel reports wildcard bindings; a visitor-style request from the same machine uses loopback.
        var loopback = WildcardHost().Replace(address, "127.0.0.1");
        return loopback.TrimEnd('/') + "/";
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,199}$")]
    private static partial Regex RefPattern();

    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$")]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"(?<=//)(\+|\*|0\.0\.0\.0|\[::\]|\[::0\])")]
    private static partial Regex WildcardHost();
}
