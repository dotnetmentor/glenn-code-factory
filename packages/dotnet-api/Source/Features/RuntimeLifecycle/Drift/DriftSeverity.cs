using Tapper;

namespace Source.Features.RuntimeLifecycle.Drift;

/// <summary>
/// Drift severity for a single <see cref="RuntimeDriftDto"/>. Values are
/// explicitly assigned so a numeric sort descending puts the most urgent rows
/// at the top — the operator UI relies on that ordering.
///
/// <para><see cref="TranspilationSourceAttribute"/> exposes the enum to the
/// shared TS generator so the frontend gets a clean string union instead of
/// a numeric one.</para>
/// </summary>
[TranspilationSource]
public enum DriftSeverity
{
    /// <summary>DB and Fly agree. No rules matched.</summary>
    Ok = 0,

    /// <summary>Informational drift, no operator action required.</summary>
    Low = 1,

    /// <summary>Worth a look on the next pass but not urgent.</summary>
    Medium = 2,

    /// <summary>Real divergence that the reconciler can't auto-fix.</summary>
    High = 3,

    /// <summary>Machine lost or unaccounted-for resource — needs immediate attention.</summary>
    Critical = 4,
}
