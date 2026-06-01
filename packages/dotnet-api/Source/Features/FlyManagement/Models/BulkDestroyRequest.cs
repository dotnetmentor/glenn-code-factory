namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Body of <c>POST /api/admin/fly/machines/bulk-destroy</c> and
/// <c>POST /api/admin/fly/volumes/bulk-destroy</c>. The super-admin Fly cleanup
/// page builds these by check-boxing rows from the corresponding list endpoint
/// and sending the resource ids back as a single batch.
///
/// <para><b>Hard cap.</b> The controller refuses bodies with more than 100 ids —
/// safety net for a UI typo or runaway "select all" on a 10k-machine app. 100 is
/// also comfortably below the per-request DB and HTTP budgets given the
/// <c>SemaphoreSlim(5)</c> concurrency gate inside the handler.</para>
///
/// <para><b>Force flag.</b> Passes straight through to
/// <see cref="FlyClient.DestroyMachineAsync"/> for machines (irrelevant on volumes —
/// <see cref="FlyClient.DestroyVolumeAsync"/> doesn't take one — and is ignored
/// there). When <c>true</c>, Fly skips the graceful stop on stuck VMs.</para>
/// </summary>
public record BulkDestroyRequest(
    List<string> Ids,
    bool Force = false);
