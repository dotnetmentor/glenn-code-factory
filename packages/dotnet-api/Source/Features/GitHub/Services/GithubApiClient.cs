using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Source.Features.GitHub.Configuration;
using Source.Features.GitHub.Services.Dtos;

namespace Source.Features.GitHub.Services;

/// <summary>
/// Default <see cref="IGithubApiClient"/> over <see cref="HttpClient"/> from
/// <see cref="IHttpClientFactory"/> (named client <c>"github"</c>).
/// </summary>
public class GithubApiClient : IGithubApiClient
{
    public const string HttpClientName = "github";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGithubAppTokenService _tokenService;
    private readonly IGithubOptionsAccessor _options;
    private readonly ILogger<GithubApiClient> _logger;

    public GithubApiClient(
        IHttpClientFactory httpClientFactory,
        IGithubAppTokenService tokenService,
        IGithubOptionsAccessor options,
        ILogger<GithubApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _tokenService = tokenService;
        _options = options;
        _logger = logger;
    }

    public async Task<GithubInstallationDto> GetInstallationAsync(long installationId, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"app/installations/{installationId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenService.CreateAppJwt());
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<GithubInstallationDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty installation payload from GitHub.");
    }

    public async Task<IReadOnlyList<GithubRepoDto>> ListInstallationRepositoriesAsync(long installationId, CancellationToken ct = default)
    {
        var token = await _tokenService.GetInstallationTokenAsync(installationId, ct);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var collected = new List<GithubRepoDto>();
        var page = 1;
        const int perPage = 100;

        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"installation/repositories?per_page={perPage}&page={page}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadFromJsonAsync<GithubInstallationRepositoriesResponse>(cancellationToken: ct);
            if (body?.Repositories is null || body.Repositories.Count == 0) break;

            collected.AddRange(body.Repositories);
            if (body.Repositories.Count < perPage) break;
            page++;
        }

