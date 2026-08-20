namespace Source.Features.BoxManagement;

/// <summary>
/// Maps a runtime's requested hardware spec (the provider-neutral cpu/mem columns
/// snapshotted on <c>ProjectRuntime</c>) onto Box's fixed size tiers:
///
/// <list type="bullet">
///   <item><c>small</c> — 2 vCPU / 4 GB (0.5× rate)</item>
///   <item><c>default</c> — 4 vCPU / 8 GB (1× rate)</item>
///   <item><c>large</c> — 8 vCPU / 16 GB (2× rate)</item>
/// </list>
///
/// The mapping rounds UP: a request must never land on a box smaller than asked
/// for (the 2 GiB OOM class of bug taught us that lesson). Even the smallest tier
/// (2 vCPU / 4 GB) comfortably clears the old 1 vCPU / 2 GB Fly default.
/// </summary>
public static class BoxSizeMapper
{
    public const string Small = "small";
    public const string Default = "default";
    public const string Large = "large";

    public static string FromSpec(int cpus, int memoryMb)
    {
        if (cpus <= 2 && memoryMb <= 4096) return Small;
        if (cpus <= 4 && memoryMb <= 8192) return Default;
        return Large;
    }
}
