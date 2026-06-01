namespace Source.Features.RuntimeLifecycle.Models;

/// <summary>
/// Health of a runtime's <i>spec application</i> — deliberately decoupled from the
/// lifecycle <see cref="RuntimeState"/>. A runtime can be <see cref="RuntimeState.Online"/>
/// (alive, daemon heartbeating) while its spec only partially applied: a bad install
/// script, a service that never binds, a missing env var. That runtime is
/// <see cref="Degraded"/> — alive but not fully provisioned — and the self-healing
/// loop (agent reads the boot issues, proposes a corrected spec, auto-applies) flips
/// it back to <see cref="Healthy"/> without a reboot.
///
/// <para><b>Persistence.</b> Stored as <c>varchar(16)</c> via
/// <c>HasConversion&lt;string&gt;()</c> with a DB default of <c>'Unknown'</c> — same
/// readability-over-compactness convention as <see cref="RuntimeState"/>. The string
/// form survives adding new members later without an ordinal value-shift on existing
/// rows.</para>
///
/// <list type="bullet">
///   <item><see cref="Unknown"/> — no spec-health report has landed yet (fresh row,
///         or a runtime booted before this feature existed). The default.</item>
///   <item><see cref="Healthy"/> — the daemon applied the spec with zero boot issues.</item>
///   <item><see cref="Degraded"/> — the runtime reached Online but one or more spec
///         stages failed; boot-issue details live in <c>RuntimeEvents</c> /
///         <c>RuntimeErrorReports</c>, never on this row.</item>
/// </list>
/// </summary>
public enum RuntimeSpecHealth
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
}
