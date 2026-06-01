using Microsoft.EntityFrameworkCore;
using Source.Features.RuntimeCuration.Models;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.RuntimePresets.Services;
using Source.Infrastructure;
using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.RuntimeCuration.Commands;

/// <summary>
/// User-initiated direct write of a <see cref="RuntimeSpecV3"/> to a
/// <see cref="Source.Features.Projects.Models.Project"/>'s persisted spec,
/// bypassing the propose-and-approve flow. This is the "paste it in myself"
/// path — used from Project Settings when the user already knows what they
/// want and doesn't need the chat agent to draft a <see cref="RuntimeProposal"/>
/// first.
///
/// <list type="number">
///   <item>Load the project; soft-deleted / missing → <c>not_found</c>.</item>
///   <item>Validate the supplied V3 spec via <see cref="RuntimeSpecV3.Validate"/>
///         (structural invariants — version stamp, services non-empty, unique
///         kind/name pairs); on failure return the validator's stable code.</item>
///   <item>Run the spec through <see cref="IPresetExpander.ExpandAsync"/> to
///         confirm every preset slug resolves + every parameter type-checks.
///         The expansion is discarded — this path doesn't persist a V2 wire
///         shape (no proposal row, no daemon push); we just want the expander
///         to act as the deeper validator.</item>
///   <item>Serialise the V3 spec to JSON, write it to
///         <see cref="Source.Features.Projects.Models.Project.Spec"/>, bump
///         <see cref="Source.Features.Projects.Models.Project.SpecVersion"/>
///         by one. Same write target + version-bump contract as
///         <see cref="ApproveProposalCommand"/>.</item>
/// </list>
///
/// <para><b>No daemon push.</b> Unlike <see cref="ApproveProposalCommand"/>
/// / <see cref="EditProposalCommand"/>, this path does NOT push an
/// <see cref="SignalR.Hubs.IRuntimeClient.ApplyRuntimeSpecDelta"/> to live
/// runtimes. The user is authoring fresh JSON — they aren't reviewing a diff
/// against what's currently running, so a live push risks disrupting
/// in-flight services on the daemon for changes the operator hasn't audited.
/// Live runtimes converge on their next cold-boot / wake / respawn via
/// <see cref="Source.Features.RuntimeBootstrap.Queries.GetBootstrapQuery"/>,
/// which reads from the project row and re-expands V3→V2 there. To apply
/// immediately, restart the runtime explicitly.</para>
///
/// <para><b>No proposal row.</b> Unlike <see cref="ApproveProposalCommand"/> /
/// <see cref="EditProposalCommand"/> this does NOT create a
/// <see cref="RuntimeProposal"/>. The user is asserting "this is what I want"
/// without a chat-agent draft to review — the audit trail lives in
/// <see cref="Source.Features.Projects.Models.Project.SpecVersion"/> bumps + the
/// project's <c>UpdatedAt</c> column, not in the proposals table.</para>
/// </summary>
public record SetProjectSpecCommand(
    Guid ProjectId,
    RuntimeSpecV3 Spec) : ICommand<Result<SetProjectSpecResponse>>;

/// <summary>
/// Wire shape returned to the frontend after a successful direct-write. The
/// SettingsUI uses <see cref="SpecVersion"/> to render the post-write "version
/// N" badge without a refetch, and <see cref="UpdatedAt"/> to stamp the "last
/// edited" timestamp.
/// </summary>
public record SetProjectSpecResponse(int SpecVersion, DateTime UpdatedAt);

public class SetProjectSpecCommandHandler
    : ICommandHandler<SetProjectSpecCommand, Result<SetProjectSpecResponse>>
{
    private readonly ApplicationDbContext _db;
    private readonly IPresetExpander _expander;
    private readonly ILogger<SetProjectSpecCommandHandler> _logger;

    public SetProjectSpecCommandHandler(
        ApplicationDbContext db,
        IPresetExpander expander,
        ILogger<SetProjectSpecCommandHandler> logger)
    {
        _db = db;
        _expander = expander;
        _logger = logger;
    }

    public async Task<Result<SetProjectSpecResponse>> Handle(
        SetProjectSpecCommand request,
        CancellationToken cancellationToken)
    {
        // Soft-deleted projects are filtered by the global query filter — the
        // failure surfaces as not_found so the UI doesn't need a second code
        // path for "project was just deleted".
        var project = await _db.Projects
            .FirstOrDefaultAsync(p => p.Id == request.ProjectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<SetProjectSpecResponse>("not_found");
        }

        // Structural V3 validation.
        var validate = request.Spec.Validate();
        if (validate.IsFailure)
        {
            return Result.Failure<SetProjectSpecResponse>(validate.Error!);
        }

        // Deep validation via the expander — confirms every preset slug
        // resolves and every parameter type-checks. The expansion itself is
        // discarded since this path doesn't push to a daemon; we just want
        // the expander's catalog of failure codes to surface here.
        var expansion = await _expander.ExpandAsync(request.Spec, cancellationToken);
        if (expansion.IsFailure)
        {
            return Result.Failure<SetProjectSpecResponse>(expansion.Error!);
        }

        // V3 semantics: the supplied spec replaces the project's persisted
        // spec wholesale (no additive merge). Serialise via
        // RuntimeSpecV3.JsonOptions so on-disk rows are byte-for-byte
        // comparable regardless of which path wrote them.
        project.Spec = request.Spec.ToJson();
        project.SpecVersion = project.SpecVersion + 1;

        await _db.SaveChangesAsync(cancellationToken);

        // Deliberately no daemon push here. Live runtimes converge on their
        // next cold-boot / wake / respawn through GetBootstrapQuery (which
        // reads project.Spec and re-expands V3→V2). Pushing a delta into a
        // running daemon for a freshly-pasted spec risks disrupting in-flight
        // services on changes the operator hasn't seen against current state
        // — that's a different contract from Approve, where the user
        // explicitly reviewed the diff.
        _logger.LogInformation(
            "SetProjectSpec: project {ProjectId} spec set directly (version {SpecVersion}); no daemon push (runtimes converge on next bootstrap).",
            project.Id, project.SpecVersion);

        return Result.Success(new SetProjectSpecResponse(
            SpecVersion: project.SpecVersion,
            UpdatedAt: project.UpdatedAt));
    }
}
