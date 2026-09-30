namespace Portal.Api.Realtime;

public static class RealtimeKinds
{
    public const string Project = "project";
    public const string Member = "member";
    public const string Milestone = "milestone";
    public const string Note = "note";
    public const string Document = "document";
    public const string Environment = "environment";
    public const string Deployment = "deployment";
}

public static class RealtimeActions
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";
}

/// <summary>
/// A change notification. It deliberately carries no content: clients re-fetch through the normal,
/// authorized API, so realtime can never leak data the caller could not read anyway.
/// </summary>
public sealed record RealtimeEvent(
    string Kind,
    string Action,
    Guid ProjectId,
    Guid EntityId,
    string? Version,
    string? OriginClientId,
    DateTimeOffset At);
