namespace Portal.Domain;

/// <summary>
/// Remembers the outcome of a mutation so that a retry carrying the same <c>Idempotency-Key</c> replays
/// the original response instead of running the mutation again.
/// </summary>
public sealed class IdempotencyRecord : Entity, ITenantScoped
{
    public Guid OrganizationId { get; set; }

    public Guid UserId { get; init; }

    public required string Key { get; init; }

    /// <summary>Hash of method, path, and body, used to reject a key that is reused for a different request.</summary>
    public required byte[] RequestHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Null while the first request is still running.</summary>
    public int? StatusCode { get; set; }

    public string? ContentType { get; set; }

    public byte[]? ResponseBody { get; set; }

    public string? Location { get; set; }

    public string? ETag { get; set; }
}
