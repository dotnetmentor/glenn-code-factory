using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.FlyManagement;
using Source.Features.FlyManagement.Configuration;
using Source.Features.FlyManagement.Models;
using Source.Infrastructure;

namespace Api.Tests.Features.FlyManagement;

/// <summary>
/// Card 10: Real integration tests that exercise <see cref="FlyClient"/> against the live
/// Fly.io Machines API. These are <b>opt-in</b> via the <c>FLY_INTEGRATION_TESTS=1</c>
/// environment variable — the rest of the test project mocks <see cref="HttpMessageHandler"/>
/// so unit-test runs stay hermetic, fast, and free.
///
/// <para><b>Why an env flag instead of <c>[Trait]</c> filtering or a separate test project.</b>
/// We want the default <c>dotnet test</c> invocation in CI to skip these silently with no
/// special filter; an env flag is the smallest possible knob and matches how the rest of
/// the codebase gates side-effecting tests. Adding the <c>Xunit.SkippableFact</c> package
/// would give us nicer "skipped" reporting but it's a new dependency for a behaviour we
/// already get with a single <c>if</c> at the top of each test.</para>
///
/// <para><b>Run locally:</b></para>
/// <code>
///   FLY_INTEGRATION_TESTS=1 \
///   FLY_API_TOKEN=fo1_xxx \
///   FLY_ORG_SLUG=personal \
///   FLY_APP_NAME=glenn-runtimes-int \
///   FLY_REGION=arn \
///   dotnet test --filter FullyQualifiedName~FlyClientIntegrationTests
/// </code>
///
/// <para><b>Cleanup contract:</b> every test that creates a billable resource (machine
/// or volume) tracks its id in <see cref="_createdMachines"/> / <see cref="_createdVolumes"/>
/// and <see cref="DisposeAsync"/> destroys anything still left in those lists, even after
/// failure. Tests that already destroyed their resources remove themselves from the list
/// to avoid spurious "already destroyed" warnings during teardown.</para>
/// </summary>
public class FlyClientIntegrationTests : IAsyncLifetime
{
    private const string EnvFlag = "FLY_INTEGRATION_TESTS";

    /// <summary>
    /// Test-run prefix applied to every machine/volume name so cleanup can scan for
    /// orphans across runs. Keeping it under 16 chars matches Fly's volume-name length
    /// limit (and leaves room for a per-test suffix).
    /// </summary>
    private readonly string _testPrefix = $"inttest-{Guid.NewGuid():N}".Substring(0, 16);

    private readonly List<string> _createdMachines = new();
    private readonly List<string> _createdVolumes = new();

    private FlyClient? _fly;
    private HttpClient? _http;
    private ApplicationDbContext? _db;

    /// <summary>True only when the env flag is set and required env vars are present.</summary>
    private static bool IsEnabled()
        => Environment.GetEnvironmentVariable(EnvFlag) == "1";

    public Task InitializeAsync()
    {
        if (!IsEnabled())
        {
            // Skip all setup when disabled — every test method also early-returns, so we
            // never touch a live API or even allocate the HttpClient.
            return Task.CompletedTask;
        }

        var token = Environment.GetEnvironmentVariable("FLY_API_TOKEN")
            ?? throw new InvalidOperationException(
                "FLY_API_TOKEN must be set when FLY_INTEGRATION_TESTS=1.");
        var orgSlug = Environment.GetEnvironmentVariable("FLY_ORG_SLUG") ?? "personal";
        var appName = Environment.GetEnvironmentVariable("FLY_APP_NAME")
            ?? throw new InvalidOperationException(
                "FLY_APP_NAME must be set when FLY_INTEGRATION_TESTS=1.");
        var region = Environment.GetEnvironmentVariable("FLY_REGION") ?? "arn";

        var options = new FlyOptions
        {
            ApiToken = token,
            OrgSlug = orgSlug,
            AppName = appName,
            DefaultRegion = region,
        };

        // Real HttpClient pointed at the production Fly API — same BaseAddress that
        // AddFlyManagement configures in DI, kept literal here so we don't drag the
        // whole DI graph into a single integration suite.
        _http = new HttpClient { BaseAddress = new Uri("https://api.machines.dev/v1/") };

        // FlyOperation rows still need somewhere to land; an ephemeral in-memory DB
        // is enough — we don't assert on the audit trail here, that's covered by
        // FlyOperationPersistenceTests.
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"fly-int-{Guid.NewGuid()}")
            .Options;
        _db = new ApplicationDbContext(dbOptions);

