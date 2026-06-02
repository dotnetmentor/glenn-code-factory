using Microsoft.EntityFrameworkCore;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Models;
using Source.Features.RuntimeLifecycle.Drift;
using Source.Features.RuntimeLifecycle.Models;
using Source.Infrastructure;

namespace Source.Features.RuntimeLifecycle.Provisioning;

/// <summary>
/// Shared Fly provisioning helpers for runtime machine/volume naming, idempotent
/// machine create, and operator-facing error copy.
/// </summary>
public static class RuntimeFlyProvisioning
{
    public static string BuildMachineName(Guid runtimeId) =>
        $"{RuntimeDriftQueryService.ProjectRuntimeMachineNamePrefix}{runtimeId:N}"[..30];

    public static string BuildVolumeName(Guid runtimeId) =>
        $"vol_{runtimeId:N}"[..30];

    /// <summary>
    /// Human-readable failure text for UI surfaces (<see cref="RuntimeStatusResponse.ErrorMessage"/>).
    /// Raw Fly bodies stay in logs and RuntimeStateEvent audit rows.
    /// </summary>
    public static string FormatUserMessage(FlyApiException ex)
    {
        var code = ex.ErrorCode ?? string.Empty;

        if (code.Contains("already_exists", StringComparison.OrdinalIgnoreCase)
            && code.Contains("machine name", StringComparison.OrdinalIgnoreCase))
        {
            return
                "A Fly machine for this runtime already exists from a previous provisioning attempt. "
                + "Use Project Settings → Runtimes to respawn, or ask a Super Admin to remove the orphan in Fly Cleanup.";
        }

        if (code.Contains("already_exists", StringComparison.OrdinalIgnoreCase))
        {
            return
                "Fly reported a name collision while provisioning this runtime. "
                + "Retry from Project Settings → Runtimes, or ask a Super Admin to check Fly Cleanup.";
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            return $"Fly rejected the runtime request ({code}). Check Super Admin → Runtime Monitor for details.";
        }

        return "Fly rejected the runtime request. Check Super Admin → Runtime Monitor for details.";
    }

    /// <summary>
    /// Create a machine, or adopt an existing Fly machine with the same deterministic
    /// name when a prior attempt created the machine but failed before persisting state.
    /// </summary>
    public static async Task<FlyMachine> CreateOrAdoptMachineAsync(
        FlyClient fly,
        ApplicationDbContext db,
        ProjectRuntime runtime,
        CreateMachineRequest request,
        CancellationToken ct)
    {
        var idempotencyKey = $"create-machine:{runtime.Id:D}";
        try
        {
            return await fly.CreateMachineAsync(
                request,
                idempotencyKey: idempotencyKey,
                runtimeId: runtime.Id,
                ct: ct);
        }
        catch (FlyApiException ex) when (IsMachineNameAlreadyExists(ex))
        {
            var adopted = await TryAdoptMachineByNameAsync(fly, db, runtime, request.Name, ct);
            if (adopted is not null)
            {
                return adopted;
            }

            throw;
        }
    }

    public static async Task<FlyMachine?> TryAdoptMachineByNameAsync(
        FlyClient fly,
        ApplicationDbContext db,
        ProjectRuntime runtime,
        string machineName,
        CancellationToken ct)
    {
        var machines = await fly.ListMachinesAsync(ct);
        var match = machines.FirstOrDefault(m =>
            string.Equals(m.Name, machineName, StringComparison.Ordinal));

        if (match is null)
        {
            return null;
        }

        var ownedByOther = await db.ProjectRuntimes
            .AnyAsync(r => r.FlyMachineId == match.Id && r.Id != runtime.Id, ct);

        return ownedByOther ? null : match;
    }

    private static bool IsMachineNameAlreadyExists(FlyApiException ex) =>
        ex.ErrorCode?.Contains("already_exists", StringComparison.OrdinalIgnoreCase) == true
        && ex.ErrorCode.Contains("machine name", StringComparison.OrdinalIgnoreCase);
}
