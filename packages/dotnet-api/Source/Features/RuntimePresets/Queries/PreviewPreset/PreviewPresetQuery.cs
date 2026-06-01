using System.Text.Json;
using Source.Features.RuntimePresets.Dtos;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimePresets.Queries.PreviewPreset;

/// <summary>
/// Render the preset's templates against an operator-supplied value bag and
/// return the rendered command / env / contributions for the live-preview
/// pane on the super-admin edit screen.
///
/// <para><b>Partial rendering.</b> Unlike <see cref="Services.IPresetExpander.ExpandAsync"/>
/// (which aborts on the first missing placeholder so a half-broken spec never
/// reaches the daemon), this query renders everything it can and collects
/// per-field errors into a list. The UI shows them inline next to the broken
/// field so the operator iterates on the form without the whole pane
/// collapsing to a 400.</para>
///
/// <para><b>Auth.</b> Gated at the controller via the super-admin attribute;
/// no per-handler check (this is a pure render of operator-owned data).</para>
/// </summary>
public sealed record PreviewPresetQuery(
    Guid PresetId,
    Dictionary<string, JsonElement>? Values
) : IQuery<Result<PreviewPresetResponse>>;
