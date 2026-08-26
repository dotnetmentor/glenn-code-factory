namespace Source.Features.RuntimeLifecycle.BoxSnapshot;

/// <summary>
/// The "what Box says" half of the <see cref="BoxSnapshotResponse"/>. A trimmed
/// projection of <see cref="Source.Features.BoxManagement.Models.BoxVm"/> so the
/// frontend doesn't need to know the upstream's full schema.
/// </summary>
public sealed class BoxVmView
{
    /// <summary>Box id.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Free-form name carried on the box. For project runtimes this is the
    /// <c>"rt-{guid}"</c> pattern produced by the provisioner; for the golden
    /// templates and other machinery it varies.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Live box lifecycle state (wire field <c>state</c>; enum: <c>init</c>,
    /// <c>provisioning</c>, <c>provisioned</c>, <c>cloning</c>, <c>ready</c>,
    /// <c>idle</c>, <c>running</c>, <c>archiving</c>, <c>archived</c>,
    /// <c>error</c>). Stringly-typed to match the upstream — Box may add new
    /// states. Kept as <c>Status</c> here for frontend compatibility.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>Box machine type (<c>small</c> / <c>default</c> / <c>large</c>), when reported.</summary>
    public string? Size { get; init; }

    /// <summary>Box region the VM lives in (EU: de/fi/fr), when reported.</summary>
    public string? Region { get; init; }

    /// <summary>
    /// Remaining TTL in seconds, when reported. Operators read this as "how long
    /// until the orphan guardrail archives this box if the extender stops running".
    /// </summary>
    public long? TtlSeconds { get; init; }

    /// <summary>UTC timestamp Box recorded for creation, when reported.</summary>
    public DateTime? CreatedAt { get; init; }
}
