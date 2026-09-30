namespace Portal.Api.Maintenance;

/// <summary>Settings for the housekeeping worker (configuration section <c>Cleanup</c>).</summary>
public sealed class CleanupOptions
{
    public const string SectionName = "Cleanup";

    /// <summary>The worker only runs when this is true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay before the first pass after the app starts, so startup is not slowed down.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Time between passes.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How long a stored idempotent response is kept. It must outlast the longest time a client can stay
    /// offline and still retry a queued change, or that retry would run twice.
    /// </summary>
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long finished (succeeded or failed) job-queue rows are kept.</summary>
    public TimeSpan JobRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// A stored file that no document points to is only removed once it is at least this old, so an upload
    /// that is still being saved is never touched. Values below one hour are raised to one hour.
    /// </summary>
    public TimeSpan OrphanGracePeriod { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// The most orphaned files removed in one pass. A safety net: if the app were ever pointed at the wrong
    /// database, it would see every file as an orphan, and this limits the damage while the log warns.
    /// </summary>
    public int MaxOrphansPerRun { get; set; } = 500;
}
