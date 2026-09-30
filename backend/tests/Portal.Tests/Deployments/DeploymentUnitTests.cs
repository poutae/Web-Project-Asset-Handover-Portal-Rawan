using System.Net;
using Microsoft.Extensions.Options;
using Portal.Infrastructure.Deployments;

namespace Portal.Tests.Deployments;

/// <summary>Pure logic around deployments; needs no database, network, or Linux.</summary>
public sealed class DeploymentUnitTests
{
    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("https://gitlab.com/group/sub/repo")]
    [InlineData("https://example.com:8443/x.git")]
    public void Public_https_repositories_are_accepted(string url) =>
        Assert.Null(RepositoryUrlPolicy.ValidateSyntax(url, allowLocal: false));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://github.com/org/repo.git")]
    [InlineData("ssh://git@github.com/org/repo.git")]
    [InlineData("git://github.com/org/repo.git")]
    [InlineData("ftp://example.com/repo")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("https://user:secret@github.com/org/repo.git")]
    [InlineData("https://localhost/repo.git")]
    [InlineData("https://intranet/repo.git")]
    [InlineData("https://build.internal/repo.git")]
    [InlineData("https://printer.local/repo.git")]
    [InlineData("https://github.com/org/repo with space.git")]
    public void Anything_else_is_refused(string url) =>
        Assert.NotNull(RepositoryUrlPolicy.ValidateSyntax(url, allowLocal: false));

    [Fact]
    public void Local_repositories_are_only_accepted_when_explicitly_allowed()
    {
        Assert.NotNull(RepositoryUrlPolicy.ValidateSyntax("file:///tmp/repo", allowLocal: false));
        Assert.Null(RepositoryUrlPolicy.ValidateSyntax("file:///tmp/repo", allowLocal: true));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("140.82.112.3", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Addresses_are_classified_public_or_private(string address, bool expectedPublic) =>
        Assert.Equal(expectedPublic, RepositoryUrlPolicy.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://127.0.0.1/repo.git")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/repo.git")]
    public async Task Literal_private_addresses_are_refused_without_a_lookup(string url) =>
        Assert.NotNull(await RepositoryUrlPolicy.ValidateAsync(url, allowLocal: false, TestContext.Current.CancellationToken));

    [Fact]
    public void The_redactor_removes_secrets_and_their_encoded_forms()
    {
        var redactor = new LogRedactor("supersecret-token");
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x-access-token:supersecret-token"));

        var cleaned = redactor.Redact($"token=supersecret-token; Authorization: Basic {encoded}");

        Assert.DoesNotContain("supersecret-token", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain(encoded, cleaned, StringComparison.Ordinal);
        Assert.Equal("token=***; Authorization: Basic ***", cleaned);
    }

    [Fact]
    public void The_redactor_ignores_missing_or_trivial_secrets()
    {
        var redactor = new LogRedactor(null, "", "abc");

        Assert.Equal("abc stays visible", redactor.Redact("abc stays visible"));
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("feature/new-thing", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("--upload-pack=x", false)]
    [InlineData("-b", false)]
    [InlineData("a..b", false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    [InlineData("", false)]
    public void Refs_are_validated(string value, bool valid) =>
        Assert.Equal(valid, StaticSiteDeploymentProvider.IsValidRef(value));

    [Theory]
    [InlineData("demo-site", true)]
    [InlineData("a", true)]
    [InlineData("Demo", false)]
    [InlineData("-bad", false)]
    [InlineData("bad-", false)]
    [InlineData("has.dot", false)]
    [InlineData("../etc", false)]
    [InlineData("", false)]
    public void Site_labels_are_validated(string label, bool valid) =>
        Assert.Equal(valid, DeploymentLayout.IsValidSiteLabel(label));

    [Fact]
    public void The_layout_never_builds_a_path_from_an_invalid_segment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"portal-layout-{Guid.NewGuid():N}");
        try
        {
            var layout = new DeploymentLayout(Options.Create(new DeployOptions { Root = root }));

            Assert.Throws<ArgumentException>(() => layout.SiteDirectory("../escape"));
            Assert.Throws<ArgumentException>(() => layout.ReleaseDirectory("demo", "not-a-key"));
            Assert.Throws<ArgumentException>(() => layout.ReleaseDirectory("demo", "../../../etc"));
            Assert.StartsWith(Path.GetFullPath(root), layout.ReleaseDirectory("demo", new string('a', 32)), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_live_pointer_is_written_read_and_removed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"portal-layout-{Guid.NewGuid():N}");
        try
        {
            var layout = new DeploymentLayout(Options.Create(new DeployOptions { Root = root }));
            Assert.Null(layout.ReadPointer("demo"));

            layout.WritePointer("demo", new LivePointer(new string('b', 32), SpaFallback: true));
            Assert.Equal(new LivePointer(new string('b', 32), true), layout.ReadPointer("demo"));

            File.WriteAllText(layout.PointerFile("demo"), """{"ReleaseKey":"../../etc","SpaFallback":false}""");
            Assert.Null(layout.ReadPointer("demo")); // a tampered pointer is ignored, not followed

            layout.DeletePointer("demo");
            Assert.Null(layout.ReadPointer("demo"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_systemd_sandbox_asks_only_for_the_helper_and_forwards_only_allowed_variables()
    {
        var command = SandboxCommand.Shell("/var/lib/portal/deployments/work/abc", "npm ci && npm run build", new Dictionary<string, string>(), TimeSpan.FromSeconds(900));

        var arguments = SystemdRunSandbox.BuildArguments("/usr/local/lib/portal/run-build", command, ["CI", "HOME"]);

        Assert.Equal(
            ["-n", "--preserve-env=CI,HOME", "/usr/local/lib/portal/run-build", "--workdir", "/var/lib/portal/deployments/work/abc",
             "--cwd", "/var/lib/portal/deployments/work/abc", "--timeout", "900", "--shell", "npm ci && npm run build"],
            arguments);
        Assert.DoesNotContain("LD_PRELOAD", SystemdRunSandbox.ForwardedVariables);
        Assert.DoesNotContain("PATH", SystemdRunSandbox.ForwardedVariables);
    }

    [Fact]
    public void A_git_step_is_passed_as_an_executable_with_arguments_not_a_shell_line()
    {
        var command = SandboxCommand.Exec("/work/x", "git", ["fetch", "--depth", "1", "origin", "main"], new Dictionary<string, string>(), TimeSpan.FromSeconds(60));

        var arguments = SystemdRunSandbox.BuildArguments("/h", command, []);

        Assert.Equal(["-n", "/h", "--workdir", "/work/x", "--cwd", "/work/x", "--timeout", "60", "--exec", "git", "--", "fetch", "--depth", "1", "origin", "main"], arguments);
    }
}
