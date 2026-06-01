using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Source.Features.EnvironmentBackup.Commands.ImportEnvironment;
using Source.Features.EnvironmentBackup.Models;
using Source.Features.EnvironmentBackup.Queries.ExportEnvironment;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.Controllers;

namespace Source.Features.EnvironmentBackup.Controllers;

/// <summary>
/// SuperAdmin-only export / import of the entire environment as a single versioned JSON
/// blob (full clone / disaster recovery). Mirrors the SystemSettings access model: the
/// blob carries secrets in clear text, so the endpoints are locked to SuperAdmin.
/// </summary>
[ApiController]
[Route("api/environment")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[Tags("Environment")]
public class EnvironmentController : BaseApiController
{
    public EnvironmentController(IMediator mediator, ILogger<EnvironmentController> logger)
        : base(mediator, logger)
    {
    }

    /// <summary>
    /// Export the full environment as a single versioned <see cref="EnvironmentSnapshotDto"/>.
    /// All in-scope secrets are decrypted to clear text inside the blob — store it securely.
    /// </summary>
    [HttpGet("export")]
    [ProducesResponseType<EnvironmentSnapshotDto>(200)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<EnvironmentSnapshotDto>> Export()
    {
        var result = await Mediator.Send(new ExportEnvironmentQuery());
        return HandleResult(result);
    }

    /// <summary>
    /// Import a previously-exported snapshot. Validates the version, restores every section
    /// in FK-safe order inside a single transaction, re-encrypts secrets under this
    /// environment's keys, and returns per-entity counts. Idempotent — safe to re-run.
    /// WARNING: overwrites existing rows that share an Id.
    /// </summary>
    [HttpPost("import")]
    [ProducesResponseType<EnvironmentImportSummary>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<EnvironmentImportSummary>> Import([FromBody] EnvironmentSnapshotDto snapshot)
    {
        var result = await Mediator.Send(new ImportEnvironmentCommand(snapshot));
        return HandleResult(result);
    }
}
