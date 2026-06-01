namespace Source.Features.RuntimeBootstrap.Models;

/// <summary>
/// Coarse-grained lifecycle stages the bootstrap daemon walks through when
/// preparing a freshly-created runtime machine. Reported back via heartbeat
/// and persisted on <see cref="BootstrapRun.FinalStage"/> so we can answer
/// "where did the boot get stuck?" without parsing logs.
///
/// <para>Stored as a string in the database (see <c>ApplicationDbContext</c>
/// configuration) so adding new stages later doesn't break existing rows.</para>
/// </summary>
public enum BootstrapStage
{
    /// <summary>Daemon has started and is opening its control-plane connection.</summary>
    Connecting,

    /// <summary>Pulling the bootstrap payload (config + secrets) from the API.</summary>
    Fetching,

    /// <summary>Writing the materialised config to the runtime filesystem.</summary>
    WritingConfig,

    /// <summary>Installing language runtimes / toolchains declared in the payload.</summary>
    InstallingRuntimes,

    /// <summary>Cloning the project's git repository into the workspace.</summary>
    CloningRepo,

    /// <summary>Running the project's setup / install hook (e.g. <c>npm install</c>).</summary>
    RunningSetup,

    /// <summary>Bringing user-defined long-running services up.</summary>
    StartingServices,

    /// <summary>Everything is up; runtime is serving traffic.</summary>
    Ready,
}