        return collected;
    }

    public async Task<GithubUserDto> GetCurrentUserAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("accessToken is required", nameof(accessToken));

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, "user");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<GithubUserDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty /user payload from GitHub.");
    }

    public async Task<IReadOnlyList<GithubEmailDto>> GetCurrentUserEmailsAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("accessToken is required", nameof(accessToken));

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Get, "user/emails");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var emails = await resp.Content.ReadFromJsonAsync<List<GithubEmailDto>>(cancellationToken: ct);
        return emails ?? new List<GithubEmailDto>();
    }

    public async Task<string> ExchangeOAuthCodeAsync(string code, CancellationToken ct = default)
    {
        var full = await ExchangeOAuthCodeFullAsync(code, ct);
        return full.AccessToken;
    }

    public async Task<GithubUserAccessTokenPayload> ExchangeOAuthCodeFullAsync(string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("code is required", nameof(code));

        // Token exchange uses github.com (not api.github.com), so use a fresh client
        // with an explicit absolute URL rather than the named client's base address.
        var options = _options.Current;
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["code"] = code,
            }),
        };
        // Force JSON response — without this, GitHub returns a form-encoded body.
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("OAuth code exchange failed: {Status} {Body}", (int)resp.StatusCode, body);
            throw new HttpRequestException($"OAuth code exchange failed: {(int)resp.StatusCode}");
        }

        var payload = await resp.Content.ReadFromJsonAsync<GithubOAuthAccessTokenResponse>(cancellationToken: ct);
        if (payload is null || !string.IsNullOrEmpty(payload.Error) || string.IsNullOrEmpty(payload.AccessToken))
        {
            _logger.LogWarning("OAuth code exchange returned error or empty token: {Error} {Description}", payload?.Error, payload?.ErrorDescription);
            throw new InvalidOperationException(payload?.ErrorDescription ?? "OAuth code exchange returned no access token.");
        }

        // expires_in / refresh_token_expires_in are seconds; resolve to absolute UTC
        // here so persistence + downstream comparisons don't have to track "captured at".
        var now = DateTime.UtcNow;
        return new GithubUserAccessTokenPayload
        {
            AccessToken = payload.AccessToken,
            AccessTokenExpiresAt = payload.ExpiresIn is { } secs ? now.AddSeconds(secs) : null,
            RefreshToken = payload.RefreshToken,
            RefreshTokenExpiresAt = payload.RefreshTokenExpiresIn is { } rsecs ? now.AddSeconds(rsecs) : null,
        };
    }

    public async Task<GithubUserAccessTokenPayload> RefreshUserAccessTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("refreshToken is required", nameof(refreshToken));

        var options = _options.Current;
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("UAT refresh failed: {Status} {Body}", (int)resp.StatusCode, body);
            throw new HttpRequestException($"UAT refresh failed: {(int)resp.StatusCode}");
        }

        var payload = await resp.Content.ReadFromJsonAsync<GithubOAuthAccessTokenResponse>(cancellationToken: ct);
        if (payload is null || !string.IsNullOrEmpty(payload.Error) || string.IsNullOrEmpty(payload.AccessToken))
        {
            _logger.LogWarning("UAT refresh returned error or empty token: {Error} {Description}", payload?.Error, payload?.ErrorDescription);
            throw new InvalidOperationException(payload?.ErrorDescription ?? "UAT refresh returned no access token.");
        }

        var now = DateTime.UtcNow;
        return new GithubUserAccessTokenPayload
        {
            AccessToken = payload.AccessToken,
            AccessTokenExpiresAt = payload.ExpiresIn is { } secs ? now.AddSeconds(secs) : null,
            RefreshToken = payload.RefreshToken,
            RefreshTokenExpiresAt = payload.RefreshTokenExpiresIn is { } rsecs ? now.AddSeconds(rsecs) : null,
        };
    }

    public async Task AddRepoToUserInstallationAsync(string userAccessToken, long installationId, long repositoryId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userAccessToken)) throw new ArgumentException("userAccessToken is required", nameof(userAccessToken));
        if (installationId <= 0) throw new ArgumentException("installationId must be positive", nameof(installationId));
        if (repositoryId <= 0) throw new ArgumentException("repositoryId must be positive", nameof(repositoryId));

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(
            HttpMethod.Put,
            $"user/installations/{installationId}/repositories/{repositoryId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userAccessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "AddRepoToUserInstallation failed: status={Status} installation={InstallationId} repo={RepositoryId} body={Body}",
                (int)resp.StatusCode, installationId, repositoryId, body);
            resp.EnsureSuccessStatusCode();
        }
    }

    public async Task<GithubRepoDto> CreateUserRepoWithTokenAsync(
        string accessToken,
        string name,
        string? description,
        bool isPrivate,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("accessToken is required", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));

        var client = _httpClientFactory.CreateClient(HttpClientName);

        var body = new
        {
            name,
            description = description ?? string.Empty,
            @private = isPrivate,
            auto_init = true,
        };
        var payload = JsonSerializer.Serialize(body);

        using var req = new HttpRequestMessage(HttpMethod.Post, "user/repos")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            string? detail = null;
            try
            {
                var bodyText = await resp.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    using var doc = JsonDocument.Parse(bodyText);
                    if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    {
                        detail = msg.GetString();
                    }
                    if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                    {
                        var msgs = new List<string>();
                        foreach (var e in errs.EnumerateArray())
                        {
                            if (e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                                msgs.Add(em.GetString() ?? string.Empty);
                            else if (e.TryGetProperty("code", out var ec) && ec.ValueKind == JsonValueKind.String)
                                msgs.Add(ec.GetString() ?? string.Empty);
                        }
                        if (msgs.Count > 0)
                        {
                            detail = (detail is null ? string.Empty : detail + " — ") + string.Join("; ", msgs);
                        }
                    }
                }
            }
            catch (JsonException) { /* non-JSON body */ }

            _logger.LogWarning(
                "GitHub /user/repos (UAT) failed: status={Status} name={Name} detail={Detail}",
                (int)resp.StatusCode, name, detail);
            throw new GitHubRepoCreateFailedException((int)resp.StatusCode, "(user)", name, detail);
        }

        var dto = await resp.Content.ReadFromJsonAsync<GithubRepoDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty /user/repos (UAT) payload from GitHub.");
        _logger.LogInformation(
            "Created GitHub repo {FullName} (id={Id}) under user account via UAT",
            dto.FullName, dto.Id);
        return dto;
    }

    public async Task<GithubRepoDto> CreateRepoFromTemplateWithTokenAsync(
        string accessToken,
        string templateOwner,
        string templateRepo,
        string newOwner,
        string newRepoName,
        string? description,
        bool isPrivate,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new ArgumentException("accessToken is required", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(templateOwner)) throw new ArgumentException("templateOwner is required", nameof(templateOwner));
        if (string.IsNullOrWhiteSpace(templateRepo)) throw new ArgumentException("templateRepo is required", nameof(templateRepo));
        if (string.IsNullOrWhiteSpace(newOwner)) throw new ArgumentException("newOwner is required", nameof(newOwner));
        if (string.IsNullOrWhiteSpace(newRepoName)) throw new ArgumentException("newRepoName is required", nameof(newRepoName));

        var client = _httpClientFactory.CreateClient(HttpClientName);

        var body = new
        {
            owner = newOwner,
            name = newRepoName,
            description = description ?? string.Empty,
            @private = isPrivate,
            include_all_branches = false,
        };
        var payload = JsonSerializer.Serialize(body);

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"repos/{Uri.EscapeDataString(templateOwner)}/{Uri.EscapeDataString(templateRepo)}/generate")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            // Mirror the IAT-path error parsing in CreateRepoFromTemplateAsync so callers see
            // a consistent error shape regardless of which token was used.
            string? detail = null;
            try
            {
                var bodyText = await resp.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    using var doc = JsonDocument.Parse(bodyText);
                    if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    {
                        detail = msg.GetString();
                    }
                    if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                    {
                        var msgs = new List<string>();
                        foreach (var e in errs.EnumerateArray())
                        {
                            if (e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                                msgs.Add(em.GetString() ?? string.Empty);
                            else if (e.TryGetProperty("code", out var ec) && ec.ValueKind == JsonValueKind.String)
                                msgs.Add(ec.GetString() ?? string.Empty);
                        }
                        if (msgs.Count > 0)
                        {
                            detail = (detail is null ? string.Empty : detail + " — ") + string.Join("; ", msgs);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON body — fall through with no detail.
            }

            _logger.LogWarning(
                "GitHub generate-from-template (UAT) failed: status={Status} template={TemplateOwner}/{TemplateRepo} newOwner={NewOwner} newName={NewName} detail={Detail}",
                (int)resp.StatusCode, templateOwner, templateRepo, newOwner, newRepoName, detail);
            throw new GitHubRepoCreateFailedException((int)resp.StatusCode, newOwner, newRepoName, detail);
        }

        var dto = await resp.Content.ReadFromJsonAsync<GithubRepoDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty generate-from-template (UAT) payload from GitHub.");
        _logger.LogInformation(
            "Generated GitHub repo {FullName} (id={Id}) from template {TemplateOwner}/{TemplateRepo} via UAT for {NewOwner}",
            dto.FullName, dto.Id, templateOwner, templateRepo, newOwner);
        return dto;
    }

    public async Task<GithubRepoDto> GetRepositoryAsync(long installationId, string owner, string repo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner is required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("repo is required", nameof(repo));

        var token = await _tokenService.GetInstallationTokenAsync(installationId, ct);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // GitHub allows mixed case in owner/repo but the canonical form is lowercase. We pass the
        // values through unchanged — GitHub does its own case-insensitive lookup.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<GithubRepoDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty repository payload from GitHub.");
    }

    public async Task<IReadOnlyList<GithubBranchDto>> ListRepositoryBranchesAsync(long installationId, string owner, string repo, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner is required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("repo is required", nameof(repo));

        var token = await _tokenService.GetInstallationTokenAsync(installationId, ct);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var collected = new List<GithubBranchDto>();
        var page = 1;
        const int perPage = 100;

        while (true)
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/branches?per_page={perPage}&page={page}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var page1 = await resp.Content.ReadFromJsonAsync<List<GithubBranchDto>>(cancellationToken: ct);
            if (page1 is null || page1.Count == 0) break;

            collected.AddRange(page1);
            // Smoke-test scope: cap at the first 100 branches per the card. Saves us from fanning
            // out into very large repos and matches the per_page=100 cap GitHub enforces anyway.
            if (page1.Count < perPage) break;
            break;
        }

        return collected;
    }

    /// <summary>
    /// Installation-authenticated <c>GET /repos/{owner}/{repo}/git/refs/heads/{branch}</c>.
    /// Returns the tip commit SHA. Translates GitHub's 404 into a typed
    /// <see cref="SourceBranchNotFoundException"/> so the Copy Branch orchestrator can
    /// surface a "push the source first" message without re-parsing the response.
    /// </summary>
    public async Task<string> GetBranchTipShaAsync(string owner, string repo, string branch, long installationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner is required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("repo is required", nameof(repo));
        if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("branch is required", nameof(branch));

        var token = await _tokenService.GetInstallationTokenAsync(installationId, ct);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // Branch names can contain slashes (e.g. "feature/foo"); escape per-segment so the
        // GitHub path parser sees the full ref. We deliberately do NOT escape the leading
        // "heads/" — that's a literal segment in the API path.
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/refs/heads/{Uri.EscapeDataString(branch)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // Source branch never pushed (or deleted from remote). Surface as a typed
            // exception so the orchestrator can decide on the user-facing message.
            throw new SourceBranchNotFoundException(owner, repo, branch);
        }

        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<GithubGitRefDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty git/refs payload from GitHub.");
        return dto.Object.Sha;
    }

    /// <summary>
    /// Installation-authenticated <c>POST /repos/{owner}/{repo}/git/refs</c> with body
    /// <c>{ ref: "refs/heads/{newName}", sha }</c>. Maps 422 → <see cref="BranchAlreadyExistsException"/>
    /// and 403 → <see cref="GitHubBranchCreationForbiddenException"/> so callers don't have
    /// to inspect HTTP status codes.
    /// </summary>
    public async Task CreateBranchRefAsync(string owner, string repo, string newName, string sha, long installationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner is required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("repo is required", nameof(repo));
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("newName is required", nameof(newName));
        if (string.IsNullOrWhiteSpace(sha)) throw new ArgumentException("sha is required", nameof(sha));

        var token = await _tokenService.GetInstallationTokenAsync(installationId, ct);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // GitHub expects the full ref form ("refs/heads/{name}") in the create body — they
        // reject bare "{name}" with a 422. The name itself is sent verbatim (slashes allowed)
        // so feature/foo-style branches work.
        var payload = JsonSerializer.Serialize(new { @ref = $"refs/heads/{newName}", sha });
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/refs")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);

        if (resp.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // GitHub returns 422 specifically for "Reference already exists". We don't
            // re-parse the body to confirm — a 422 on this endpoint is collision in the
            // overwhelming majority of cases, and the rare malformed-payload 422 would be
            // a server-side bug on our end (sha shape, ref shape) better surfaced loudly.
            throw new BranchAlreadyExistsException(owner, repo, newName);
        }

        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            // Pull GitHub's message out of the body when possible — branch protection
            // returns useful detail ("required status checks", etc.) that we want to keep.
            string? detail = null;
            try
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    {
                        detail = msg.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON body (HTML edge page during an incident, etc.) — fall through with no detail.
            }
            throw new GitHubBranchCreationForbiddenException(owner, repo, newName, detail);
        }

        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Installation-authenticated <c>DELETE /repos/{owner}/{repo}/git/refs/heads/{name}</c>.
    /// Swallows 404 (idempotent: the ref is already gone, which is what the rollback path wants).
    /// All other non-success statuses bubble as <see cref="HttpRequestException"/> via
    /// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>.
    /// </summary>
    public async Task DeleteBranchRefAsync(string owner, string repo, string name, long installationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner is required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("repo is required", nameof(repo));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));

        var token = await _tokenService.GetInstallationTokenAsync(installationId, ct);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var req = new HttpRequestMessage(
            HttpMethod.Delete,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/refs/heads/{Uri.EscapeDataString(name)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);

        // Rollback path: a missing ref is the desired end-state, not an error. Anything else
        // (auth, 5xx, branch protection) bubbles so the orchestrator can log and surface it.
        if (resp.StatusCode == HttpStatusCode.NotFound) return;

        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Installation-authenticated <c>GET /repos/{owner}/{repo}/commits/{sha}</c>. Used by
    /// the project-scoped branch picker to enrich each branch row with the tip commit's
    /// author / date / message. Soft-fails by returning <c>null</c> on any non-success
    /// status — the branch list call must not 500 the whole page over one bad commit.
    /// </summary>
    public async Task<GithubCommitDto?> GetCommitAsync(long installationId, string owner, string repo, string sha, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner is required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("repo is required", nameof(repo));
        if (string.IsNullOrWhiteSpace(sha)) throw new ArgumentException("sha is required", nameof(sha));

        var token = await _tokenService.GetInstallationTokenAsync(installationId, ct);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/commits/{Uri.EscapeDataString(sha)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);
        // Per-branch enrichment is best-effort: 404 (commit gone), 403 (unusual rate-limit
        // shape), 5xx (transient) — any of these collapse to "no commit metadata for this
        // branch" rather than failing the whole list.
        if (!resp.IsSuccessStatusCode) return null;

        return await resp.Content.ReadFromJsonAsync<GithubCommitDto>(cancellationToken: ct);
    }

    /// <summary>
    /// Creates a brand-new repo under the installation's account. Dispatches on
    /// <paramref name="accountType"/> — "Organization" hits the org repos endpoint, "User"
    /// hits the user repos endpoint. <c>auto_init: true</c> seeds an initial commit so the
    /// returned repo already has the default branch pushed (otherwise daemons can't clone).
    /// </summary>
    public async Task<GithubRepoDto> CreateInstallationRepositoryAsync(
        long installationId,
        string ownerLogin,
        string accountType,
        string name,
        string? description,
        bool isPrivate,
        string defaultBranch,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerLogin)) throw new ArgumentException("ownerLogin is required", nameof(ownerLogin));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required", nameof(name));
        if (string.IsNullOrWhiteSpace(defaultBranch)) throw new ArgumentException("defaultBranch is required", nameof(defaultBranch));

        var token = await _tokenService.GetInstallationTokenAsync(installationId, ct);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        // GitHub differentiates org vs user repo creation by URL path. Both accept the same
        // body shape — only the auth context differs.
        var isOrg = string.Equals(accountType, "Organization", StringComparison.OrdinalIgnoreCase);
        var path = isOrg
            ? $"orgs/{Uri.EscapeDataString(ownerLogin)}/repos"
            : "user/repos";

        var body = new
        {
            name,
            description = description ?? string.Empty,
            @private = isPrivate,
            auto_init = true,
            // Keep things simple — no .gitignore / license templates. The agent will fill
            // the repo from scratch. Default branch defaults to the App's setting if we
            // don't override, but for predictability we pin it here.
            // NOTE: GitHub's create-repo endpoint doesn't accept a default_branch param on
            // creation — the default branch is the first ref auto_init creates, which is
            // controlled by the *account's* repo defaults (typically "main"). We rely on
            // the caller passing "main" and the response carrying the actual chosen branch.
        };
        var payload = JsonSerializer.Serialize(body);

        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            // Extract GitHub's "message" field for the actionable detail. GitHub sometimes
            // returns an "errors" array for validation problems — flatten it into one line.
            string? detail = null;
            try
            {
                var bodyText = await resp.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    using var doc = JsonDocument.Parse(bodyText);
                    if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    {
                        detail = msg.GetString();
                    }
                    if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                    {
                        var msgs = new List<string>();
                        foreach (var e in errs.EnumerateArray())
                        {
                            if (e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                                msgs.Add(em.GetString() ?? string.Empty);
                            else if (e.TryGetProperty("code", out var ec) && ec.ValueKind == JsonValueKind.String)
                                msgs.Add(ec.GetString() ?? string.Empty);
                        }
                        if (msgs.Count > 0)
                        {
                            detail = (detail is null ? string.Empty : detail + " — ") + string.Join("; ", msgs);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON body — fall through with no detail.
            }

            _logger.LogWarning(
                "GitHub repo create failed: status={Status} owner={Owner} name={Name} detail={Detail}",
                (int)resp.StatusCode, ownerLogin, name, detail);
            throw new GitHubRepoCreateFailedException((int)resp.StatusCode, ownerLogin, name, detail);
        }

        var dto = await resp.Content.ReadFromJsonAsync<GithubRepoDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty create-repo payload from GitHub.");
        _logger.LogInformation(
            "Created GitHub repo {FullName} (id={Id}) via installation {InstallationId}",
            dto.FullName, dto.Id, installationId);
        return dto;
    }

    /// <summary>
    /// Creates a new repo from a template via GitHub's
    /// <c>POST /repos/{templateOwner}/{templateRepo}/generate</c> endpoint. The new repo
    /// inherits the template's content as a single squashed commit on the default branch.
    /// We pin <c>include_all_branches=false</c> — V1 only ships the default branch from the
    /// template; cloning every branch would surface stale work-in-progress refs to the user.
    /// Mirrors <see cref="CreateInstallationRepositoryAsync"/>'s error handling: any non-success
    /// status (including 404 for missing / private templates) is wrapped in
    /// <see cref="GitHubRepoCreateFailedException"/> with GitHub's <c>message</c> field
    /// preserved verbatim.
    /// </summary>
    public async Task<GithubRepoDto> CreateRepoFromTemplateAsync(
        long installationId,
        string templateOwner,
        string templateRepo,
        string newOwner,
        string newRepoName,
        string? description,
        bool isPrivate,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(templateOwner)) throw new ArgumentException("templateOwner is required", nameof(templateOwner));
        if (string.IsNullOrWhiteSpace(templateRepo)) throw new ArgumentException("templateRepo is required", nameof(templateRepo));
        if (string.IsNullOrWhiteSpace(newOwner)) throw new ArgumentException("newOwner is required", nameof(newOwner));
        if (string.IsNullOrWhiteSpace(newRepoName)) throw new ArgumentException("newRepoName is required", nameof(newRepoName));

        var token = await _tokenService.GetInstallationTokenAsync(installationId, ct);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var body = new
        {
            owner = newOwner,
            name = newRepoName,
            description = description ?? string.Empty,
            @private = isPrivate,
            // V1 scope: only ship the template's default branch. The "generate" API will
            // otherwise mirror every branch on the source, which is noise we don't want.
            include_all_branches = false,
        };
        var payload = JsonSerializer.Serialize(body);

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"repos/{Uri.EscapeDataString(templateOwner)}/{Uri.EscapeDataString(templateRepo)}/generate")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            // Same body-parsing shape as CreateInstallationRepositoryAsync — keep them in
            // lockstep so the user-facing message style is consistent regardless of which
            // create path the handler picked.
            string? detail = null;
            try
            {
                var bodyText = await resp.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    using var doc = JsonDocument.Parse(bodyText);
                    if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                    {
                        detail = msg.GetString();
                    }
                    if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                    {
                        var msgs = new List<string>();
                        foreach (var e in errs.EnumerateArray())
                        {
                            if (e.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                                msgs.Add(em.GetString() ?? string.Empty);
                            else if (e.TryGetProperty("code", out var ec) && ec.ValueKind == JsonValueKind.String)
                                msgs.Add(ec.GetString() ?? string.Empty);
                        }
                        if (msgs.Count > 0)
                        {
                            detail = (detail is null ? string.Empty : detail + " — ") + string.Join("; ", msgs);
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON body — fall through with no detail.
            }

            _logger.LogWarning(
                "GitHub generate-from-template failed: status={Status} template={TemplateOwner}/{TemplateRepo} newOwner={NewOwner} newName={NewName} detail={Detail}",
                (int)resp.StatusCode, templateOwner, templateRepo, newOwner, newRepoName, detail);
            throw new GitHubRepoCreateFailedException((int)resp.StatusCode, newOwner, newRepoName, detail);
        }

        var dto = await resp.Content.ReadFromJsonAsync<GithubRepoDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty generate-from-template payload from GitHub.");
        _logger.LogInformation(
            "Generated GitHub repo {FullName} (id={Id}) from template {TemplateOwner}/{TemplateRepo} via installation {InstallationId}",
            dto.FullName, dto.Id, templateOwner, templateRepo, installationId);
        return dto;
    }

    /// <summary>
    /// Installation-authenticated <c>PUT /repos/{owner}/{repo}/contents/{path}</c>. Creates
    /// a file on the given branch. GitHub auto-encodes the response payload but the request
    /// content field MUST be base64-encoded — we do that here so callers pass plain text.
    /// </summary>
    public async Task CreateFileAsync(
        long installationId,
        string owner,
        string repo,
        string path,
        string content,
        string commitMessage,
        string branch,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner is required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("repo is required", nameof(repo));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required", nameof(path));
        if (content is null) throw new ArgumentException("content is required", nameof(content));
        if (string.IsNullOrWhiteSpace(commitMessage)) throw new ArgumentException("commitMessage is required", nameof(commitMessage));
        if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("branch is required", nameof(branch));

        var token = await _tokenService.GetInstallationTokenAsync(installationId, ct);
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var body = new
        {
            message = commitMessage,
            content = encoded,
            branch,
        };
        var payload = JsonSerializer.Serialize(body);

        // Path segments must be URL-encoded individually so a path like "docs/foo.md"
        // doesn't lose its slash.
        var encodedPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        using var req = new HttpRequestMessage(
            HttpMethod.Put,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/contents/{encodedPath}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await client.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var bodyText = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "GitHub create-file failed: status={Status} repo={Owner}/{Repo} path={Path} body={Body}",
                (int)resp.StatusCode, owner, repo, path, bodyText);
            resp.EnsureSuccessStatusCode();
        }
    }
}
