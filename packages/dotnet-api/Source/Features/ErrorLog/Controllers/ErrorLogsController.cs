using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Source.Features.ErrorLog.Commands;
using Source.Features.ErrorLog.Models;
using Source.Features.ErrorLog.Queries;
using Source.Infrastructure.AuthorizationModels;
using Source.Shared.Controllers;
using System.ComponentModel.DataAnnotations;

namespace Source.Features.ErrorLog.Controllers;

[Route("api/error-logs")]
[Authorize(Roles = RoleConstants.SuperAdmin)]
[EnableRateLimiting("GeneralPolicy")]
[Tags("ErrorLogs")]
public class ErrorLogsController : BaseApiController
{
    public ErrorLogsController(IMediator mediator, ILogger<ErrorLogsController> logger)
        : base(mediator, logger)
    {
    }

    [HttpGet]
    [ProducesResponseType<GetAllErrorLogsResponse>(200)]
    public async Task<ActionResult<GetAllErrorLogsResponse>> GetAllErrorLogs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? source = null,
        [FromQuery] string? severity = null,
        [FromQuery] bool? isResolved = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        (page, pageSize) = ValidatePagination(page, pageSize);

        var query = new GetAllErrorLogsQuery
        {
            Page = page,
            PageSize = pageSize,
            Search = search,
            Source = source,
            Severity = severity,
            IsResolved = isResolved,
            FromDate = fromDate,
            ToDate = toDate
        };
        var result = await Mediator.Send(query);

        return HandleResult(result);
    }

    [HttpGet("count")]
    [ProducesResponseType<GetUnresolvedErrorCountResponse>(200)]
    public async Task<ActionResult<GetUnresolvedErrorCountResponse>> GetUnresolvedErrorCount()
    {
        var query = new GetUnresolvedErrorCountQuery();
        var result = await Mediator.Send(query);

        return HandleResult(result);
    }

    /// <summary>
    /// Returns a JSON snapshot of the error-capture pipeline's five monotonic counters
    /// (Enqueued, Dropped, Persisted, PersistFailed, Suppressed) plus the current
    /// approximate queue depth. Mirrors what <c>ErrorPipelineSummaryReporter</c> logs
    /// every minute, but in structured form for dashboards.
    /// </summary>
    [HttpGet("pipeline-stats")]
    [ProducesResponseType<GetErrorPipelineStatsResponse>(200)]
    public async Task<ActionResult<GetErrorPipelineStatsResponse>> GetPipelineStats()
    {
        var query = new GetErrorPipelineStatsQuery();
        var result = await Mediator.Send(query);

        return HandleResult(result);
    }

    [HttpGet("{id}")]
    [ProducesResponseType<ErrorLogResponse>(200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ErrorLogResponse>> GetErrorLog(Guid id)
    {
        var query = new GetErrorLogQuery(id);
        var result = await Mediator.Send(query);

        return HandleResultWithNotFound(result);
    }

    [HttpPost]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType<CreateErrorLogResponse>(201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<CreateErrorLogResponse>> CreateErrorLog([FromBody] CreateErrorLogRequest request)
    {
        var command = new CreateErrorLogCommand
        {
            Message = request.Message,
            StackTrace = request.StackTrace,
            Source = request.Source,
            Severity = request.Severity,
            CorrelationId = request.CorrelationId,
            RequestPath = request.RequestPath,
            RequestMethod = request.RequestMethod,
            ContextData = request.ContextData,
            IsResolved = request.IsResolved,
            ResolvedAt = request.ResolvedAt,
        };
        var result = await Mediator.Send(command);

        return HandleCreatedResult(result, nameof(GetErrorLog), new { id = result.IsSuccess ? result.Value.Id : default(Guid) });
    }

    [HttpPut("{id}")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType<UpdateErrorLogResponse>(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<UpdateErrorLogResponse>> UpdateErrorLog(Guid id, [FromBody] UpdateErrorLogRequest request)
    {
        var command = new UpdateErrorLogCommand
        {
            Id = id,
            Message = request.Message,
            StackTrace = request.StackTrace,
            Source = request.Source,
            Severity = request.Severity,
            CorrelationId = request.CorrelationId,
            RequestPath = request.RequestPath,
            RequestMethod = request.RequestMethod,
            ContextData = request.ContextData,
            IsResolved = request.IsResolved,
            ResolvedAt = request.ResolvedAt,
        };
        var result = await Mediator.Send(command);

        return HandleResultWithNotFound(result);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType<DeleteErrorLogResponse>(200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<DeleteErrorLogResponse>> DeleteErrorLog(Guid id)
    {
        var command = new DeleteErrorLogCommand(id);
        var result = await Mediator.Send(command);

        return HandleResultWithNotFound(result);
    }

    [HttpPut("bulk-resolve")]
    [Authorize(Roles = RoleConstants.SuperAdmin)]
    [ProducesResponseType<BulkResolveErrorLogsResponse>(200)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<BulkResolveErrorLogsResponse>> BulkResolve([FromBody] BulkResolveErrorLogsCommand command)
    {
        var result = await Mediator.Send(command);

        return HandleResult(result);
    }
}

public record CreateErrorLogRequest
{
    [Required]
    [StringLength(2000)]
    public required string Message { get; init; } = string.Empty;
    public string? StackTrace { get; init; }
    [Required]
    [StringLength(50)]
    public required string Source { get; init; } = string.Empty;
    [Required]
    [StringLength(20)]
    public required string Severity { get; init; } = string.Empty;
    [StringLength(100)]
    public string? CorrelationId { get; init; }
    [StringLength(500)]
    public string? RequestPath { get; init; }
    [StringLength(10)]
    public string? RequestMethod { get; init; }
    public string? ContextData { get; init; }
    public bool? IsResolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
}

public record UpdateErrorLogRequest
{
    [StringLength(2000)]
    public string? Message { get; init; }
    public string? StackTrace { get; init; }
    [StringLength(50)]
    public string? Source { get; init; }
    [StringLength(20)]
    public string? Severity { get; init; }
    [StringLength(100)]
    public string? CorrelationId { get; init; }
    [StringLength(500)]
    public string? RequestPath { get; init; }
    [StringLength(10)]
    public string? RequestMethod { get; init; }
    public string? ContextData { get; init; }
    public bool? IsResolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
}
