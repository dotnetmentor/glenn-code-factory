using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeCuration.Queries;

/// <summary>
/// Read the <i>project's</i> currently installed V3 spec — the data that
/// drives the Project Settings → Runtime → Spec tab. Plain project lookup
/// since runtime spec moved from <c>ProjectRuntime</c> to <c>Project</c>
/// (<c>project-level-runtime-spec</c> spec).
///
/// <para><b>Spec parsing is defensive.</b> Null/empty <c>Project.Spec</c>
/// (Phase 1 projects) → empty <see cref="RuntimeSpecV3"/> in the DTO.
/// Malformed JSON → empty spec + a warning log; we never fail a read because
/// some legacy row has a hand-edited body.</para>
///
/// <para><b>404 vs empty.</b> Project missing → <c>not_found</c>. A project
/// with a null/empty spec → 200 with an empty <see cref="RuntimeSpecV3"/> so
/// the Spec tab can render "no spec installed yet" without a separate code
/// path. The DTO's <see cref="ProjectRuntimeSpecDto.RuntimeId"/> is the
/// most-recent live runtime under the project (null if none exist) so the
/// frontend can still target a SignalR group for Edit / Save-to-Catalog
/// delta pushes.</para>
/// </summary>
public record GetProjectRuntimeSpecQuery(Guid ProjectId)
    : IQuery<Result<ProjectRuntimeSpecDto>>;

public class GetProjectRuntimeSpecQueryHandler
    : IQueryHandler<GetProjectRuntimeSpecQuery, Result<ProjectRuntimeSpecDto>>
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<GetProjectRuntimeSpecQueryHandler> _logger;

    public GetProjectRuntimeSpecQueryHandler(
        ApplicationDbContext db,
        ILogger<GetProjectRuntimeSpecQueryHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<ProjectRuntimeSpecDto>> Handle(
        GetProjectRuntimeSpecQuery request,
        CancellationToken cancellationToken)
    {
        // Project is the source of truth for spec since the
        // `project-level-runtime-spec` cutover. Soft-deleted projects are
        // filtered by the global query filter.
        var project = await _db.Projects
            .AsNoTracking()
            .Where(p => p.Id == request.ProjectId)
            .Select(p => new
            {
                p.Id,
                p.Spec,
                p.UpdatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (project is null)
        {
            return Result.Failure<ProjectRuntimeSpecDto>("not_found");
        }

        // Separate lookup for "most-recent live runtime under this project"
        // — the DTO surfaces it so the frontend can target a SignalR group
        // for Edit / Save-to-Catalog delta pushes. Soft-deleted runtimes are
        // filtered by the global query filter. Returns null when the project
        // has no live runtimes (Spec tab disables Edit / Save buttons until
        // one spins up).
        var latestRuntime = await _db.ProjectRuntimes
            .AsNoTracking()
            .Where(r => r.ProjectId == request.ProjectId)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.Id, r.State })
            .FirstOrDefaultAsync(cancellationToken);

        RuntimeSpecV3 spec;
        if (string.IsNullOrWhiteSpace(project.Spec))
        {
            spec = new RuntimeSpecV3 { Services = new() };
        }
        else
        {
            var parsed = RuntimeSpecV3.TryParse(project.Spec);
            if (parsed is null)
            {
                // Non-empty body that doesn't parse as V3 — most likely a
                // legacy V2 row that hasn't been re-authored yet, or a hand-
                // edited shape. Log so an operator can investigate, but don't
                // fail the read; the Spec tab renders empty rather than 500.
                _logger.LogWarning(
                    "GetProjectRuntimeSpec: project {ProjectId} has a Spec body that does not parse as V3; returning empty spec.",
                    project.Id);
                spec = new RuntimeSpecV3 { Services = new() };
            }
            else
            {
                spec = parsed;
            }
        }

        return Result.Success(new ProjectRuntimeSpecDto(
            RuntimeId: latestRuntime?.Id,
            ProjectId: project.Id,
            State: latestRuntime?.State,
            Spec: spec,
            // Approximate; see DTO comment. UpdatedAt is the project's last
            // write, which includes spec replacements from Approve / Edit
            // handlers.
            SpecUpdatedAt: project.UpdatedAt));
    }
}
