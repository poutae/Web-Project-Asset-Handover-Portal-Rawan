using System.Threading.Channels;
using Portal.Domain;
using Portal.Infrastructure.Deployments;
using Portal.Infrastructure.Persistence;

namespace Portal.Api.Deployments;

/// <summary>
/// Collects a deployment's log lines from the build process and writes them to the database in batches.
/// It uses its own database scope, so it can run while the pipeline is doing other work. Secrets are
/// removed before anything is stored, and the log is capped so a runaway build cannot fill the database.
/// </summary>
public sealed class DeploymentLogSink : IDeploymentLog, IAsyncDisposable
{
    public const int MaxLines = 10_000;
    public const int MaxLineLength = 4000;
    private const int BatchSize = 100;

    private readonly IServiceScopeFactory _scopes;
    private readonly Guid _organizationId;
    private readonly Guid _deploymentId;
    private readonly LogRedactor _redactor;
    private readonly TimeProvider _clock;
    private readonly Channel<(DateTimeOffset At, LogChannel Channel, string Message)> _queue =
        Channel.CreateUnbounded<(DateTimeOffset, LogChannel, string)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private int _accepted;

    public DeploymentLogSink(
        IServiceScopeFactory scopes, Guid organizationId, Guid deploymentId, LogRedactor redactor, TimeProvider clock)
    {
        _scopes = scopes;
        _organizationId = organizationId;
        _deploymentId = deploymentId;
        _redactor = redactor;
        _clock = clock;
        _writer = Task.Run(WriteLoopAsync);
    }

    public void System(string message) => Enqueue(LogChannel.System, message);

    public void Output(string line, bool isError) => Enqueue(isError ? LogChannel.Stderr : LogChannel.Stdout, line);

    private void Enqueue(LogChannel channel, string message)
    {
        var count = Interlocked.Increment(ref _accepted);
        if (count > MaxLines + 1)
        {
            return;
        }

        if (count == MaxLines + 1)
        {
            message = "Log limit reached; further output was discarded.";
            channel = LogChannel.System;
        }

        var clean = _redactor.Redact(message).Replace("\0", string.Empty, StringComparison.Ordinal);
        if (clean.Length > MaxLineLength)
        {
            clean = string.Concat(clean.AsSpan(0, MaxLineLength - 1), "…");
        }

        _queue.Writer.TryWrite((_clock.GetUtcNow(), channel, clean));
    }

    /// <summary>Writes everything that has been logged so far and stops.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writer;
    }

    private async Task WriteLoopAsync()
    {
        var batch = new List<DeploymentLogLine>(BatchSize);
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync())
        {
            // Give bursts a moment to accumulate so we write in batches, not one row per line.
            await Task.Delay(200);
            while (batch.Count < BatchSize && reader.TryRead(out var item))
            {
                batch.Add(new DeploymentLogLine
                {
                    DeploymentId = _deploymentId,
                    OrganizationId = _organizationId,
                    At = item.At,
                    Channel = item.Channel,
                    Message = item.Message,
                });
            }

            if (batch.Count > 0)
            {
                await FlushAsync(batch);
                batch.Clear();
            }
        }
    }

    private async Task FlushAsync(List<DeploymentLogLine> batch)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
            using (tenant.Use(_organizationId))
            {
                db.DeploymentLogLines.AddRange(batch);
                await db.SaveChangesAsync();
            }

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Losing log lines must never fail a deployment.
            using var scope = _scopes.CreateScope();
            scope.ServiceProvider.GetService<ILogger<DeploymentLogSink>>()
                ?.LogWarning(ex, "Could not store {Count} log lines for deployment {DeploymentId}", batch.Count, _deploymentId);
        }
    }
}
