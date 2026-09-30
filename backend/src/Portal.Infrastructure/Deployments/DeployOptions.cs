namespace Portal.Infrastructure.Deployments;

/// <summary>Settings for "Deploy on Our Platform". Bound from the <c>Deploy</c> configuration section.</summary>
public sealed class DeployOptions
{
    public const string SectionName = "Deploy";

    /// <summary>Where build workspaces, stored builds, and live-site pointers are kept.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>
    /// The domain deployed sites are served under, as <c>{site-label}.{BaseDomain}</c>. Point a wildcard
    /// DNS record at the portal. Leave empty to disable serving. Use a domain that is NOT a parent of the
    /// portal's own host, so client sites cannot share cookies with the portal.
    /// </summary>
    public string BaseDomain { get; set; } = string.Empty;

    /// <summary>The scheme used in the site URLs shown to people (http for local development).</summary>
    public string PublicScheme { get; set; } = "https";

    /// <summary><c>systemd</c> (production: sudo helper + systemd-run) or <c>local-unsafe</c> (development and tests only).</summary>
    public string Sandbox { get; set; } = "systemd";

    public string SudoHelperPath { get; set; } = "/usr/local/lib/portal/run-build";

    public int BuildTimeoutSeconds { get; set; } = 900;

    public long MaxArtifactBytes { get; set; } = 200L * 1024 * 1024;

    public int MaxArtifactFiles { get; set; } = 20_000;

    /// <summary>How many stored builds to keep per site (for rollback). The live one is always kept.</summary>
    public int KeepReleases { get; set; } = 5;

    /// <summary>Accept <c>file://</c> repositories. For development and tests only.</summary>
    public bool AllowLocalRepositories { get; set; }

    /// <summary>Overrides the address the health check calls (default: this server's own listening address).</summary>
    public string? HealthCheckBaseUrl { get; set; }

    public int MaxConcurrentBuilds { get; set; } = 1;

    public int HealthCheckAttempts { get; set; } = 5;
}
