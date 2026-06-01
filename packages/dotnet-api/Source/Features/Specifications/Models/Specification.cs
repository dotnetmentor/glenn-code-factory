using System.Text.RegularExpressions;
using Source.Features.Specifications.Events;
using Source.Shared;
using Source.Shared.Events;
using Source.Shared.Results;

namespace Source.Features.Specifications.Models;

/// <summary>
/// One product specification for a project. The planning subagent writes specs
/// via the platform's MCP layer; the main agent reads them to seed kanban cards
/// (Card 2+ of the platform-planning-kanban spec).
///
/// <list type="bullet">
///   <item><see cref="ProjectId"/> is a plain Guid (no FK). Mirrors the
///         <c>ProjectKanbanCard</c> / <c>ProjectRuntime</c> / <c>ProjectSecret</c>
///         convention — the Project entity is owned by another feature slice
///         and the spec row must outlive any future project hard-delete.</item>
///   <item><see cref="Slug"/> is the URL-friendly identifier the daemon uses
///         when referencing a spec from a kanban card description ("📋 Spec:
///         {slug}"). Kept stable across edits; uniqueness is enforced per
///         project via a filtered unique index on <c>(ProjectId, Slug)</c>
///         where <c>IsDeleted = false</c> so re-creating a deleted slug is
///         allowed.</item>
///   <item><see cref="Content"/> is unbounded markdown — Postgres <c>text</c>
///         column, mirroring <c>AgentSession.Prompt</c> and
///         <c>ProjectKanbanCard.Description</c>. No <c>jsonb</c> because the
///         body is plain markdown, not structured data.</item>
///   <item><see cref="Status"/> is an enum (currently single-value
///         <see cref="SpecificationStatus.Draft"/>) so future statuses can land
///         without a column-type migration. The spec's non-goals are explicit:
///         no <c>Accepted</c> state, no approval workflow.</item>
///   <item><see cref="CreatedBy"/> is the FK to <c>User.Id</c>. Identity user
///         ids are strings up to 450 chars. Nullable because MCP calls have no
///         end-user identity — the planning subagent writes specs through a
///         runtime token and records <c>"runtime:&lt;runtimeId&gt;"</c>; that
///         string doesn't satisfy the FK so we set it to null and capture the
///         actor in the audit trail instead. <c>OnDelete=Restrict</c> mirrors
///         <c>ProjectKanbanCard.CreatedBy</c>.</item>
///   <item>Soft-deletable; the global query filter hides deleted rows so the
///         filtered unique index above is the only thing needed for "re-create
///         after delete" to work.</item>
/// </list>
///
/// <para><b>Rich entity.</b> Inherits <see cref="Entity"/> so every state
/// transition raises a domain event in the same place it mutates fields —
/// <see cref="Create"/>, <see cref="UpdateContent"/>, <see cref="MarkDeleted"/>.
/// Card 3 of the spec wires SignalR broadcasts to those events; this card just
/// emits them and lets the <c>DomainEventInterceptor</c> persist them to the
/// <c>StoredDomainEvents</c> audit table.</para>
/// </summary>
public class Specification : Entity, IAuditable, ISoftDelete
{
    /// <summary>
    /// Loose validation for a URL-friendly slug: lowercase letters, digits, and
    /// hyphens; cannot start or end with a hyphen; no consecutive hyphens; not
    /// empty. We're tolerant of weirdness rather than strict — the daemon
    /// generates slugs itself, so this is a sanity check, not a hostile-input
    /// gate.
    /// </summary>
    private static readonly Regex SlugPattern = new(
        @"^[a-z0-9]+(-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const int MaxSlugLength = 200;
    public const int MaxNameLength = 500;

    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Project the spec belongs to. Plain Guid (no FK) — see the type-level
    /// XML doc for the rationale.
    /// </summary>
    public Guid ProjectId { get; private set; }

    /// <summary>
    /// URL-friendly identifier, unique per project among non-deleted rows.
    /// Lowercase-kebab; immutable after creation (the upsert flow keys on it).
    /// </summary>
    public string Slug { get; private set; } = string.Empty;

    /// <summary>
    /// Human-readable display name. Mutable via <see cref="UpdateContent"/>.
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Markdown body. Postgres <c>text</c> column (no max length). Mutable via
    /// <see cref="UpdateContent"/>.
    /// </summary>
    public string Content { get; private set; } = string.Empty;

    /// <summary>Lifecycle bucket. See <see cref="SpecificationStatus"/>.</summary>
    public SpecificationStatus Status { get; private set; }

    /// <summary>
    /// FK to <c>User.Id</c>. Nullable because MCP calls have no end-user identity
    /// — see the type-level XML doc.
    /// </summary>
    public string? CreatedBy { get; private set; }

    // -------- IAuditable --------
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // -------- ISoftDelete --------
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }

