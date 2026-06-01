using Hangfire;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Source.Features.Cloudflare.Extensions;
using Source.Features.FlyManagement.Extensions;
using Source.Features.GitHub.Extensions;
using Source.Features.RuntimeImages.Extensions;
using Source.Features.RuntimeTokens.Bootstrap;
using Source.Features.RuntimeTokens.Services;
using Source.Features.SignalR.Hubs;
using Source.Features.SystemSettings.Extensions;
using Source.Features.Workspaces.Extensions;
using Source.Infrastructure;
using Source.Infrastructure.Bootstrap;
using Source.Infrastructure.ErrorHandling;
using Source.Infrastructure.Extensions;
using Source.Infrastructure.Logging;
using Source.Infrastructure.Workspaces;
using Source.Shared;

var builder = WebApplication.CreateBuilder(args);

// Last-line-of-defence log scrubber: wraps every ILoggerProvider already
// registered by WebApplicationBuilder (Console, Debug, EventSource, …) so any
// JWT-shaped or PEM-private-key-shaped substring in a formatted log message is
// redacted before it reaches a sink. Snapshots the current provider descriptors
// at this call site, so any further builder.Logging.AddX() additions below this
// line would not be wrapped — the project doesn't add more, but keep that in
// mind if you do.
builder.Services.AddJwtRedactingLogging();

builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.AllowSynchronousIO = true;
    serverOptions.ConfigureEndpointDefaults(listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
    });
});

// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
// Check if running in swagger generation mode (skip DB-dependent services)
var isSwaggerGeneration = Environment.GetEnvironmentVariable("SWAGGER_GENERATION_MODE") == "true";

builder.Services.AddDatabaseServices(builder.Configuration);
builder.Services.AddIdentityServices();
builder.Services.AddMediatRServices();

// Clock abstraction — overridable in tests via WithService<IClock>(fakeClock)
builder.Services.AddSingleton<IClock, SystemClock>();

// Process-local rolling buffer for daemon health telemetry (Phase D Card 1).
// Singleton because the heartbeat handler appends from concurrent SignalR
// invocations and the read endpoint reads from concurrent HTTP requests; the
// buffer's own ConcurrentDictionary handles both.
builder.Services.AddSingleton<Source.Features.Health.HealthSnapshotBuffer>();
// Stateful service-down detector (Phase D Card 2) — tracks outage windows
// across heartbeats, dedupes RuntimeServiceDown events. Singleton lifetime is
// load-bearing: per-request lifetime would forget the outage window every
// 5 seconds and burst-fire events.
builder.Services.AddSingleton<Source.Features.Health.Services.ServiceDownDetector>();
// Throttle ledger for RestartService dispatches (Phase D Card 2). Singleton
// for the same reason as ServiceDownDetector — the ledger must outlive a
// single heartbeat to enforce the 3-per-5-min cap.
builder.Services.AddSingleton<Source.Features.Health.Services.RestartServiceThrottle>();

if (!isSwaggerGeneration)
{
    builder.Services.AddHangfireServices(builder.Configuration);
}
else
{
    // Swagger generation skips Hangfire — but MediatR auto-discovers handlers
    // (e.g. ScheduleRespawnHandler) that take IBackgroundJobClient in their ctor.
    // Register a no-op so DI validation succeeds; swagger gen never actually enqueues.
    builder.Services.AddNoOpBackgroundJobClient();
}

