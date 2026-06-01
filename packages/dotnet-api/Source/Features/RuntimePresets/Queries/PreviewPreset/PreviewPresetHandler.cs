using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimePresets.Dtos;
using Source.Features.RuntimePresets.Models;
using Source.Features.RuntimePresets.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Queries.PreviewPreset;

/// <summary>
/// Handler for <see cref="PreviewPresetQuery"/>. Loads the preset, builds a
/// value bag (defaults overlaid by caller-supplied values, plus the
/// system-injected <c>repoDir</c>), renders every template independently, and
/// returns whatever rendered alongside a flat list of per-field errors so the
/// UI can highlight individual broken fields without collapsing the whole
/// preview.
/// </summary>
public sealed class PreviewPresetHandler
    : IQueryHandler<PreviewPresetQuery, Result<PreviewPresetResponse>>
{
    public const string NotFoundError = "preset_not_found";

    /// <summary>
    /// Canonical repo root inside the runtime container — system-injected into
    /// the value bag as <c>{{repoDir}}</c>. Mirrors the constant in
    /// <see cref="PresetExpander"/> so the preview shows the exact path the
    /// daemon will see at boot.
    /// </summary>
    private const string RepoDir = "/data/project/repo";

    private readonly ApplicationDbContext _db;

    public PreviewPresetHandler(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<PreviewPresetResponse>> Handle(
        PreviewPresetQuery request,
        CancellationToken cancellationToken)
    {
        var entity = await _db.ServicePresets
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.PresetId, cancellationToken);

        if (entity is null)
        {
            return Result.Failure<PreviewPresetResponse>(NotFoundError);
        }

        var parameters = PresetParameter.DeserializeList(entity.Parameters);

        // Build the value bag exactly the way the runtime expander will at
        // boot — system-injected first, then per-parameter defaults, then
        // caller-supplied overrides. Order is deliberate; see PresetExpander
        // for the rationale.
        var bag = new Dictionary<string, string>
        {
            ["repoDir"] = RepoDir,
        };
        foreach (var p in parameters)
        {
            if (p.DefaultValue is not null)
            {
                bag[p.Key] = p.DefaultValue;
            }
        }
        if (request.Values is not null)
        {
            foreach (var (k, v) in request.Values)
            {
                bag[k] = JsonElementToString(v);
            }
        }

        var errors = new List<string>();

        // Render every template independently; failures land in errors[] rather
        // than short-circuiting the whole preview. The expander's contract is
        // "fail-closed at boot"; this is the editor counterpart, "fail-open for
        // feedback".
        var renderedCommand = RenderOrCollect(entity.CommandTemplate, bag, "commandTemplate", errors);

        var envOut = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(entity.EnvTemplate))
        {
            Dictionary<string, string>? rawEnv = null;
            try
            {
                rawEnv = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.EnvTemplate);
            }
            catch (JsonException)
            {
                errors.Add("preset_env_template_malformed");
            }

            if (rawEnv is not null)
            {
                foreach (var (k, v) in rawEnv)
                {
                    envOut[k] = RenderOrCollect(v, bag, $"envTemplate.{k}", errors);
                }
            }
        }

        var healthcheck = string.IsNullOrWhiteSpace(entity.HealthcheckCommand)
            ? null
            : RenderOrCollect(entity.HealthcheckCommand!, bag, "healthcheckCommand", errors);

        var installContrib = string.IsNullOrWhiteSpace(entity.InstallContribution)
            ? null
            : RenderOrCollect(entity.InstallContribution!, bag, "installContribution", errors);

        var setupContrib = string.IsNullOrWhiteSpace(entity.SetupContribution)
            ? null
            : RenderOrCollect(entity.SetupContribution!, bag, "setupContribution", errors);

        var installVerify = string.IsNullOrWhiteSpace(entity.InstallVerify)
            ? null
            : RenderOrCollect(entity.InstallVerify!, bag, "installVerify", errors);

        return Result.Success(new PreviewPresetResponse(
            Command: renderedCommand,
            Env: envOut,
            HealthcheckCommand: healthcheck,
            InstallContribution: installContrib,
            SetupContribution: setupContrib,
            InstallVerify: installVerify,
            Errors: errors));
    }

    /// <summary>
    /// Run a single template through <see cref="PresetExpander.Render"/> and
    /// translate failure into an entry in <paramref name="errors"/> tagged with
    /// the source field. Returns the rendered string on success, or the
    /// original template (unsubstituted) on failure — preserving the original
    /// in the response gives the operator something to look at next to the
    /// error message.
    /// </summary>
    private static string RenderOrCollect(
        string template,
        Dictionary<string, string> bag,
        string field,
        List<string> errors)
    {
        var result = PresetExpander.Render(template, bag);
        if (result.IsFailure)
        {
            errors.Add($"{field}:{result.Error}");
            return template;
        }
        return result.Value;
    }

    /// <summary>
    /// Stringify a JsonElement the same way <see cref="PresetExpander"/> does
    /// at expansion time, so the preview matches what the daemon will see.
    /// </summary>
    private static string JsonElementToString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? string.Empty,
        JsonValueKind.Number => v.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => v.GetRawText(),
    };
}
