using Source.Features.Conversations.Events;
using Source.Features.CursorModels.Models;
using Source.Features.RuntimeLifecycle.Models;
using Source.Shared;
using Source.Shared.Events;
using Source.Shared.Results;

namespace Source.Features.Conversations.Models;

/// <summary>
/// One round-trip of agent work inside a <see cref="Conversation"/> — a single
/// prompt the user submitted, plus everything the agent did in response.
/// </summary>
public class AgentSession : Entity, IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ConversationId { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public AgentSessionStatus Status { get; set; } = AgentSessionStatus.Pending;

    /// <summary>
    /// Cursor SDK persistent agent identity captured on first response. Used
    /// to resume context on subsequent turns via <c>Agent.resume(agentId)</c>.
    /// </summary>
    public string? AgentId { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public int SelfHealAttempts { get; set; }
    public Guid RuntimeId { get; set; }
    public int? QueuePosition { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>
    /// Per-session model override. <c>null</c> means fall back to the project default.
    /// </summary>
    public Guid? ModelId { get; set; }

    public decimal? TotalCostUsd { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheReadTokens { get; set; }
    public int? CacheWriteTokens { get; set; }
    public int? ReasoningTokens { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Conversation Conversation { get; set; } = null!;
    public ProjectRuntime Runtime { get; set; } = null!;
    public CursorModel? Model { get; set; }
    public ICollection<AgentEvent> Events { get; set; } = new List<AgentEvent>();

    /// <summary>
    /// Per-turn aggregate written when the run reaches a terminal state — see
    /// <see cref="RunResult"/>. Nullable: only Succeeded runs that emit a
    /// Cursor <c>RunResult</c> populate this. One-to-one with cascade delete.
    /// </summary>
    public RunResult? RunResult { get; set; }

    public void RecordEventEmitted(AgentEventEmitted @event) => RaiseDomainEvent(@event);

    public void Enqueue(int queuePosition)
    {
        if (Status != AgentSessionStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Cannot enqueue session {Id}: status is {Status}, must be Pending.");
        }

        QueuePosition = queuePosition;
        RaiseDomainEvent(new SessionEnqueued(Id, RuntimeId, queuePosition));
    }

    public void Dispatch()
    {
        if (Status != AgentSessionStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Cannot dispatch session {Id}: status is {Status}, must be Pending.");
        }

        Status = AgentSessionStatus.Running;
        QueuePosition = null;
        RaiseDomainEvent(new SessionDispatched(Id, RuntimeId));
    }

    public void Succeed()
    {
        if (Status is not (AgentSessionStatus.Running or AgentSessionStatus.Canceling))
        {
            throw new InvalidOperationException(
                $"Cannot mark session {Id} Succeeded from {Status}; must be Running or Canceling.");
        }

        Status = AgentSessionStatus.Succeeded;
        QueuePosition = null;
        CompletedAt = DateTime.UtcNow;
        RaiseDomainEvent(new AgentSessionTerminated(Id, RuntimeId, Status, null));
    }

    public void Fail(string? reason)
    {
        if (Status is AgentSessionStatus.Succeeded
            or AgentSessionStatus.Failed
            or AgentSessionStatus.Canceled)
        {
            return;
        }

        Status = AgentSessionStatus.Failed;
        if (reason is not null)
        {
            FailureReason = reason;
        }
        QueuePosition = null;
        CompletedAt = DateTime.UtcNow;
        RaiseDomainEvent(new AgentSessionTerminated(Id, RuntimeId, Status, reason ?? FailureReason));
    }

    public void MarkCanceling(string reason)
    {
        if (Status is AgentSessionStatus.Canceling
            or AgentSessionStatus.Canceled
            or AgentSessionStatus.Succeeded
            or AgentSessionStatus.Failed)
        {
            return;
        }

        if (Status != AgentSessionStatus.Running)
        {
            throw new InvalidOperationException(
                $"Cannot mark session {Id} Canceling from {Status}; must be Running. " +
                "Pending sessions go straight to Canceled via MarkCanceled.");
        }

        Status = AgentSessionStatus.Canceling;
        CancelReason = reason;
        RaiseDomainEvent(new SessionCancelRequested(Id, RuntimeId, reason));
    }

    public Result MarkRefused(string reason)
    {
        if (Status is AgentSessionStatus.Succeeded
            or AgentSessionStatus.Failed
            or AgentSessionStatus.Canceled)
        {
            return Result.Success();
        }

        Status = AgentSessionStatus.Failed;
        CancelReason = reason;
        QueuePosition = null;
        CompletedAt = DateTime.UtcNow;
        RaiseDomainEvent(new SessionRefused(Id, reason));
        return Result.Success();
    }

    public void MarkCanceled(string? reason)
    {
        if (Status is AgentSessionStatus.Canceled
            or AgentSessionStatus.Succeeded
            or AgentSessionStatus.Failed)
        {
            return;
        }

        Status = AgentSessionStatus.Canceled;
        if (reason is not null)
        {
            CancelReason = reason;
        }
        QueuePosition = null;
        CompletedAt = DateTime.UtcNow;
        RaiseDomainEvent(new AgentSessionTerminated(Id, RuntimeId, Status, reason ?? CancelReason));
    }
}
