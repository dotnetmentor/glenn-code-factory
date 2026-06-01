using Source.Features.ProjectKanban.Models;
using Tapper;

namespace Source.Features.SignalR.Contracts;

/// <summary>
/// Coarse change-kind discriminator carried on every planning broadcast payload.
/// Lets a single typed client method (e.g. <c>SpecificationChanged</c>) carry
/// every lifecycle transition on the entity without ballooning the
/// <c>IPlanningClient</c> interface to one method per (entity × verb) pair.
///
/// <para>Values are emitted as ordinal ints over the wire (Tapper's default for
/// enums); the React side switches on the numeric value. Stable ordinals are
/// load-bearing — DO NOT reorder, only append.</para>
/// </summary>
[TranspilationSource]
public enum PlanningChangeKind
{
    Created = 0,
    Updated = 1,
    Moved = 2,
    Deleted = 3,
    Toggled = 4,
}

/// <summary>
/// Pushed to the <c>project:{ProjectId}</c> group when a <c>Specification</c>
/// row changes (created via <c>SaveSpecificationCommand</c>'s factory branch,
/// updated via the upsert branch, soft-deleted via <c>MarkDeleted</c>).
///
/// <para>Payload-light by design — the frontend re-fetches via Orval on
/// receipt. <see cref="Slug"/> is kept on the wire because it's a meaningful
/// human-readable hint the UI can show in a "spec X changed" toast without a
/// second round-trip, and because the slug is the URL key the spec list /
/// detail pages route on.</para>
/// </summary>
[TranspilationSource]
public record SpecificationChangedNotification(
    PlanningChangeKind Kind,
    Guid ProjectId,
    Guid SpecificationId,
    string Slug,
    DateTime OccurredAt);

/// <summary>
/// Pushed to the <c>project:{ProjectId}</c> group for every lifecycle
/// transition on a <c>ProjectKanbanCard</c>. Drives live kanban-board
/// re-renders without polling.
///
/// <para><see cref="PlanningChangeKind.Moved"/> is distinct from
/// <see cref="PlanningChangeKind.Updated"/> so the UI can animate column /
/// position changes differently from in-place metadata edits. Payload-light —
/// the React side re-fetches the card (or the whole board) on receipt; the
/// new column / position is intentionally NOT on the wire to keep the contract
/// stable through future schema changes.</para>
///
/// <para><b>Provenance fields</b> (<see cref="Source"/>, <see cref="CreatedOnBranch"/>)
/// are populated only on <see cref="PlanningChangeKind.Created"/> broadcasts —
/// the spec scopes provenance to "where did this card come from", and a card's
/// origin never changes after birth. Other lifecycle kinds leave them at their
/// defaults (<see cref="ProjectKanbanCardSource.Human"/> / <c>null</c>) and the
/// React subscriber should ignore them outside the create path.</para>
/// </summary>
[TranspilationSource]
public record CardChangedNotification(
    PlanningChangeKind Kind,
    Guid ProjectId,
    Guid CardId,
    DateTime OccurredAt,
    ProjectKanbanCardSource Source = ProjectKanbanCardSource.Human,
    string? CreatedOnBranch = null);

/// <summary>
/// Pushed to the <c>project:{ProjectId}</c> group whenever a
/// <c>ProjectKanbanCardSubtask</c> row changes. Carries both the parent
/// <see cref="CardId"/> (resolved server-side from the subtask's owning card)
/// and the affected <see cref="SubtaskId"/> so a subscriber rendering one
/// card's checklist can decide whether to refetch.
///
/// <para>Project routing matches the card broadcast — subscribers join one
/// SignalR group per project, not per card. The card-level filter is a client
/// concern.</para>
/// </summary>
[TranspilationSource]
public record SubtaskChangedNotification(
    PlanningChangeKind Kind,
    Guid ProjectId,
    Guid CardId,
    Guid SubtaskId,
    DateTime OccurredAt);
