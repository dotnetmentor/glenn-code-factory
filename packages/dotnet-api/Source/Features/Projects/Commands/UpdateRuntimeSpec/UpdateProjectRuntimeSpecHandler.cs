using Microsoft.EntityFrameworkCore;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.Projects.Commands.UpdateRuntimeSpec;

/// <summary>
/// Handles <see cref="UpdateProjectRuntimeSpecCommand"/>. Two-line pipeline:
///
/// <list type="number">
///   <item><b>Load + delegate.</b> Tracked load of the project (global IsDeleted
///         filter excludes tombstones), call <c>Project.SetRuntimeSpec(...)</c>
///         which validates every field, enforces the Fly performance-class
///         memory floor, and either mutates + raises a domain event or returns
///         a sentinel error for the controller to map to 400.</item>
///   <item><b>Save.</b> Single SaveChanges; the rich method already short-circuits
///         on no-op so re-submitting the same values is a true no-op (no
///         StoredDomainEvents row, no UpdatedAt touch).</item>
/// </list>
///
/// <para><b>No Cloudflare fan-out, no SignalR push.</b> Unlike preview-port
/// changes, runtime-spec changes do not affect live infrastructure. They take
/// effect on the next <c>ProjectRuntime</c> creation (new branch / fork /
/// attach / AI onboarding) — there is nothing to propagate elsewhere. If we
/// later want other tabs to live-update, a SignalR push can be added without
/// changing the handler's contract.</para>
/// </summary>
public sealed class UpdateProjectRuntimeSpecHandler
    : ICommandHandler<UpdateProjectRuntimeSpecCommand, Result<UpdateProjectRuntimeSpecResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<UpdateProjectRuntimeSpecHandler> _logger;

    public UpdateProjectRuntimeSpecHandler(
        ApplicationDbContext db,
        ILogger<UpdateProjectRuntimeSpecHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<UpdateProjectRuntimeSpecResponse>> Handle(
        UpdateProjectRuntimeSpecCommand request,
        CancellationToken cancellationToken)
    {
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == request.ProjectId, cancellationToken);

        if (project is null)
        {
            return Result.Failure<UpdateProjectRuntimeSpecResponse>("Project not found");
        }

        var setResult = project.SetRuntimeSpec(
            cpuKind: request.CpuKind,
            cpus: request.Cpus,
            memoryMb: request.MemoryMb,
            volumeSizeGb: request.VolumeSizeGb);

        if (setResult.IsFailure)
        {
            return Result.Failure<UpdateProjectRuntimeSpecResponse>(setResult.Error!);
        }

        // SaveChanges is a no-op (no tracked changes) when SetRuntimeSpec
        // short-circuits because every field already matched. Still cheap and
        // keeps the pipeline uniform.
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Updated runtime spec for project {ProjectId}: cpuKind={CpuKind} cpus={Cpus} memoryMb={MemoryMb} volumeSizeGb={VolumeSizeGb}",
            request.ProjectId, project.RuntimeCpuKind, project.RuntimeCpus, project.RuntimeMemoryMb, project.RuntimeVolumeSizeGb);

        return Result.Success(new UpdateProjectRuntimeSpecResponse(
            ProjectId: project.Id,
            CpuKind: project.RuntimeCpuKind,
            Cpus: project.RuntimeCpus,
            MemoryMb: project.RuntimeMemoryMb,
            VolumeSizeGb: project.RuntimeVolumeSizeGb));
    }
}
