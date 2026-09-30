using System.Diagnostics;

namespace Portal.Tests.Deployments;

/// <summary>A throwaway local Git repository holding a tiny "site" that builds with node, so the whole fetch-and-build path is real.</summary>
public sealed class GitRepo : IDisposable
{
    public const string BuildCommand = "node build.js";

    public GitRepo(string marker = "version-one", string? buildScript = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"portal-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        Git("init", "-q", "-b", "main");
        Commit(marker, buildScript);
    }

    public string Path { get; }

    /// <summary>The <c>file://</c> URL the platform is given (only accepted when local repositories are enabled).</summary>
    public string Url => new Uri(Path + System.IO.Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');

    public string HeadSha => Git("rev-parse", "HEAD").Trim();

    /// <summary>Writes a new version of the site and commits it; returns the new commit.</summary>
    public string Commit(string marker, string? buildScript = null)
    {
        File.WriteAllText(
            System.IO.Path.Combine(Path, "build.js"),
            buildScript ?? $$"""
                const fs = require('fs');
                fs.mkdirSync('dist/about', { recursive: true });
                fs.writeFileSync('dist/index.html', '<!doctype html><title>site</title><p>{{marker}}</p>');
                fs.writeFileSync('dist/about/index.html', '<!doctype html><p>about {{marker}}</p>');
                fs.writeFileSync('dist/app.js', 'console.log("{{marker}}")');
                fs.writeFileSync('dist/.secret', 'hidden');
                console.log('build finished: {{marker}}');
                """);
        File.WriteAllText(System.IO.Path.Combine(Path, "package.json"), """{ "name": "fixture", "version": "1.0.0", "private": true }""");
        Git("add", "-A");
        Git("-c", "user.name=Test", "-c", "user.email=test@example.test", "commit", "-q", "-m", marker);
        return HeadSha;
    }

    public void Dispose()
    {
        // Git marks object files read-only on Windows.
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Path, recursive: true);
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }
}
