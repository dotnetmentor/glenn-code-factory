namespace Source.Features.RuntimeTemplates.Models;

/// <summary>Body for registering a freshly-built template box into the catalog.</summary>
public record RegisterRuntimeTemplateRequest(
    string BoxId,
    string Label,
    string GitSha,
    DateTime BuiltAt,
    string? Notes);

/// <summary>Paged list envelope for the admin templates table.</summary>
public record RuntimeTemplatesResponse(
    List<RuntimeTemplate> Items,
    int Total,
    int Page,
    int PageSize);

/// <summary>Body for the status-update endpoint.</summary>
public record UpdateRuntimeTemplateStatusRequest(RuntimeTemplateStatus Status);

/// <summary>
/// A live box on the account as shown in the super-admin "pick a template to
/// register" discovery list (replaces the old Fly registry-tags picker).
/// </summary>
public record TemplateCandidateBoxDto(
    string Id,
    string? Name,
    string Status,
    string? Size,
    string? Region,
    DateTime? CreatedAt,
    bool AlreadyRegistered);
