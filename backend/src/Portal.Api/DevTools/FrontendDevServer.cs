using System.Diagnostics;
using System.Net.Sockets;

namespace Portal.Api.DevTools;

/// <summary>
/// Development convenience: starts the Vite dev server together with the API, so pressing F5 in Visual
/// Studio gives a working app. It is only registered when <c>Frontend:DevServer:Enabled</c> is true (the
/// Visual Studio launch profile sets it), installs the npm packages on the first run, and holds back the API
/// start-up until the frontend answers, so the browser Visual Studio opens finds it ready. Problems are
/// logged and never stop the API. Not used in tests or production.
/// </summary>
public sealed class FrontendDevServer(
    IHostEnvironment environment, IConfiguration configuration, ILogger<FrontendDevServer> logger)
    : IHostedLifecycleService, IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(90);

    private Process? _vite;

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StartViteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not start the frontend dev server. Run `npm run dev` in the frontend folder yourself.");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => Stop();

    private async Task StartViteAsync(CancellationToken ct)
    {
        var port = int.TryParse(configuration["VITE_DEV_PORT"], out var configured) ? configured : 5173;
        if (await IsListeningAsync(port, ct))
        {
            logger.LogInformation("The frontend is already running on port {Port}; not starting another one", port);
            return;
        }

        var frontend = FindFrontendDirectory();
        if (frontend is null)
        {
            logger.LogWarning("Could not find the frontend folder next to the backend. Run `npm run dev` in it yourself.");
            return;
        }

        if (!Directory.Exists(Path.Combine(frontend, "node_modules")))
        {
            logger.LogInformation("Installing the frontend packages (first run only; this can take a minute)");
            using var install = Launch(frontend, "ci");
            await install.WaitForExitAsync(ct);
            if (install.ExitCode != 0)
            {
                logger.LogError("`npm ci` failed (exit code {Code}); the frontend was not started. Is Node.js 22 or newer installed?", install.ExitCode);
                return;
            }
        }

        logger.LogInformation("Starting the frontend dev server on port {Port}", port);
        _vite = Launch(frontend, "run dev");

        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_vite.HasExited)
            {
                logger.LogError("The frontend dev server stopped right after starting (exit code {Code})", _vite.ExitCode);
                return;
            }

            if (await IsListeningAsync(port, ct))
            {
                logger.LogInformation("The frontend is ready at http://localhost:{Port}", port);
                return;
            }

            await Task.Delay(300, ct);
        }

        logger.LogWarning("The frontend dev server did not answer within {Seconds} seconds", ReadyTimeout.TotalSeconds);
    }

    private string? FindFrontendDirectory()
    {
        if (configuration["Frontend:DevServer:Path"] is { Length: > 0 } explicitPath)
        {
            return Directory.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        }

        for (var directory = new DirectoryInfo(environment.ContentRootPath); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "frontend");
            if (File.Exists(Path.Combine(candidate, "package.json")))
            {
                return candidate;
            }
        }

        return null;
    }

    private Process Launch(string workingDirectory, string npmArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // npm is a .cmd script on Windows, which only a shell can run.
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/d /s /c \"npm {npmArguments}\"";
        }
        else
        {
            startInfo.FileName = "npm";
            startInfo.Arguments = npmArguments;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Relay(e.Data, isError: false);
        process.ErrorDataReceived += (_, e) => Relay(e.Data, isError: true);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private void Relay(string? line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (isError)
        {
            logger.LogWarning("[frontend] {Line}", line);
        }
        else
        {
            logger.LogInformation("[frontend] {Line}", line);
        }
    }

    private static async Task<bool> IsListeningAsync(int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            await client.ConnectAsync("localhost", port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private void Stop()
    {
        var process = Interlocked.Exchange(ref _vite, null);
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }
}
