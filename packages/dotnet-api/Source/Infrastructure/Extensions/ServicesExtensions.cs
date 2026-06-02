using Source.Infrastructure.Services.Email;
using Source.Infrastructure.Services.FileStorage;
using Source.Features.SignalR.Diagnostics;
using Source.Features.SignalR.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;
using Resend;

namespace Source.Infrastructure.Extensions;

public static class ServicesExtensions
{
    public static IServiceCollection AddOfflineFirstServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Email Service - Configurable provider
        var emailProvider = configuration["Email:Provider"] ?? "Console";

        switch (emailProvider.ToUpperInvariant())
        {
            case "RESEND":
                services.AddOptions();
                services.AddHttpClient<ResendClient>();
                services.Configure<ResendClientOptions>(o =>
                {
                    o.ApiToken = configuration["Email:Resend:ApiToken"]
                        ?? throw new InvalidOperationException("Email:Resend:ApiToken configuration is required when using Resend provider");
                });
                services.AddTransient<IResend, ResendClient>();
                services.AddScoped<IEmailService, ResendEmailService>();
                break;
            case "CONSOLE":
            default:
                services.AddScoped<IEmailService, ConsoleEmailService>();
                break;
        }

        // File Storage Service - Configurable provider
        var fileStorageProvider = configuration["FileStorage:Provider"] ?? "Local";

        switch (fileStorageProvider.ToUpperInvariant())
        {
            case "R2":
                services.AddScoped<IFileStorageService, CloudflareR2StorageService>();
                break;
            case "LOCAL":
            default:
                services.AddScoped<IFileStorageService, LocalFileStorageService>();
                break;
        }

        // Web Search (Tavily)
        services.AddHttpClient("TavilyClient");

        return services;
    }

    public static IServiceCollection AddRealTimeServices(this IServiceCollection services)
    {
        // Singleton, process-local map of runtimeId → daemon connectionId.
        // Required by user-facing controllers that need to InvokeAsync a typed
        // hub method with a return value (e.g. DiffsController) — those calls
        // only work against a single client connection, never against a group.
        // Populated by TrackRuntimeConnectionHandler on the standard
        // RuntimeConnected/RuntimeDisconnected event path.
        services.AddSingleton<IRuntimeConnectionRegistry, RuntimeConnectionRegistry>();

        services.AddSignalR(options =>
        {
            // Surface the real exception text to the daemon while we debug the
            // bootstrap round-trip. With detailed errors off, every server-side
            // throw is redacted to "an error on the server" — useless during
            // initial wire-up. Safe to flip on for now: hub callers are our own
            // daemon authenticated by RuntimeToken; no untrusted clients on
            // /hubs/runtime. Re-evaluate before public exposure.
            options.EnableDetailedErrors = true;
            options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10 MB — daemon tool results and diff payloads can be large; default 32 KB drops the runtime websocket mid-turn
            options.ClientTimeoutInterval = TimeSpan.FromMinutes(2);
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        });

        // Diagnostic wrapper: log raw input bytes around JsonHubProtocol so we
        // can see the actual JSON that produces the mysterious
        // "target: TurnCompleted" parse failure on /hubs/runtime. Temporary —
        // remove once that frame's origin is identified.
        services.AddSingleton<IHubProtocol>(sp =>
        {
            var opts = Options.Create(new JsonHubProtocolOptions());
            opts.Value.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            // Tolerate the daemon's "" sessionId sentinel on bootstrap-stage
            // EmitEvent calls — see EmptyStringNullableGuidJsonConverter.
            opts.Value.PayloadSerializerOptions.Converters.Add(new EmptyStringNullableGuidJsonConverter());
            // NOTE: do NOT add `DefaultIgnoreCondition.WhenWritingNull` here.
            // Stripping nulls on the way out breaks the daemon's
            // `BootstrapPayloadV2.repo === null` check (see CloningRepoStage):
            // the daemon expects `"repo": null` for the "no repo configured"
            // case. With WhenWritingNull, `repo` is omitted entirely, the
            // daemon reads `undefined`, and `repo.url` throws — daemon exits 1,
            // supervisord hits FATAL state. The wire reality the daemon types
            // are built against is "explicit null for absent nullables"; keep
            // it that way. Daemon-side fix for the analogous
            // `ServiceSpec.env` / `Object.keys(null)` issue lives in
            // SupervisordController (`!= null` loose check).
            var inner = new JsonHubProtocol(opts);
            return new LoggingHubProtocol(inner, sp.GetRequiredService<ILogger<LoggingHubProtocol>>());
        });
        return services;
    }
}
