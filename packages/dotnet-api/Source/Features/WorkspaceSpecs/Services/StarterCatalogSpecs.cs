using Source.Features.RuntimePresets.Contracts;
using Source.Features.WorkspaceSpecs.Models;

namespace Source.Features.WorkspaceSpecs.Services;

/// <summary>
/// System-seeded starter specs inserted into every newly-created workspace's
/// catalog (Scene 6 of the workspace-spec-catalog spec). The user should never
/// face an empty catalog on day one — there's always at least a <c>blank</c>
/// entry to pick from on the first project.
///
/// <para><b>Now V3-shaped.</b> The V2→V3 cutover left this catalog empty of
/// realistic recipes — the legacy V2 starters were not viable to translate
/// 1:1 (V3 services are preset references, not inline bash) and we'd rather
/// ship a single safe empty starter than auto-translate a recipe nobody has
/// validated end-to-end. The starter library will get repopulated against
/// real V3 presets (e.g. <c>dotnet-mise</c> + <c>node-vite</c> + <c>postgres-15</c>)
/// as those presets land — for now the catalog is intentionally minimal.</para>
///
/// <para><b>Validity is enforced at seed time.</b> <see cref="BuildFor"/> runs
/// every starter's <see cref="RuntimeSpecV3.TryParse"/> + <see cref="RuntimeSpecV3.Validate"/>
/// and throws an <see cref="InvalidOperationException"/> if any starter is
/// malformed. Better to crash workspace creation than to silently poison the
/// catalog of every new workspace with bad data.</para>
/// </summary>
internal static class StarterCatalogSpecs
{
    /// <summary>
    /// Build the three starter <see cref="WorkspaceSpec"/> rows for
    /// <paramref name="workspaceId"/>, owned by <paramref name="ownerUserId"/>.
    /// Throws on any internal validation failure — see class remarks.
    /// </summary>
    public static IReadOnlyList<WorkspaceSpec> BuildFor(Guid workspaceId, string ownerUserId)
    {
        var seeds = new[]
        {
            (Name: "blank",
             Description: "Empty runtime. No services configured yet.",
             Content: BlankContent),
        };

        var rows = new List<WorkspaceSpec>(seeds.Length);
        foreach (var seed in seeds)
        {
            // Defence-in-depth: parse + validate each starter so a typo here
            // crashes workspace creation rather than silently shipping a bad
            // seed into every new workspace.
            var parsed = RuntimeSpecV3.TryParse(seed.Content);
            if (parsed is null)
            {
                throw new InvalidOperationException(
                    $"Starter catalog spec '{seed.Name}' is not valid V3 JSON.");
            }
            var validated = parsed.Validate();
            if (!validated.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Starter catalog spec '{seed.Name}' failed V3 validation: {validated.Error}");
            }

            rows.Add(new WorkspaceSpec
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                Name = seed.Name,
                Description = seed.Description,
                Content = seed.Content,
                CreatedByUserId = ownerUserId,
                UpdatedByUserId = ownerUserId,
                // CreatedAt / UpdatedAt auto-set by IAuditable interceptor.
            });
        }

        return rows;
    }

    // ----------------------------------------------------------------------
    // Starter spec JSON. Kept as raw strings (not record literals) so what
    // ends up in the jsonb column is byte-identical to what we authored —
    // easier to grep / spot-check in production.
    // ----------------------------------------------------------------------

    /// <summary>
    /// Minimal valid V3 spec. V3 requires at least one service to validate,
    /// so the blank starter ships a single placeholder <c>bash-raw</c> service
    /// the user is expected to edit before approving. This is the smallest
    /// possible spec that round-trips through <see cref="RuntimeSpecV3.Validate"/>
    /// — anything emptier would fail the seed-time guard.
    /// </summary>
    private const string BlankContent = """
        {
          "version": 3,
          "install": "",
          "setup": "",
          "services": [
            {
              "kind": "bash-raw",
              "name": "placeholder",
              "values": {
                "command": "sleep infinity"
              }
            }
          ]
        }
        """;

}