    /// <summary>EF Core ctor — keep parameterless and private.</summary>
    private Specification() { }

    /// <summary>
    /// Factory for a brand-new spec. Validates the slug / name, sets defaults,
    /// and raises <see cref="SpecificationCreated"/>. The caller is responsible
    /// for adding the returned instance to the DbContext and calling
    /// <c>SaveChangesAsync</c>; the interceptor handles event persistence and
    /// dispatch.
    ///
    /// <para><b>Why a factory, not a constructor.</b> The factory returns
    /// nothing other than the instance today, but it's the seam where we'd add
    /// failure-modelling later (e.g. returning <see cref="Result{T}"/>). The
    /// kanban entity stayed CRUD-anemic for the same kind of inputs, but this
    /// slice has a richer state surface (upsert keyed on slug, soft-delete with
    /// re-create) so we lean into the rich-entity pattern from day one.</para>
    /// </summary>
    public static Specification Create(
        Guid projectId,
        string slug,
        string name,
        string content,
        string? createdBy)
    {
        // Validation is deliberately throwing rather than returning Result here
        // — this is a programmer-error path the upsert handler guards against
        // by validating inputs before calling Create. Mirrors the way
        // KanbanCardValidation is called from the handler, not the entity.
        ValidateSlug(slug);
        ValidateName(name);

        var spec = new Specification
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Slug = slug.Trim().ToLowerInvariant(),
            Name = name.Trim(),
            Content = content ?? string.Empty,
            Status = SpecificationStatus.Draft,
            CreatedBy = createdBy,
        };

        spec.RaiseDomainEvent(new SpecificationCreated(spec.Id, spec.ProjectId, spec.Slug));
        return spec;
    }

    /// <summary>
    /// Update the spec's display name and markdown body. Raises
    /// <see cref="SpecificationUpdated"/> on success. Returns
    /// <see cref="Result.Failure(string)"/> with an error code on bad input —
    /// the handler maps that to its own failure response.
    /// </summary>
    public Result UpdateContent(string name, string content)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Failure("invalid_name");
        }
        if (name.Length > MaxNameLength)
        {
            return Result.Failure("invalid_name");
        }

        Name = name.Trim();
        Content = content ?? string.Empty;

        RaiseDomainEvent(new SpecificationUpdated(Id, ProjectId, Slug));
        return Result.Success();
    }

    /// <summary>
    /// Soft-delete the spec. Flips <see cref="IsDeleted"/>; the DbContext
    /// stamps <see cref="DeletedAt"/> / <see cref="DeletedBy"/> via the
    /// <c>ISoftDelete</c> interceptor on SaveChanges. Raises
    /// <see cref="SpecificationDeleted"/> so Card 3 can broadcast the removal.
    ///
    /// <para>Idempotent on the surface — deleting an already-deleted spec is a
    /// no-op in practice because the handler filters on
    /// <see cref="IsDeleted"/> first; if a caller bypasses that filter, the
    /// method still re-raises the event, which we consider acceptable for the
    /// audit trail.</para>
    /// </summary>
    public Result MarkDeleted()
    {
        IsDeleted = true;
        RaiseDomainEvent(new SpecificationDeleted(Id, ProjectId, Slug));
        return Result.Success();
    }

    private static void ValidateSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new ArgumentException("Slug must not be empty.", nameof(slug));
        }
        var normalised = slug.Trim().ToLowerInvariant();
        if (normalised.Length > MaxSlugLength)
        {
            throw new ArgumentException(
                $"Slug must be {MaxSlugLength} characters or fewer.",
                nameof(slug));
        }
        if (!SlugPattern.IsMatch(normalised))
        {
            throw new ArgumentException(
                "Slug must be lowercase letters, digits, and single hyphens (no leading/trailing/consecutive hyphens).",
                nameof(slug));
        }
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name must not be empty.", nameof(name));
        }
        if (name.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Name must be {MaxNameLength} characters or fewer.",
                nameof(name));
        }
    }
}
