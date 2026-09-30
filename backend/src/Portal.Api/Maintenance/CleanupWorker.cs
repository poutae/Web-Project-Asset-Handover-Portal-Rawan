using Microsoft.Extensions.Options;

namespace Portal.Api.Maintenance;

/// <summary>Runs <see cref="CleanupRunner"/> on a schedule for as long as the app is up.</summary>
public sealed class CleanupWorker(
    CleanupRunner runner,
    IOptions<CleanupOptions> options,
    TimeProvider clock,
    ILogger<CleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("The cleanup worker is disabled (Cleanup:Enabled=false)");
            return;
        }

        var interval = settings.Interval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : settings.Interval;
        try
        {
            await Task.Delay(settings.InitialDelay < TimeSpan.Zero ? TimeSpan.Zero : settings.InitialDelay, stoppingToken);
            using var timer = new PeriodicTimer(interval);
            do
            {
                try
                {
                    await runner.RunOnceAsync(clock.GetUtcNow(), stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A pass that fails (for example while the database restarts) is simply tried again later.
                    logger.LogError(ex, "The cleanup pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The app is shutting down.
        }
    }
}
