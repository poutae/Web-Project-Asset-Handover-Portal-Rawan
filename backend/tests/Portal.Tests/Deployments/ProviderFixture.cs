using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Portal.Infrastructure.Deployments;

namespace Portal.Tests.Deployments;

/// <summary>Everything the platform provider needs, on a temporary folder, using the (unsafe, local) sandbox.</summary>
public sealed class ProviderFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"portal-deploy-{Guid.NewGuid():N}");

    public ProviderFixture(Func<HttpClient>? healthClient = null, Action<DeployOptions>? configure = null)
    {
        Options = new DeployOptions
        {
            Root = _root,
            BaseDomain = "sites.test",
            Sandbox = "local-unsafe",
            AllowLocalRepositories = true,
            BuildTimeoutSeconds = 120,
            HealthCheckAttempts = 2,
        };
        configure?.Invoke(Options);
        Layout = new DeploymentLayout(Microsoft.Extensions.Options.Options.Create(Options));
        Provider = new StaticSiteDeploymentProvider(
            Microsoft.Extensions.Options.Options.Create(Options),
            Layout,
            new LocalProcessSandbox(),
            new StubHttpClientFactory(healthClient ?? (() => new HttpClient())),
            new EmptyServiceProvider(),
            NullLogger<StaticSiteDeploymentProvider>.Instance);
    }

    public DeployOptions Options { get; }

    public DeploymentLayout Layout { get; }

    public StaticSiteDeploymentProvider Provider { get; }

    public static DeploymentRequest Request(GitRepo repo, string? command = null, string output = "dist", string? gitRef = "main", string label = "demo-site", string? token = null) =>
        new(Guid.NewGuid(), label, repo.Url, token, gitRef ?? "main", command ?? GitRepo.BuildCommand, output);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class StubHttpClientFactory(Func<HttpClient> create) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => create();
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

public sealed class CapturingLog : IDeploymentLog
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines;

    public string Text => string.Join('\n', _lines);

    public void System(string message) => _lines.Add($"[system] {message}");

    public void Output(string line, bool isError) => _lines.Add($"[{(isError ? "err" : "out")}] {line}");
}
