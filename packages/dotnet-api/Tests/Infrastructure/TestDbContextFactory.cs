using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Source.Features.RuntimeLifecycle.Events;
using Source.Features.RuntimeTokens.Services;
using Source.Infrastructure;
using Source.Infrastructure.Interceptors;
using Source.Infrastructure.Services.Email;

namespace Api.Tests.Infrastructure;

/// <summary>
/// Factory for creating in-memory <see cref="ApplicationDbContext"/> instances
/// for tests, fully wired with the canonical <see cref="DomainEventInterceptor"/>
/// pipeline. The pre-Phase-D version of this factory built a bare context and
/// every callsite that wanted interceptor-driven domain-event dispatch had to
/// hand-roll a <see cref="ServiceCollection"/> with MediatR + the interceptor +
/// SignalR + a Hangfire mock — eight near-identical copies in
/// <c>Tests/Features/RuntimeLifecycle/</c> alone. Card 4 of the runtime-health
/// spec consolidates that boilerplate here so all tests get the same wired
/// pipeline production uses.
///
/// <para><b>What's wired.</b> Logging (null sink), <see cref="IHttpContextAccessor"/>
/// (so the interceptor's user-id stamping branch is reachable), SignalR core
/// services (<c>IHubContext&lt;...&gt;</c> resolution for broadcast handlers
/// auto-discovered by MediatR), MediatR scanning the api assembly (every
/// <see cref="IEventHandler{T}"/> is registered), a no-op
/// <see cref="IBackgroundJobClient"/> mock (some handlers — e.g.
/// <c>ScheduleRespawnHandler</c> — depend on it), and the
/// <see cref="DomainEventInterceptor"/> attached to the
/// <see cref="ApplicationDbContext"/>.</para>
///
/// <para><b>What's intentionally not wired.</b> The
/// <see cref="ChangeTrackingInterceptor"/> (Layer 2 audit) is omitted because
/// it pollutes <c>StoredEntityChange</c> assertions in tests that diff DB
/// state. Production wires both; tests want the smaller surface unless they
/// opt in. Same rationale for the Postgres-backed
/// <c>ErrorCaptureSaveChangesInterceptor</c> — in-memory provider can't
/// surface the EF errors it watches for.</para>
///
/// <para>The bare-bones <see cref="Create()"/> overload is preserved for the
/// large set of tests that don't care about events firing through MediatR;
/// they all keep working unchanged. New tests (and tests being simplified)
/// should reach for <see cref="CreateWithProvider"/> instead so they get the
/// canonical pipeline for free.</para>
/// </summary>
public static class TestDbContextFactory
{
    /// <summary>
    /// Creates a new <see cref="ApplicationDbContext"/> backed by a unique
    /// in-memory database, with the <see cref="DomainEventInterceptor"/>
    /// pipeline wired. Use this when you only need a context — e.g. pure
    /// query tests. Domain events raised on entities WILL be dispatched
    /// through the registered MediatR handlers (matching production), but
    /// the test does not get a handle on the provider; callers that want to
    /// resolve <see cref="IMediator"/> / <see cref="IPublisher"/> /
    /// <see cref="IBackgroundJobClient"/> directly should call
    /// <see cref="CreateWithProvider"/> instead.
    ///
    /// <para><b>Lifecycle.</b> The returned context owns its
    /// <see cref="ServiceProvider"/> through a lambda capture; disposing the
    /// context disposes the provider. xUnit's <see cref="IDisposable"/> on
    /// <see cref="HandlerTestBase"/> takes care of this for every test that
    /// inherits from the base.</para>
    /// </summary>
    public static ApplicationDbContext Create()
    {
        var harness = BuildHarness(Guid.NewGuid().ToString());
        return harness.Db;
    }

    /// <summary>
    /// Variant of <see cref="Create()"/> that uses the supplied
    /// <paramref name="databaseName"/> instead of a unique GUID. Used by the
    /// rare tests that need to share an in-memory DB across two contexts to
    /// observe a specific concurrency interleaving.
    /// </summary>
    public static ApplicationDbContext Create(string databaseName)
    {
        var harness = BuildHarness(databaseName);
        return harness.Db;
    }

    /// <summary>
    /// Creates a new <see cref="ApplicationDbContext"/> AND returns the
    /// <see cref="ServiceProvider"/> behind it so the test can resolve
    /// <see cref="IMediator"/>, <see cref="IPublisher"/>,
    /// <see cref="IBackgroundJobClient"/>, etc. — useful for handler tests
    /// that want to publish events directly or assert on Hangfire enqueues.
    ///
    /// <para>The returned <see cref="TestHarness"/> implements
    /// <see cref="IDisposable"/>; disposing it tears down both the context
    /// and the provider in the right order.</para>
    /// </summary>
    public static TestHarness CreateWithProvider()
    {
        return BuildHarness(Guid.NewGuid().ToString());
    }

