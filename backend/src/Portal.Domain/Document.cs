namespace Portal.Domain;

/// <summary>Who may see a document.</summary>
public enum DocumentVisibility
{
    /// <summary>Agency staff only.</summary>
    Internal = 1,

    /// <summary>Staff and the project's clients.</summary>
    Client = 2,
}

/// <summary>Metadata for an uploaded file. The bytes live in file storage under <see cref="StorageKey"/>.</summary>
public sealed class Document : Entity, ITenantScoped
{
    public Guid OrganizationId { get; set; }

    public Guid ProjectId { get; init; }

    public required string Title { get; set; }

    /// <summary>The sanitized name the file had when uploaded; used only as the download name.</summary>
    public required string FileName { get; init; }

    /// <summary>Assigned by the server from the validated extension, never taken from the client.</summary>
    public required string ContentType { get; init; }

    public long SizeBytes { get; init; }

    public required byte[] Sha256 { get; init; }

    /// <summary>A random, server-generated key. It contains no user input.</summary>
    public required string StorageKey { get; init; }

    public DocumentVisibility Visibility { get; set; }

    public Guid UploadedByUserId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}