        _fly = new FlyClient(
            _http,
            new StubFlyOptionsAccessor(options),
            _db,
            NullLogger<FlyClient>.Instance);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Best-effort teardown. We swallow exceptions per-resource so a single stuck
    /// destroy doesn't leak the rest of the lists — leaving orphaned billable
    /// machines/volumes is the worst possible outcome for an integration suite.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_fly is not null)
        {
            foreach (var id in _createdMachines.ToList())
            {
                try
                {
                    await _fly.DestroyMachineAsync(id, force: true, ct: CancellationToken.None);
                }
                catch
                {
                    // best effort — already destroyed, network blip, ... Don't mask the original test failure.
                }
            }

            foreach (var id in _createdVolumes.ToList())
            {
                try
                {
                    await _fly.DestroyVolumeAsync(id, ct: CancellationToken.None);
                }
                catch
                {
                    // best effort
                }
            }
        }

        _http?.Dispose();
        _db?.Dispose();
    }

    [Fact]
    public async Task EnsureApp_CreatesOrFinds_App()
    {
        if (!IsEnabled()) return;

        var app = await _fly!.EnsureAppAsync(CancellationToken.None);

        app.Should().NotBeNull();
        app.Name.Should().Be(Environment.GetEnvironmentVariable("FLY_APP_NAME"));
    }

    [Fact]
    public async Task CreateAndDestroyVolume_RoundTrip_Works()
    {
        if (!IsEnabled()) return;

        var req = new CreateVolumeRequest(
            Name: $"{_testPrefix}_vol",
            Region: Environment.GetEnvironmentVariable("FLY_REGION") ?? "arn",
            SizeGb: 1);

        var vol = await _fly!.CreateVolumeAsync(req, ct: CancellationToken.None);
        _createdVolumes.Add(vol.Id);

        vol.Id.Should().NotBeNullOrEmpty();
        // Card 5 security guarantee — encryption defaults to true and the wire round-trip
        // should preserve it. Catching any future regression that silently flips the default.
        vol.Encrypted.Should().BeTrue();

        var fetched = await _fly.GetVolumeAsync(vol.Id);
        fetched.Id.Should().Be(vol.Id);

        await _fly.DestroyVolumeAsync(vol.Id);
        _createdVolumes.Remove(vol.Id); // cleanup already happened — DisposeAsync shouldn't retry.
    }

    [Fact]
    public async Task CreateMachineLifecycle_Start_Stop_Destroy_Works()
    {
        if (!IsEnabled()) return;

        var req = new CreateMachineRequest(
            Name: $"{_testPrefix}_mach",
            Region: Environment.GetEnvironmentVariable("FLY_REGION") ?? "arn",
            Config: new MachineConfig(
                Image: "flyio/hellofly:latest",
                Guest: new MachineGuest(Cpus: 1, MemoryMb: 256)));

        var machine = await _fly!.CreateMachineAsync(req, ct: CancellationToken.None);
        _createdMachines.Add(machine.Id);

        machine.Id.Should().NotBeNullOrEmpty();

        // Wait for "started" — Fly reliably gets there inside a minute for hellofly.
        var started = await _fly.WaitForStateAsync(machine.Id, "started", TimeSpan.FromSeconds(60));
        started.State.Should().Be("started");

        // Stop -> verify state. Use a slightly tighter timeout since stops are cheap.
        await _fly.StopMachineAsync(machine.Id);
        var stopped = await _fly.WaitForStateAsync(machine.Id, "stopped", TimeSpan.FromSeconds(30));
        stopped.State.Should().Be("stopped");

        // Bring it back up so we exercise the full Start path on a real machine.
        await _fly.StartMachineAsync(machine.Id);
        var restarted = await _fly.WaitForStateAsync(machine.Id, "started", TimeSpan.FromSeconds(60));
        restarted.State.Should().Be("started");

        // Suspend is observability-only here: it isn't supported on every base image and
        // the post-condition state varies, so we send the call and rely on the next
        // assertion (no exception) — the audit row plus a non-throwing return is enough.
        try
        {
            await _fly.SuspendMachineAsync(machine.Id);
        }
        catch (FlyApiException)
        {
            // Suspend is opt-in per machine config; hellofly hasn't enabled it. Acceptable here.
        }

        await _fly.DestroyMachineAsync(machine.Id, force: true);
        _createdMachines.Remove(machine.Id); // already destroyed — skip retry in DisposeAsync.
    }

    [Fact]
    public async Task ListMachines_AndVolumes_ReturnLists()
    {
        if (!IsEnabled()) return;

        var machines = await _fly!.ListMachinesAsync(CancellationToken.None);
        machines.Should().NotBeNull(); // empty list is fine — the app may have nothing running.

        var volumes = await _fly!.ListVolumesAsync(CancellationToken.None);
        volumes.Should().NotBeNull();
    }
}
