using Portal.Domain;

namespace Portal.Api.Contracts;

public sealed record RegisterRequest(string? OrganizationName, string? DisplayName, string? Email, string? Password);

public sealed record LoginRequest(string? Email, string? Password, bool RememberMe = false);

public sealed record CsrfResponse(string Token);

public sealed record OrganizationDto(Guid Id, string Name, string Slug);

public sealed record MeResponse(Guid UserId, string Email, string DisplayName, OrgRole Role, OrganizationDto Organization);

public sealed record MemberDto(Guid Id, string Email, string DisplayName, OrgRole Role);

public sealed record CreateInvitationRequest(string? Email, OrgRole Role);

public sealed record InvitationDto(
    Guid Id, string Email, OrgRole Role, InvitationStatus Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>The secret <see cref="Token"/> is returned exactly once, when the invitation is created.</summary>
public sealed record CreatedInvitationDto(
    Guid Id, string Email, OrgRole Role, DateTimeOffset ExpiresAt, string Token);

public sealed record AcceptInvitationRequest(string? Token, string? DisplayName, string? Password);

public sealed record ProjectDto(
    Guid Id,
    string Name,
    string Description,
    ProjectStatus Status,
    DateTimeOffset CreatedAt,
    ProjectRole? MyRole,
    string Version);

public sealed record CreateProjectRequest(string? Name, string? Description);

public sealed record UpdateProjectRequest(string? Name, string? Description, ProjectStatus Status);

public sealed record ProjectMemberDto(Guid UserId, string DisplayName, string Email, ProjectRole Role, string Version);

public sealed record AddProjectMemberRequest(Guid UserId, ProjectRole Role);

public sealed record UpdateProjectMemberRequest(ProjectRole Role);

public sealed record MilestoneDto(
    Guid Id,
    Guid ProjectId,
    string Title,
    string Description,
    DateOnly? DueDate,
    MilestoneStatus Status,
    DateTimeOffset CreatedAt,
    string Version);

public sealed record SaveMilestoneRequest(string? Title, string? Description, DateOnly? DueDate, MilestoneStatus Status);

public sealed record NoteDto(
    Guid Id,
    Guid ProjectId,
    Guid AuthorUserId,
    string AuthorName,
    string Body,
    NoteVisibility Visibility,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Version);

public sealed record SaveNoteRequest(string? Body, NoteVisibility Visibility);

public sealed record DocumentDto(
    Guid Id,
    Guid ProjectId,
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DocumentVisibility Visibility,
    Guid UploadedByUserId,
    string UploadedByName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Version);

public sealed record UpdateDocumentRequest(string? Title, DocumentVisibility Visibility);
