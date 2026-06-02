using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.RuntimePresets.Models;
using Source.Infrastructure;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Services;

/// <summary>
/// Expands a <see cref="RuntimeSpecV3"/> (preset-based input) into a
/// <see cref="RuntimeSpecV2"/> (the wire shape the daemon already consumes).
///
/// <para><b>Why this seam exists.</b> The daemon was built around V2; rolling
/// a new contract through the runtime / supervisord translator would be a
/// week of careful work for negligible gain. Expanding server-side keeps the
/// daemon untouched, lets the agent author against the small, typed V3
/// surface, and gives the super-admin UI a "live preview" pane that shows
/// exactly what the daemon will see.</para>
/// </summary>
public interface IPresetExpander
{
    /// <summary>
    /// Validate the spec, resolve every preset slug from the DB, type-check
    /// every parameter, render every handlebars template, and return the
    /// V2-shaped result. Failures short-circuit with a stable
    /// <c>snake_case</c> error code so callers can pattern-match — see the
    /// individual error sites in <see cref="PresetExpander.ExpandAsync"/> for
    /// the catalog.
    /// </summary>
    Task<Result<RuntimeSpecV2>> ExpandAsync(RuntimeSpecV3 spec, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IPresetExpander"/> implementation backed by
/// <c>ApplicationDbContext.ServicePresets</c>. Scoped lifetime — the
/// DbContext it depends on is scoped.
/// </summary>
public class PresetExpander : IPresetExpander
{
    /// <summary>
    /// Canonical repo root inside the runtime container — system-injected
    /// into every template's value bag as <c>{{repoDir}}</c>. Hardcoded
    /// because every runtime mounts the repo at the same path; if that ever
    /// changes it'll change in the daemon's repo-clone code first and this
    /// constant follows.
    /// </summary>
    private const string RepoDir = "/data/project/repo";

    /// <summary>
    /// Handlebars-lite regex: <c>{{name}}</c> with optional inner whitespace,
    /// <c>name</c> is a C-style identifier. Deliberately not implementing
    /// full Handlebars — no helpers, no nesting, no conditionals. The preset
    /// authoring surface is shell + a few placeholders; anything more elaborate
    /// belongs in the bash itself.
    /// </summary>
    private static readonly Regex HandlebarsRegex =
        new(@"\{\{\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    private readonly ApplicationDbContext _db;
    private readonly ILogger<PresetExpander> _logger;

    public PresetExpander(ApplicationDbContext db, ILogger<PresetExpander> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<RuntimeSpecV2>> ExpandAsync(
        RuntimeSpecV3 spec,
        CancellationToken ct = default)
    {
        var validate = spec.Validate();
        if (validate.IsFailure)
        {
            return Result.Failure<RuntimeSpecV2>(validate.Error!);
        }

        // Load all referenced presets in a single round-trip. AsNoTracking
        // because this is a pure read path — the expander never persists.
        var slugs = spec.Services!.Select(s => s.Kind).Distinct().ToList();
        var presets = await _db.ServicePresets
            .AsNoTracking()
            .Where(p => slugs.Contains(p.Slug))
            .ToDictionaryAsync(p => p.Slug, ct);

        foreach (var slug in slugs)
        {
            if (!presets.ContainsKey(slug))
            {
                return Result.Failure<RuntimeSpecV2>($"preset_not_found:{slug}");
            }
        }

        var expandedServices = new List<ServiceSpec>();
        // Contributions are collected per-service in spec order, then deduped
        // and joined at the top level. Order is preserved (first occurrence
        // wins) so mise-toolchain-install precedes mise-tool-install, etc.
        var installContribs = new List<string>();
        var setupContribs = new List<string>();
        var installVerifies = new List<string>();

        foreach (var svc in spec.Services!)
        {
            var preset = presets[svc.Kind];
            var paramList = PresetParameter.DeserializeList(preset.Parameters);

            // Build the value bag — system-injected, then defaults, then
            // user-supplied (overrides win). Order matters: any future
            // system-injected key would still be overrideable by a default
            // (intentional — operator can pin an alternative), and a default
            // is always overrideable by an explicit value.
            var bag = new Dictionary<string, string>
            {
                ["repoDir"] = RepoDir,
            };

            foreach (var p in paramList)
            {
                if (p.DefaultValue is not null)
                {
                    bag[p.Key] = p.DefaultValue;
                }
            }

            if (svc.Values is not null)
            {
                foreach (var (k, v) in svc.Values)
                {
                    bag[k] = ValueToString(v);
                }
            }

            // Required-presence + per-type validation. Enum is strict (closed
            // set), integer / boolean parse-checked, string is freeform.
            foreach (var p in paramList)
            {
                if (p.Required && !bag.ContainsKey(p.Key))
                {
                    return Result.Failure<RuntimeSpecV2>(
                        $"param_required:{svc.Name}.{p.Key}");
                }

                if (bag.TryGetValue(p.Key, out var raw))
                {
                    if (p.Type == PresetParameterType.Integer && !int.TryParse(raw, out _))
                    {
                        return Result.Failure<RuntimeSpecV2>(
                            $"param_not_integer:{svc.Name}.{p.Key}");
                    }
                    if (p.Type == PresetParameterType.Boolean && !bool.TryParse(raw, out _))
                    {
                        return Result.Failure<RuntimeSpecV2>(
                            $"param_not_boolean:{svc.Name}.{p.Key}");
                    }
                    if (p.Type == PresetParameterType.Enum &&
                        p.EnumOptions is { Count: > 0 } opts &&
                        !opts.Contains(raw))
                    {
                        return Result.Failure<RuntimeSpecV2>(
                            $"param_not_in_enum:{svc.Name}.{p.Key}");
                    }
                }
            }

            // Render the per-service templates against the bag.
            var renderedCommand = Render(preset.CommandTemplate, bag);
            if (renderedCommand.IsFailure)
            {
                return Result.Failure<RuntimeSpecV2>(renderedCommand.Error!);
            }

            Dictionary<string, string>? envDict = null;
            if (!string.IsNullOrWhiteSpace(preset.EnvTemplate))
            {
                Dictionary<string, string>? rawEnv;
                try
                {
                    rawEnv = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        preset.EnvTemplate);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex,
                        "Preset {Slug} has malformed EnvTemplate JSON", preset.Slug);
                    return Result.Failure<RuntimeSpecV2>(
                        $"preset_env_template_malformed:{preset.Slug}");
                }

                if (rawEnv is not null && rawEnv.Count > 0)
                {
                    envDict = new Dictionary<string, string>();
                    foreach (var (k, v) in rawEnv)
                    {
                        var r = Render(v, bag);
                        if (r.IsFailure)
                        {
                            return Result.Failure<RuntimeSpecV2>(r.Error!);
                        }
                        envDict[k] = r.Value;
                    }
                }
            }

            string? healthcheckCmd = null;
            if (!string.IsNullOrWhiteSpace(preset.HealthcheckCommand))
            {
                var hc = Render(preset.HealthcheckCommand, bag);
                if (hc.IsFailure)
                {
                    return Result.Failure<RuntimeSpecV2>(hc.Error!);
                }
                if (!string.IsNullOrWhiteSpace(hc.Value))
                {
                    healthcheckCmd = hc.Value;
                }
            }

            // Merge required-env declarations: preset contribution first, then
            // ad-hoc V3 declarations. Dedupe by Key (case-sensitive); first
            // occurrence wins. Description is rendered against the same bag;
            // the Key itself is never templated. Empty → leave RequiredEnv null
            // (don't emit an empty array).
            var requiredEnv = MergeRequiredEnv(
                ServicePreset.DeserializeRequiredEnv(preset.RequiredEnvContribution),
                svc.RequiredEnv);
            var renderedRequiredEnv = new List<RequiredEnvVar>();
            foreach (var rev in requiredEnv)
            {
                string? renderedDescription = null;
                if (!string.IsNullOrWhiteSpace(rev.Description))
                {
                    var rd = Render(rev.Description, bag);
                    if (rd.IsFailure)
                    {
                        return Result.Failure<RuntimeSpecV2>(rd.Error!);
                    }
                    renderedDescription = rd.Value;
                }
                renderedRequiredEnv.Add(new RequiredEnvVar
                {
                    Key = rev.Key,
                    Description = renderedDescription,
                    Secret = rev.Secret,
                    Required = rev.Required,
                });
            }

            HealthcheckSpec? hcSpec = healthcheckCmd is null
                ? null
                : new HealthcheckSpec
                {
                    Command = healthcheckCmd,
                    IntervalSeconds = preset.HealthcheckInterval,
                };

            expandedServices.Add(new ServiceSpec
            {
                Name = svc.Name,
                Command = renderedCommand.Value,
                User = preset.DefaultUser,
                Autorestart = preset.Autorestart,
                Env = envDict,
                Healthcheck = hcSpec,
                // Contributions live at the top level (deduped across the
                // spec), so per-service Install / InstallVerify are always
                // null in the V2 expansion.
                Install = null,
                InstallVerify = null,
                RequiredEnv = renderedRequiredEnv.Count > 0 ? renderedRequiredEnv : null,
            });

            if (!string.IsNullOrWhiteSpace(preset.InstallContribution))
            {
                var rendered = Render(preset.InstallContribution, bag);
                if (rendered.IsFailure)
                {
                    return Result.Failure<RuntimeSpecV2>(rendered.Error!);
                }
                installContribs.Add(rendered.Value);
            }
            if (!string.IsNullOrWhiteSpace(preset.SetupContribution))
            {
                var rendered = Render(preset.SetupContribution, bag);
                if (rendered.IsFailure)
                {
                    return Result.Failure<RuntimeSpecV2>(rendered.Error!);
                }
                setupContribs.Add(rendered.Value);
            }
            if (!string.IsNullOrWhiteSpace(preset.InstallVerify))
            {
                var rendered = Render(preset.InstallVerify, bag);
                if (rendered.IsFailure)
                {
                    return Result.Failure<RuntimeSpecV2>(rendered.Error!);
                }
                installVerifies.Add(rendered.Value);
            }
        }

        // Dedupe + join top-level contributions. The spec's own top-level
        // freeform fields are appended last so the operator's overrides win
        // over preset-provided bash.
        var install = JoinDeduped(installContribs, spec.Install);
        var setup = JoinDeduped(setupContribs, spec.Setup);
        var installVerify = JoinDeduped(installVerifies, spec.InstallVerify);

        return Result.Success(new RuntimeSpecV2
        {
            Version = 2,
            Install = string.IsNullOrWhiteSpace(install) ? null : install,
            InstallVerify = string.IsNullOrWhiteSpace(installVerify) ? null : installVerify,
            Services = expandedServices,
            Setup = string.IsNullOrWhiteSpace(setup) ? null : setup,
        });
    }

    /// <summary>
    /// Stringify a value from the V3 spec's <c>Values</c> dictionary. We
    /// preserve number formatting (no quotes, no precision loss) and unwrap
    /// JSON string literals so a port supplied as <c>5338</c> renders the
    /// same as <c>"5338"</c> in the final shell command.
    /// </summary>
    private static string ValueToString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        // Arrays / objects: emit raw JSON. Unusual but at least lossless;
        // a preset author who needs structure can JSON-decode at runtime.
        _ => v.GetRawText(),
    };

    /// <summary>
    /// Substitute every <c>{{name}}</c> placeholder in <paramref name="template"/>
    /// against <paramref name="bag"/>. Returns
    /// <c>template_unknown_placeholder:{name}</c> on the FIRST unknown key —
    /// fail-fast rather than rendering a half-substituted command that would
    /// crash supervisord cryptically.
    /// </summary>
    public static Result<string> Render(string template, Dictionary<string, string> bag)
    {
        string? missing = null;
        var output = HandlebarsRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (bag.TryGetValue(key, out var v))
            {
                return v;
            }
            missing ??= key;
            return match.Value;
        });
        if (missing is not null)
        {
            return Result.Failure<string>($"template_unknown_placeholder:{missing}");
        }
        return Result.Success(output);
    }

    /// <summary>
    /// Merge the preset's required-env contribution with any ad-hoc vars the
    /// V3 service entry declared. Preset entries are considered first, then the
    /// ad-hoc list, so on a duplicate <see cref="RequiredEnvVar.Key"/> the
    /// preset wins (first-wins). Dedup is case-sensitive on the key (env var
    /// names are case-sensitive). Entries with blank keys are skipped.
    /// </summary>
    private static List<RequiredEnvVar> MergeRequiredEnv(
        IEnumerable<RequiredEnvVar> presetContribution,
        IEnumerable<RequiredEnvVar>? adHoc)
    {
        var merged = new List<RequiredEnvVar>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rev in presetContribution.Concat(adHoc ?? Enumerable.Empty<RequiredEnvVar>()))
        {
            if (rev is null || string.IsNullOrWhiteSpace(rev.Key))
            {
                continue;
            }
            if (seen.Add(rev.Key))
            {
                merged.Add(rev);
            }
        }
        return merged;
    }

    /// <summary>
    /// Concatenate contributions in arrival order, dedupe by trimmed string
    /// (so whitespace-only differences don't double-install), then append the
    /// spec's top-level freeform extra (if any) after the contributions. The
    /// extra is NOT deduped against contributions — the operator may want it
    /// to run a second time on purpose.
    /// </summary>
    private static string JoinDeduped(IEnumerable<string> contribs, string? extra)
    {
        var seen = new HashSet<string>();
        var sb = new StringBuilder();
        foreach (var c in contribs)
        {
            if (seen.Add(c.Trim()))
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(c);
            }
        }
        if (!string.IsNullOrWhiteSpace(extra))
        {
            if (sb.Length > 0)
            {
                sb.Append('\n');
            }
            sb.Append(extra);
        }
        return sb.ToString();
    }
}
