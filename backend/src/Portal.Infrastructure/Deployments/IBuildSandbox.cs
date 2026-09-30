namespace Portal.Infrastructure.Deployments;

/// <summary>One command to run inside the sandbox: either an executable with arguments, or a shell line.</summary>
public sealed record SandboxCommand(
    string WorkingDirectory,
    string? Executable,
    IReadOnlyList<string> Arguments,
    string? ShellCommand,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout)
{
    public static SandboxCommand Exec(string workingDirectory, string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string, string> environment, TimeSpan timeout) =>
        new(workingDirectory, executable, arguments.ToList(), null, environment, timeout);

    public static SandboxCommand Shell(string workingDirectory, string command, IReadOnlyDictionary<string, string> environment, TimeSpan timeout) =>
        new(workingDirectory, null, [], command, environment, timeout);
}

public sealed record SandboxResult(int ExitCode, bool TimedOut, IReadOnlyList<string> OutputLines);

/// <summary>
/// Runs untrusted client build code somewhere it cannot harm the portal or other projects. Output is
/// streamed to <paramref name="output"/> as it is produced (<c>true</c> = stderr).
/// </summary>
public interface IBuildSandbox
{
    string Name { get; }

    Task<SandboxResult> RunAsync(SandboxCommand command, Action<string, bool> output, CancellationToken ct);
}