    /// <summary>
    /// Variant of <see cref="CreateWithProvider"/> that uses the supplied
    /// <paramref name="databaseName"/> instead of a unique GUID.
    /// </summary>
    public static TestHarness CreateWithProvider(string databaseName)
    {
        return BuildHarness(databaseName);
    }

    private static TestHarness BuildHarness(string databaseName)
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpContextAccessor();

        // SignalR core services satisfy auto-discovered IEventHandler<T>
        // implementations that depend on IHubContext<...> (e.g.
        // BroadcastRuntimeStateChangedHandler). The hub itself is never
        // invoked in unit tests — there are no connected clients — so this
        // is purely a wire-up to keep DI happy when the dispatcher
        // chain activates a broadcast handler as a side effect of an event.
        services.AddSignalR();

        // MediatR scans the api assembly. Same set as production
        // (MediatRExtensions.AddMediatRServices uses
        // Assembly.GetExecutingAssembly()) — pick any source-side type
        // and the assembly resolver finds the rest.
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(RuntimeStateChanged).Assembly));

        // Several auto-discovered handlers (ScheduleRespawnHandler is the
        // canonical example) take IBackgroundJobClient. A noop Mock is
        // enough — tests that actually want to assert on enqueue calls
        // resolve this same singleton from the provider and configure the
        // mock from there. See ScheduleRespawnHandlerTests for the pattern.
        services.AddSingleton<IBackgroundJobClient>(new Mock<IBackgroundJobClient>().Object);

        // RuntimeTokenSigningKeyCacheInvalidator (auto-discovered as an
        // IEventHandler<SystemSettingChanged>) takes IRuntimeTokenSigningKeyService.
        // A noop Mock keeps DI happy. Tests asserting on cache invalidation
        // resolve this same singleton from the provider; the rest don't care.
        services.AddSingleton<IRuntimeTokenSigningKeyService>(new Mock<IRuntimeTokenSigningKeyService>().Object);

        // SystemSettingsCacheInvalidator (auto-discovered as an
        // IEventHandler<SystemSettingChanged>) takes the concrete
        // SystemSettingsCache. Any SaveChanges that raises SystemSettingChanged —
        // e.g. SecretEncryptionService lazily auto-seeding its master key on the
        // first Encrypt/Decrypt — fans out to this handler through the wired
        // MediatR pipeline, so the cache must be resolvable or activation throws.
        services.AddSingleton<Source.Features.SystemSettings.Services.SystemSettingsCache>();

        // IEmailService — required by auto-discovered email-sending handlers
        // (e.g. SendWorkspaceInviteEmailHandler on WorkspaceInviteCreated, and
        // SendWelcomeEmailHandler on UserCreated). A noop mock keeps DI happy;
        // tests asserting on delivery resolve this same singleton.
        services.AddSingleton<IEmailService>(new Mock<IEmailService>().Object);

        // IConfiguration — some handlers read settings (e.g. the invite-email
        // handler reads App:FrontendBaseUrl). An empty configuration is enough;
        // handlers fall back to relative links when the key is absent.
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddScoped<DomainEventInterceptor>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseInMemoryDatabase(databaseName);
            options.AddInterceptors(sp.GetRequiredService<DomainEventInterceptor>());
        });

        var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<ApplicationDbContext>();
        db.Database.EnsureCreated();

        return new TestHarness(db, provider);
    }

    /// <summary>
    /// Pair of (<see cref="ApplicationDbContext"/>, <see cref="IServiceProvider"/>)
    /// returned from <see cref="CreateWithProvider"/>. Disposing tears down
    /// both in the right order. Implicitly converts to
    /// <see cref="ApplicationDbContext"/> so existing call sites that took
    /// the bare context can adopt this without a signature change.
    /// </summary>
    public sealed class TestHarness : IDisposable
    {
        public ApplicationDbContext Db { get; }
        public ServiceProvider Provider { get; }

        internal TestHarness(ApplicationDbContext db, ServiceProvider provider)
        {
            Db = db;
            Provider = provider;
        }

        public T GetRequiredService<T>() where T : notnull
            => Provider.GetRequiredService<T>();

        public static implicit operator ApplicationDbContext(TestHarness harness)
            => harness.Db;

        public void Dispose()
        {
            // Order matters: dispose the context before the provider so the
            // EF Core scope owning the interceptor is gone before we tear
            // down the provider that holds the singletons it referenced.
            Db.Dispose();
            Provider.Dispose();
        }
    }
}
