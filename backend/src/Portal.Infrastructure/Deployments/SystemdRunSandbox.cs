using System.Diagnostics;

namespace Portal.Infrastructure.Deployments;

/// <summary>
/// Production sandbox. The portal (an unprivileged user) may run exactly one fixed helper through sudo;
/// the helper launches the step with <c>systemd-run</c> as a separate low-privilege build user, with CPU,
/// memory, task and time limits, a private /tmp, a read-only view of the system, write access to the one
/// workspace only, and no route to the portal's own network services. See <c>deploy/README.md</c>.
/// The helper decides what is allowed; this class only asks.
/// </summary>
public sealed class SystemdRunSandbox(string helperPath) : IBuildSandbox
{
    /// <summary>The only environment variables that are passed through to the build.</summary>
    public static readonly IReadOnlyCollection<string> ForwardedVariables =
    [
        "CI", "HOME", "TMPDIR", "GIT_TERMINAL_PROMPT", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0",
        "GIT_CONFIG_KEY_1", "GIT_CONFIG_VALUE_1", "npm_config_cache", "npm_config_update_notifier",
    ];

    public string Name => "systemd";

    public Task<SandboxResult> RunAsync(SandboxCommand command, Action<string, bool> output, CancellationToken ct)
    {
        var environment = new Dictionary<string, string>(command.Environment)
        {
            ["CI"] = "true",
            ["HOME"] = Path.Combine(command.WorkingDirectory, ".home"),
            ["TMPDIR"] = "/tmp",
        };
        var forwarded = environment.Keys.Where(k => ForwardedVariables.Contains(k)).Order().ToList();

        var startInfo = new ProcessStartInfo("sudo");
        foreach (var argument in BuildArguments(helperPath, command, forwarded))
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in forwarded)
        {
            startInfo.Environment[name] = environment[name];
        }

        return ProcessRunner.RunAsync(startInfo, command.Timeout + TimeSpan.FromSeconds(30), output, ct);
    }

    /// <summary>The exact sudo command line. Public so it can be verified without a Linux host.</summary>
    public static IReadOnlyList<string> BuildArguments(string helperPath, SandboxCommand command, IReadOnlyCollection<string> forwardedVariables)
    {
        var arguments = new List<string> { "-n" };
        if (forwardedVariables.Count > 0)
        {
            arguments.Add($"--preserve-env={string.Join(',', forwardedVariables)}");
        }

        arguments.Add(helperPath);
        arguments.Add("--workdir");
        arguments.Add(command.WorkingDirectory);
        arguments.Add("--cwd");
        arguments.Add(command.WorkingDirectory);
        arguments.Add("--timeout");
        arguments.Add(((int)command.Timeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (command.ShellCommand is { } shell)
        {
            arguments.Add("--shell");
            arguments.Add(shell);
        }
        else
        {
            arguments.Add("--exec");
            arguments.Add(command.Executable ?? throw new ArgumentException("No command to run."));
            arguments.Add("--");
            arguments.AddRange(command.Arguments);
        }

        return arguments;
    }
}
