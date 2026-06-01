using Source.Features.RuntimeLifecycle.Models;
using Source.Features.RuntimePresets.Contracts;
using Tapper;

namespace Source.Features.RuntimeCuration.Models;

/// <summary>
/// HTTP-response projection of the <i>project</i>'s currently installed spec
/// — the data the Project Settings → Runtime → Spec tab renders. Driven off
/// <see cref="Source.Features.Projects.Models.Project.Spec"/> parsed into a
/// typed <see cref="RuntimeSpecV3"/> so the frontend doesn't have to re-parse
/// on every render.
///
/// <para>Phase 1 / pre-curation projects have <c>Spec = null</c>; the read
/// handler treats null as an empty spec so the tab renders "no spec yet"
/// without a separate code path. Same defensive parsing applies to malformed
/// JSON (treated as empty + a warn log) so a corrupt spec never blocks a
/// page-load.</para>
///
/// <para><b>RuntimeId</b> is the most-recent live runtime under the project
/// (nullable — null when no runtimes exist yet). The frontend uses it to
/// target a SignalR group for Edit / Save-to-Catalog delta pushes; when
/// null, those buttons are disabled. <b>State</b> mirrors that runtime's
/// lifecycle position. <b>SpecUpdatedAt</b> is the project row's last
/// write — close-enough proxy for "when did the spec last change" since the
/// spec lives on the same row. Null on Phase 1 projects that never had their
/// row touched.</para>
///
/// <para><see cref="TranspilationSourceAttribute"/> exposes the DTO to Tapper
/// so the frontend's queries-commands.ts surfaces the type alongside the
/// generated <c>useGetApiProjectsProjectIdRuntimeSpec</c> hook.</para>
/// </summary>
[TranspilationSource]
public record ProjectRuntimeSpecDto(
    Guid? RuntimeId,
    Guid ProjectId,
    RuntimeState? State,
    RuntimeSpecV3 Spec,
    DateTime? SpecUpdatedAt);
