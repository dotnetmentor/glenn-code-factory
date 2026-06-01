using System.Text.Json.Serialization;

namespace Source.Features.GitHub.Services.Dtos;

/// <summary>
/// Subset of the <c>installation</c> webhook payload we actually consume.
/// GitHub sends many more fields; we only model what the handler reads.
/// </summary>
public record GithubInstallationWebhookPayload(
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("installation")] GithubInstallationWebhookInstallation? Installation,
    [property: JsonPropertyName("repositories")] List<GithubWebhookRepoDto>? Repositories);

/// <summary>The <c>installation</c> object embedded in <c>installation</c> + <c>installation_repositories</c> events.</summary>
public record GithubInstallationWebhookInstallation(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("account")] GithubAccountDto? Account,
    /// <summary>Presence (non-null) means the install is currently suspended on GitHub's side.</summary>
    [property: JsonPropertyName("suspended_at")] DateTime? SuspendedAt);

/// <summary>
/// Subset of the <c>installation_repositories</c> webhook payload. The webhook delivers
/// compact repo objects (id, name, full_name, private) — not the full repo schema returned
/// by the REST API. Modeled separately from <see cref="GithubRepoDto"/> so we don't pretend
/// to have fields we never receive.
/// </summary>
public record GithubInstallationRepositoriesWebhookPayload(
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("installation")] GithubInstallationWebhookInstallation? Installation,
    [property: JsonPropertyName("repositories_added")] List<GithubWebhookRepoDto>? RepositoriesAdded,
    [property: JsonPropertyName("repositories_removed")] List<GithubWebhookRepoDto>? RepositoriesRemoved);

/// <summary>Compact repo entry as it appears inside <c>installation_repositories.repositories_added/removed</c>.</summary>
public record GithubWebhookRepoDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("full_name")] string FullName,
    [property: JsonPropertyName("private")] bool Private);
