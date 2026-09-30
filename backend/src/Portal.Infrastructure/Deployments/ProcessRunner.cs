using System.Diagnostics;

namespace Portal.Infrastructure.Deployments;

/// <summary>Starts a process, streams its output, and kills its whole tree on timeout or cancellation.</summary>
internal static class ProcessRunner
{
    public static async Task<SandboxResult> RunAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, Action<string, bool> output, CancellationToken ct)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        using var process = new Process { StartInfo = startInfo };
        var lines = new List<string>();
        var gate = new object();

        void Capture(string? line, bool isError)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                if (!isError && lines.Count < 200)
                {
                    lines.Add(line);
                }

                output(line, isError);
            }
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data, false);
        process.ErrorDataReceived += (_, e) => Capture(e.Data, true);

        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            process.WaitForExit(); // let the output readers drain
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already exited
            }

            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            return new SandboxResult(-1, TimedOut: true, lines);
        }

        return new SandboxResult(process.ExitCode, TimedOut: false, lines);
    }
}
