namespace Portal.Infrastructure.Deployments;

/// <summary>Receives the human-readable progress of a deployment.</summary>
public interface IDeploymentLog
{
    void System(string message);

    void Output(string line, bool isError);
}

public sealed record DeploymentRequest(
    Guid DeploymentId,
    string SiteLabel,
    string RepositoryUrl,
    string? AccessToken,
    string Ref,
    string BuildCommand,
    string OutputDirectory);

public sealed record BuildOutput(string ReleaseKey, string CommitSha, long SizeBytes, int FileCount);

public sealed record SiteTarget(string SiteLabel, bool SpaFallback);

public sealed record HealthCheckSpec(string Path, int Attempts);

public sealed record HealthCheckResult(bool Healthy, string Detail);

/// <summary>
/// Turns a Git repository into a live site. Implementations are responsible for isolating client build
/// code from the portal and from other projects. The platform provider serves static sites itself;
/// another provider could push to a CDN or a container host without changing the pipeline around it.
/// </summary>
public interface IDeploymentProvider
{
    string Name { get; }

    /// <summary>Fetches the source, runs the build, and stores the output as a new release. Never fakes success.</summary>
    Task<BuildOutput> BuildAsync(DeploymentRequest request, IDeploymentLog log, CancellationToken ct);

    bool ReleaseExists(string siteLabel, string releaseKey);

    /// <summary>Makes the stored release the live one, atomically.</summary>
    Task ActivateAsync(SiteTarget site, string releaseKey, CancellationToken ct);

    /// <summary>Takes the site offline.</summary>
    Task DeactivateAsync(SiteTarget site, CancellationToken ct);

    /// <summary>Requests the live site over HTTP the way a visitor would.</summary>
    Task<HealthCheckResult> CheckHealthAsync(SiteTarget site, HealthCheckSpec spec, CancellationToken ct);

    Task RemoveReleaseAsync(string siteLabel, string releaseKey, CancellationToken ct);

    IReadOnlyList<string> ListReleases(string siteLabel);
}

public sealed class DeploymentFailedException(string message) : Exception(message);
