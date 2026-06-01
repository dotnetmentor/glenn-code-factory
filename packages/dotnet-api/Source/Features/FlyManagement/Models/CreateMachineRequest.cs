namespace Source.Features.FlyManagement.Models;

/// <summary>
/// Body of <c>POST /v1/apps/{app}/machines</c>. Fly's API uses snake_case JSON, so the
/// FlyClient serialises these records with <c>JsonNamingPolicy.SnakeCaseLower</c> rather
/// than annotating every property — matches how the AnalyticsChat WebSearchTool talks
/// to its snake_case upstream.
/// </summary>
public record CreateMachineRequest(
    string Name,
    string Region,
    MachineConfig Config);

/// <summary>
/// Per-machine config: the OCI image to run, optional environment, exposed services,
/// volume mounts, and guest sizing. Mirrors the subset of Fly's <c>config</c> object we
/// actually use today; new fields can be added without a breaking change.
/// </summary>
public record MachineConfig(
    string Image,
    Dictionary<string, string>? Env = null,
    List<MachineService>? Services = null,
    List<MachineMount>? Mounts = null,
    MachineGuest? Guest = null);

/// <summary>
/// One service exposed by a machine — protocol (<c>tcp</c>/<c>udp</c>), the port the
/// process listens on inside the VM, and the public ports + handlers Fly should map to it.
/// </summary>
public record MachineService(string Protocol, int InternalPort, List<MachinePort> Ports);

/// <summary>
/// A single externally-visible port plus the chain of edge handlers Fly should apply
/// (e.g. <c>["tls", "http"]</c> for terminated HTTPS).
/// </summary>
public record MachinePort(int Port, List<string>? Handlers = null);

/// <summary>Bind a previously-created Fly volume to a path inside the machine.</summary>
public record MachineMount(string Volume, string Path);

/// <summary>
/// Guest sizing. Defaults to shared-cpu-1x / 2048 MiB — the Claude Code CLI binary
/// is a ~230 MB Bun-compiled single file, and the daemon's self-check spawns it
/// against a 5 s timeout; on 256 MiB the binary couldn't even page in and the
/// self-check hung silently with no stdout, leaving runtimes stuck Bootstrapping.
/// 2 GiB is the smallest size that comfortably loads the CLI plus the node daemon.
///
/// <para><b>CpuKind is a string</b> on the wire (Fly rejects numeric values:
/// <c>cannot unmarshal number into Go struct field MachineGuest.config.guest.cpu_kind of type string</c>).
/// Valid values: <c>"shared"</c> (default), <c>"performance"</c>.</para>
///
/// <para><b>PersistRootfs = "always"</b> is the linchpin of our "things I install stay
/// installed" model. Fly machines run on an overlayfs: read-only image layer + writable
/// upper layer (the rootfs). By default that upper layer is <i>ephemeral</i> — wiped on
/// <c>fly machine update</c>, scale-to-zero wake, and host migration. Our daemon's
/// InstallStage runs apt-install snippets from RuntimeSpecV2 (mongodb, mariadb, redis, …);
/// without persistence, those binaries land on the ephemeral overlay and disappear at
/// the next restart, while the install-hash store on <c>/data</c> survives and says
/// "already installed" — a contract violation that leaves supervisord trying to exec
/// missing binaries (FATAL loop).
/// </para>
///
/// <para>Fly shipped <c>persist_rootfs</c> in 2025 (community.fly.io/t/26146) with three
/// values: <c>"never"</c> (legacy default, ephemeral), <c>"restart"</c> (survives reboot
/// only), <c>"always"</c> (survives reboot + machine update + scale-to-zero wake). We
/// pick <c>"always"</c> so the user's mental model holds and cold-boot is instant.
/// Trade-off: $0.15/GB-month for the stopped rootfs (negligible vs daemon dev time
/// lost debugging "why is my service gone"). NOT durable against host maintenance —
/// keep all install scripts idempotent so a wiped overlay is recoverable by re-running
/// the spec, which is already part of the bootstrap flow.</para>
/// </summary>
public record MachineGuest(
    string CpuKind = "shared",
    int Cpus = 1,
    int MemoryMb = 2048,
    string PersistRootfs = "always");