builder.Services.AddAuthenticationServices(builder.Configuration);
// Additive: registers the "RuntimeToken" JWT scheme used by daemon-facing endpoints.
// MUST run AFTER AddAuthenticationServices so the user-auth scheme keeps the default-scheme slot.
builder.Services.AddRuntimeTokenAuthScheme();
// Authorization policy used by RuntimeHub mapping below. Pinning the policy to
// the RuntimeToken scheme keeps the user-JWT default scheme out of the picture
// for daemon connections — and keeps AgentHub on the user scheme untouched.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(RuntimeTokenAuthenticationDefaults.SchemeName + "Policy", p =>
        p.AddAuthenticationSchemes(RuntimeTokenAuthenticationDefaults.SchemeName)
         .RequireAuthenticatedUser());
});
builder.Services.AddWorkspaceAuthorization();
builder.Services.AddWorkspacesFeature();
builder.Services.AddGithubFeature(builder.Configuration);
builder.Services.AddFlyManagement();
builder.Services.AddCloudflareFeature();
builder.Services.AddRuntimeImagesFeature();
builder.Services.AddSystemSettingsFeature(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<IRuntimeTokenSigningKeyService, RuntimeTokenSigningKeyService>();
// Singleton: caches the master key after the first read; per-project DEKs are
// not cached (re-unwrapped per call to keep plaintext-DEK lifetime tight).
// Uses IServiceScopeFactory to access the scoped DbContext + SystemSettings service.
builder.Services.AddSingleton<Source.Features.ProjectSecrets.Services.SecretEncryptionService>();
// Scoped: shares the request DbContext. Resolves a project's current daemon-bound
// V2 expanded spec from the most-recent terminal proposal — used by the approval
// flow (delta diff) and the branch env-status query (missing-required calc).
builder.Services.AddScoped<
    Source.Features.RuntimeCuration.Services.ICurrentExpandedSpecResolver,
    Source.Features.RuntimeCuration.Services.CurrentExpandedSpecResolver>();
// Singleton: in-memory token-bucket store keyed by (runtimeId, server, method).
// Single-instance only — multi-instance scaling needs Redis. See class-level
// XML doc on McpRateLimiter for the rationale.
builder.Services.AddSingleton<Source.Features.Mcp.Framework.McpRateLimiter>();
// IMemoryCache is already registered (e.g. by AddGithubFeature) — calling
// AddMemoryCache() again is idempotent. Singleton: process-local hot-path cache
// for revoked jtis. WarmFromDatabaseAsync runs in RevocationCacheWarmupService.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IRevocationCache, RevocationCache>();
// Singleton: process-local accumulator for RuntimeToken validate-path usage
// metrics. The validate hot path bumps an in-memory counter; the
// RuntimeTokenUsageFlushJob drains it to RuntimeTokenIssues.LastUsedAt /
// RequestCount every 30 s. Singleton lifetime is load-bearing — a per-request
// recorder would defeat the batching entirely (and skew the multi-instance
// LastUsedAt = max(...) merge). Mirrors HealthSnapshotBuffer / ServiceDownDetector.
builder.Services.AddSingleton<Source.Features.RuntimeTokens.Services.RuntimeTokenUsageRecorder>();
if (!isSwaggerGeneration)
{
    builder.Services.AddHostedService<RevocationCacheWarmupService>();
}
// Scoped: depends on ApplicationDbContext (scoped). Mints + audits in one SaveChanges
// per call; the singleton signing-key service supplies the HMAC material.
builder.Services.AddScoped<IRuntimeTokenService, RuntimeTokenService>();
// Scoped: shared "create + dispatch a turn" path used by both AgentHub.SubmitPrompt
// (user-typed prompts) and RuntimeHub.RequestSelfHealContinuation (daemon-driven
// retry after an afterPrompt hook fails). Single source of truth for the
// session-create + audit-event + counter-bump + StartTurn dispatch order.
builder.Services.AddScoped<Source.Features.Conversations.Services.ITurnDispatcher,
    Source.Features.Conversations.Services.TurnDispatcher>();
// Scoped: BYOK pre-flight for AgentHub.SubmitPrompt. Touches the scoped
// DbContext + the singleton SecretEncryptionService and mirrors the resolution
// chain RuntimeHub.GetSecrets uses on the daemon side, so both code paths
// agree on what "configured" means before we dispatch a turn.
builder.Services.AddScoped<
    Source.Features.SignalR.Services.IAgentSecretsResolver,
    Source.Features.SignalR.Services.AgentSecretsResolver>();
// Scoped: resolves the effective AgentPermissionsConfig for a project per turn.
// Reads either the ProjectAgentPermissions override row (if present) or the
// AgentPermissions system-catalog defaults — never both. See the resolver's
// class doc for the no-merging invariant.
builder.Services.AddScoped<
    Source.Features.AgentPermissions.Services.IAgentPermissionsResolver,
    Source.Features.AgentPermissions.Services.AgentPermissionsResolver>();
builder.Services.AddOfflineFirstServices(builder.Configuration);
builder.Services.AddRealTimeServices();

builder.Services.AddRateLimitingServices();
builder.Services.AddTelemetryServices(builder.Configuration);
builder.Services.AddSwaggerServices();

// Error capture pipeline
builder.Services.Configure<ErrorCaptureOptions>(
    builder.Configuration.GetSection(ErrorCaptureOptions.SectionName));

// Runtime daemon-stamping options (PublicApiUrl daemons dial back at).
// The IOptions<RuntimeOptions> binding is kept for any legacy/test code paths,
// but production reads go through IRuntimeOptionsAccessor which is backed by
// SystemSettings — so the value can be live-edited from the admin UI without
// a process restart. The seeder copies the appsettings value into SystemSettings
// on first boot of an environment.
builder.Services.Configure<Source.Features.RuntimeLifecycle.Configuration.RuntimeOptions>(
    builder.Configuration.GetSection(Source.Features.RuntimeLifecycle.Configuration.RuntimeOptions.SectionName));
builder.Services.AddScoped<
    Source.Features.RuntimeLifecycle.Configuration.IRuntimeOptionsAccessor,
    Source.Features.RuntimeLifecycle.Configuration.RuntimeOptionsAccessor>();

// Scoped: snapshot service powering GET /api/admin/runtimes/drift. Touches the
// scoped DbContext + FlyClient and is pure-function on the read path, so scoped
// lifetime matches the surrounding request-bound dependencies exactly.
builder.Services.AddScoped<
    Source.Features.RuntimeLifecycle.Drift.IRuntimeDriftQueryService,
    Source.Features.RuntimeLifecycle.Drift.RuntimeDriftQueryService>();

// Scoped: per-runtime snapshot service powering
// GET /api/admin/runtimes/{runtimeId}/fly-snapshot. Same lifetime reasoning as the
// drift query service above — scoped DbContext + FlyClient, request-bound read path.
builder.Services.AddScoped<
    Source.Features.RuntimeLifecycle.FlySnapshot.IRuntimeFlySnapshotService,
    Source.Features.RuntimeLifecycle.FlySnapshot.RuntimeFlySnapshotService>();

// Scoped: shared wake-window resolver consumed by every handler in
// Source/Features/RuntimeWakeObservability/. Touches only the scoped
// ApplicationDbContext on a read path — matches the drift / fly-snapshot
// services above. Used by the three /api/admin/runtime-wake-observability/...
// queries (summary, stage-breakdown, slow-sessions).
builder.Services.AddScoped<Source.Features.RuntimeWakeObservability.Internal.WakeWindowResolver>();

// Scoped: runtime preset expander. Turns V3 (preset + values) specs into the
// V2 wire format the daemon already speaks — see RuntimeSpecV3 / PresetExpander
// docs for the why. Scoped because it reads from the request-bound
// ApplicationDbContext; pure read path, no writes.
builder.Services.AddScoped<
    Source.Features.RuntimePresets.Services.IPresetExpander,
    Source.Features.RuntimePresets.Services.PresetExpander>();

// Singleton: mise version lookup for the admin "Lookup versions" affordance
// on PresetParameter.MiseTool. v1 is a hand-curated static dictionary, so a
// singleton has zero per-request cost; a future revision can swap in a
// periodic refresh or live shell-out without touching call sites.
builder.Services.AddSingleton<
    Source.Features.RuntimePresets.Services.IMiseVersionLookup,
    Source.Features.RuntimePresets.Services.MiseVersionLookup>();

builder.Services.AddSingleton<IPiiRedactor, PiiRedactor>();
builder.Services.AddSingleton<IErrorSignatureHasher, ErrorSignatureHasher>();
builder.Services.AddSingleton<ErrorQueue>();
builder.Services.AddSingleton<ErrorPersistenceWorkerOptions>();
if (!isSwaggerGeneration)
{
    builder.Services.AddHostedService<ErrorPersistenceWorker>();
    builder.Services.AddHostedService<ErrorPipelineSummaryReporter>();

    // One-shot backfill so users that pre-date P1.2 (the auto-workspace-on-signup change)
    // get a primary workspace + WorkspaceUser role on next startup. Idempotent — safe to
    // leave registered indefinitely; subsequent startups find no eligible users and exit.
    builder.Services.AddHostedService<ExistingUserWorkspaceBackfill>();

    // ServicePresets (Runtime Spec V3) are seeded by the AddServicePresetsV3
    // migration as raw INSERTs — no startup hosted service. Admin edits to
    // cloned (non-IsBuiltIn) rows are preserved across deploys because the
    // migration is one-shot, not idempotent-on-every-boot.
}

// Register dev seed services (only needed in Development)
if (builder.Environment.IsDevelopment() && !isSwaggerGeneration)
{
    builder.Services.AddDevSeedServices();
}

var app = builder.Build();

// Auto-migrate database and seed data
// Skip when generating swagger to avoid database dependency
// Skip in the Testing environment — integration tests use EF Core InMemory provider,
// which does not support migrations or seeding that assumes Postgres behavior.
var isTestingEnv = app.Environment.IsEnvironment("Testing");
if (!isSwaggerGeneration && !isTestingEnv)
{
    await app.MigrateDatabase();

    // Seed roles in all environments (required for authorization)
    await app.SeedRoles();

    // Ensure the bootstrap SuperAdmin exists (all environments). Runs after role seeding
    // so the SuperAdmin role is present; ExistingUserWorkspaceBackfill then grants the
    // workspace + WorkspaceUser role for full access. Self-registration is closed, so this
    // is the only login path on a fresh deployment.
    await app.SeedSuperAdmin();
}

if (app.Environment.IsDevelopment() && !isSwaggerGeneration)
{
    await app.SeedDevelopmentData();
}

// Configure the HTTP request pipeline
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Global exception handler - must be early in the pipeline to catch all errors
app.UseMiddleware<GlobalExceptionMiddleware>();

app.UseSwaggerInDevelopment();

// Enforce the 8 KB hard cap on POST /api/errors/report BEFORE rate-limiting, so
// oversize requests get a clean 413 and never consume a rate-limit permit.
app.UseMiddleware<ErrorReportSizeLimitMiddleware>();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (!isSwaggerGeneration)
{
    app.UseHangfire(app.Environment);
}

// Serve static files (frontend build in wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();

// Map Controllers
app.MapControllers();

// SignalR hubs — see Source/Features/SignalR for AgentHub (React clients,
// JWT-authed via the default user scheme) and RuntimeHub (daemons, RuntimeToken
// JWT scheme — header on HTTP transports, ?access_token=... on WebSocket).
app.MapHub<AgentHub>("/hubs/agent");
// Belt-and-braces: RuntimeHub also has a class-level [Authorize] attribute
// pinning the same scheme. Endpoint policy + attribute both enforce it.
app.MapHub<RuntimeHub>("/hubs/runtime")
    .RequireAuthorization(RuntimeTokenAuthenticationDefaults.SchemeName + "Policy");
// PlanningHub (React-facing, JWT-authed via the default user scheme) — fans
// out domain events from the Specifications + ProjectKanban slices to the
// `project:{id}` group so the planning surfaces (spec list, kanban board)
// re-render in real time without polling.
app.MapHub<PlanningHub>("/hubs/planning");

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// SPA fallback - serve index.html for non-API routes (client-side routing)
app.MapFallbackToFile("index.html");

// Global last-resort error handlers
var errorQueue = app.Services.GetRequiredService<ErrorQueue>();
var lastResortLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalErrorHandlers");

AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
{
    var ex = args.ExceptionObject as Exception;
    lastResortLogger.LogCritical(ex, "AppDomain unhandled exception. IsTerminating: {IsTerminating}", args.IsTerminating);

    if (ex != null)
    {
        var entry = new ErrorEntry(
            Message: ex.Message,
            StackTrace: ex.StackTrace,
            Source: "Unhandled",
            Severity: "Critical",
            CorrelationId: null,
            RequestPath: null,
            RequestMethod: null,
            ContextData: $"IsTerminating: {args.IsTerminating}",
            OccurredAt: DateTime.UtcNow
        );
        // Fire-and-forget since this may be terminating
        _ = errorQueue.EnqueueAsync(entry);
    }
};

TaskScheduler.UnobservedTaskException += (sender, args) =>
{
    lastResortLogger.LogError(args.Exception, "Unobserved task exception");

    var entry = new ErrorEntry(
        Message: args.Exception?.Message ?? "Unobserved task exception",
        StackTrace: args.Exception?.StackTrace,
        Source: "Unhandled",
        Severity: "Error",
        CorrelationId: null,
        RequestPath: null,
        RequestMethod: null,
        ContextData: "UnobservedTaskException",
        OccurredAt: DateTime.UtcNow
    );
    _ = errorQueue.EnqueueAsync(entry);

    args.SetObserved();
};

app.Run();

// Make Program class accessible for testing
public partial class Program { }
