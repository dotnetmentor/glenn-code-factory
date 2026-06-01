using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Source.Features.Hooks.Models;
using Source.Features.SignalR.Contracts;
using Source.Features.SignalR.Hubs;
using Source.Infrastructure;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.Hooks.Controllers;

/// <summary>
/// Operator-only HTTP surface for editing a runtime's <see cref="RuntimeHookConfig"/>.
/// Backs the admin UI / CLI for the <c>daemon-hooks-runner</c> spec: an operator
/// posts the full hooks document, we validate the top-level shape, persist to
/// jsonb, and push the new bytes to the live daemon over SignalR via
/// <see cref="IRuntimeClient.UpdateConfig"/>.
///
/// <para><b>Why no MediatR.</b> Same pragmatic reasoning as
/// <see cref="Source.Features.FlyManagement.Controllers.FlyAdminController"/> and
/// <see cref="Source.Features.RuntimeBootstrap.Controllers.BootstrapRunsController"/>:
/// a thin upsert over a single jsonb column with a SignalR push at the end is
/// not a business feature with cross-slice events. Wrapping it in a command
/// would add four files without changing behaviour.</para>
///
/// <para><b>Authorisation.</b> <see cref="RoleConstants.SuperAdmin"/>, matching
/// every other admin surface (FlyAdmin, RuntimeImages, BootstrapRuns,
/// SystemSettings). TenantAdmin would be too broad — hooks can run arbitrary
/// shell commands inside the runtime VM.</para>
///
/// <para><b>Schema ownership.</b> The daemon owns the per-hook schema —
/// <c>cmd</c>, <c>feedbackMode</c>, <c>pattern</c>, etc. We only validate the
/// top-level envelope (object with the four required arrays) so that a
/// completely malformed body still 400s here instead of breaking the daemon
/// on receive. Adding per-field validation would force a coordinated deploy
/// every time the daemon's hook shape evolves.</para>
/// </summary>
[ApiController]
[Route("api/admin/runtimes/{runtimeId:guid}/hooks")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("HookConfigAdmin")]
public class HookConfigAdminController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IHubContext<RuntimeHub, IRuntimeClient> _runtimeHub;
    private readonly ILogger<HookConfigAdminController> _logger;

    /// <summary>
    /// Top-level keys the daemon's hook schema currently uses. The list is
    /// closed: an unknown key is a 400 so a typo doesn't silently disappear
    /// into jsonb. When the daemon adds a new lifecycle point we ship the
    /// constant alongside.
    /// </summary>
    private static readonly string[] RequiredHookKeys =
    {
        "beforePrompt",
        "afterPrompt",
        "onFileChange",
        "beforeCommit",
    };

    public HookConfigAdminController(
        ApplicationDbContext db,
        IHubContext<RuntimeHub, IRuntimeClient> runtimeHub,
        ILogger<HookConfigAdminController> logger)
    {
        _db = db;
        _runtimeHub = runtimeHub;
        _logger = logger;
    }

    /// <summary>
    /// Replace (upsert) the runtime's hook config and push it to the live
    /// daemon. Returns 404 when the runtime is unknown / soft-deleted, 400 on
    /// a malformed envelope, otherwise 200 with the persisted row.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(typeof(UpdateHookConfigResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<UpdateHookConfigResponse>> Update(
        Guid runtimeId,
        [FromBody] UpdateHookConfigRequest request,
        CancellationToken ct)
    {
        // Existence check — soft-deleted rows are filtered by the global
        // ProjectRuntime query filter, so a janitor-marked runtime falls
        // through to 404 just like a hard-missing one. Same kill-switch
        // story as RuntimeHub.OnConnectedAsync.
        var runtimeExists = await _db.ProjectRuntimes
            .AsNoTracking()
            .AnyAsync(r => r.Id == runtimeId, ct);
        if (!runtimeExists)
        {
            return NotFound();
        }

        // Top-level shape validation. Daemon owns the per-hook schema; we
        // only guarantee the envelope is sane so a typo in the operator's
        // payload doesn't survive jsonb persistence and break the daemon on
        // receive.
        var validation = ValidateTopLevelShape(request.Hooks);
        if (validation is not null)
        {
            return BadRequest(new { error = validation });
        }

        // Persist the JSON exactly as serialized — we round-trip through the
        // .NET serializer to normalize whitespace and key ordering before it
        // hits jsonb. The daemon parses tolerantly, so the exact byte form
        // doesn't matter, but a stable normalized form keeps the audit trail
        // consistent.
        var json = JsonSerializer.Serialize(request.Hooks);

        var config = await _db.RuntimeHookConfigs
            .FirstOrDefaultAsync(c => c.RuntimeId == runtimeId, ct);
        if (config is null)
        {
            config = new RuntimeHookConfig
            {
                RuntimeId = runtimeId,
                Json = json,
            };
            _db.RuntimeHookConfigs.Add(config);
        }
        else
        {
            config.Json = json;
        }

        await _db.SaveChangesAsync(ct);

        // Push the new bytes to the live daemon. Best-effort: a daemon that's
        // offline at write time will pick up the same JSON via the bootstrap
        // delivery in RuntimeHub.OnConnectedAsync on its next reconnect, so
        // a failed fan-out doesn't compromise eventual consistency.
        try
        {
            await _runtimeHub.Clients
                .Group($"runtime-{runtimeId}")
                .UpdateConfig(new ConfigUpdatePayload(
                    RuntimeId: runtimeId,
                    Version: "1",
                    RuntimeToken: null,
                    HooksJson: json));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "HookConfigAdminController: UpdateConfig push failed for runtime {RuntimeId}; daemon will pick up hooks on reconnect via the bootstrap delivery.",
                runtimeId);
        }

        _logger.LogInformation(
            "HookConfigAdminController: persisted hook config for runtime {RuntimeId} (configId={ConfigId}, jsonLength={JsonLength}).",
            runtimeId, config.Id, json.Length);

        return Ok(new UpdateHookConfigResponse(config.Id, config.RuntimeId, config.Json));
    }

    /// <summary>
    /// Validate the top-level envelope: must be a JSON object that contains
    /// exactly the four required keys, each pointing at a JSON array. Returns
    /// the human-readable error string on failure, <c>null</c> on success.
    /// </summary>
    private static string? ValidateTopLevelShape(JsonElement hooks)
    {
        if (hooks.ValueKind != JsonValueKind.Object)
        {
            return "hooks must be an object";
        }

        foreach (var key in RequiredHookKeys)
        {
            if (!hooks.TryGetProperty(key, out var value))
            {
                return $"missing required key: {key}";
            }
            if (value.ValueKind != JsonValueKind.Array)
            {
                return $"{key} must be an array";
            }
        }

        // Reject unknown top-level keys. The set is closed for this card —
        // a typo (e.g. "beforPrompt") landing in jsonb would silently
        // disappear, so we surface it as 400 instead.
        foreach (var prop in hooks.EnumerateObject())
        {
            if (Array.IndexOf(RequiredHookKeys, prop.Name) < 0)
            {
                return $"unknown top-level key: {prop.Name}";
            }
        }

        return null;
    }
}

/// <summary>
/// Admin write payload. <see cref="Hooks"/> is a raw <see cref="JsonElement"/>
/// so the daemon's open shape can land here without a coordinated deploy when
/// it grows new fields — we only validate the top-level envelope.
/// </summary>
public record UpdateHookConfigRequest(JsonElement Hooks);

/// <summary>
/// 200 response from the admin write. <see cref="Json"/> is the persisted
/// jsonb body (post-normalization) so callers can echo it back without a
/// follow-up read.
/// </summary>
public record UpdateHookConfigResponse(Guid Id, Guid RuntimeId, string Json);
