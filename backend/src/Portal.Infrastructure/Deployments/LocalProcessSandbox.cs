using System.Diagnostics;

namespace Portal.Infrastructure.Deployments;

/// <summary>
/// Runs the build as an ordinary child process of the portal with a scrubbed environment. It provides NO
/// isolation and exists only so the pipeline can be run and tested on a developer machine or CI runner.
/// The application refuses to use it in production.
/// </summary>
public sealed class LocalProcessSandbox : IBuildSandbox
{
    public string Name => "local-unsafe";

    public Task<SandboxResult> RunAsync(SandboxCommand command, Action<string, bool> output, CancellationToken ct)
    {
        var home = Path.Combine(command.WorkingDirectory, ".home");
        var temp = Path.Combine(command.WorkingDirectory, ".tmp");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(temp);

        var startInfo = new ProcessStartInfo { WorkingDirectory = command.WorkingDirectory };
        if (command.ShellCommand is { } shell)
        {
            if (OperatingSystem.IsWindows())
            {
                // cmd.exe does not understand the backslash-escaped quotes that ArgumentList would produce, so
                // hand it the raw line; /s makes it strip exactly the outer pair of quotes and run the rest as is.
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = $"/d /s /c \"{shell}\"";
            }
            else
            {
                startInfo.FileName = "/bin/sh";
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(shell);
            }
        }
        else
        {
            startInfo.FileName = command.Executable ?? throw new ArgumentException("No command to run.");
            foreach (var argument in command.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        startInfo.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "ComSpec", "PATHEXT", "windir" })
        {
            if (System.Environment.GetEnvironmentVariable(name) is { } value)
            {
                startInfo.Environment[name] = value;
            }
        }

        startInfo.Environment["HOME"] = home;
        startInfo.Environment["USERPROFILE"] = home;
        startInfo.Environment["TEMP"] = temp;
        startInfo.Environment["TMP"] = temp;
        startInfo.Environment["TMPDIR"] = temp;
        startInfo.Environment["CI"] = "true";
        foreach (var (name, value) in command.Environment)
        {
            startInfo.Environment[name] = value;
        }

        return ProcessRunner.RunAsync(startInfo, command.Timeout, output, ct);
    }
}
