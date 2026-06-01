using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Source.Features.Authentication.Services;
using Source.Features.RuntimeTokens.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace Source.Infrastructure.Extensions;

public static class AuthenticationExtensions
{
    private const string JwtConfigSection = "Jwt";
    private const string JwtKeyName = "Key";
    private const int MinJwtKeyLength = 32;

    public static IServiceCollection AddAuthenticationServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var jwtKey = RequireJwtSigningKey(configuration, environment);
        var jwtIssuer = configuration["Jwt:Issuer"] ?? "api";
        var jwtAudience = configuration["Jwt:Audience"] ?? "api";

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = false; // Only for development
            options.SaveToken = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.Zero,
                ValidIssuer = jwtIssuer,
                ValidAudience = jwtAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            };

            // Configure to read JWT tokens from cookies
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    // First check the Authorization header (default behavior)
                    var token = context.Request.Headers.Authorization
                        .FirstOrDefault()?.Split(" ").Last();

                    // If no Authorization header token, check cookies
                    if (string.IsNullOrEmpty(token))
                    {
                        token = context.Request.Cookies["auth-token"];
                    }

                    if (!string.IsNullOrEmpty(token))
                    {
                        context.Token = token;
                    }

                    return Task.CompletedTask;
                }
            };
        });

        services.AddAuthorization();

        // Register JWT token service
        services.AddScoped<IJwtTokenService, JwtTokenService>();

        return services;
    }

    /// <summary>
    /// Read <c>Jwt:Key</c> from configuration. Refuses to boot when absent or too short —
    /// same fail-closed posture as <c>SystemSettings:EncryptionKey</c>.
    /// </summary>
    internal static string RequireJwtSigningKey(IConfiguration configuration, IHostEnvironment environment)
    {
        var key = configuration[$"{JwtConfigSection}:{JwtKeyName}"];
        if (!string.IsNullOrWhiteSpace(key) && key.Length >= MinJwtKeyLength)
        {
            return key;
        }

        var hint = environment.IsDevelopment()
            ? $".env / appsettings (\"{JwtConfigSection}\": {{ \"{JwtKeyName}\": \"...\" }}) — min {MinJwtKeyLength} chars"
            : $"the {JwtConfigSection}__{JwtKeyName} environment variable — min {MinJwtKeyLength} chars";

        throw new InvalidOperationException(
            $"""
            {JwtConfigSection}:{JwtKeyName} is not configured or is too short.

            User JWTs are signed with this key. Booting with a missing or weak key would
            let anyone forge sessions — we refuse to start.

            Generate: openssl rand -base64 48
            Set in {hint}.
            """);
    }

    /// <summary>
    /// Registers the additive <c>"RuntimeToken"</c> JWT bearer scheme used by daemon-facing
    /// endpoints (e.g. <c>GET /api/runtimes/{runtimeId}/active-session</c>). This is purely
    /// additive — the default scheme stays <see cref="JwtBearerDefaults.AuthenticationScheme"/>
    /// configured by <see cref="AddAuthenticationServices"/>; the user-auth flow is unchanged.
    ///
    /// <para>Validation reuses the same constants the mint path uses
    /// (<see cref="RuntimeTokenService.Issuer"/>, <see cref="RuntimeTokenService.Audience"/>)
    /// and the same key source (<see cref="IRuntimeTokenSigningKeyService.GetValidationKeys"/>).
    /// The signing key resolver is wired via an <see cref="IPostConfigureOptions{TOptions}"/> so
    /// the lambda can pull a fresh key set from DI on every request — that lets Card 1's
    /// rotation invalidator take effect without restarting the host.</para>
    ///
    /// <para>The <see cref="JwtBearerEvents.OnTokenValidated"/> hook applies the same
    /// revocation-cache check <see cref="RuntimeTokenService.ValidateAsync"/> does, so endpoints
    /// gated on <c>[Authorize(AuthenticationSchemes = "RuntimeToken")]</c> never see a principal
    /// for a revoked jti.</para>
    /// </summary>
    public static IServiceCollection AddRuntimeTokenAuthScheme(this IServiceCollection services)
    {
        // AddAuthentication() (no args) does NOT reset the default scheme — it just returns
        // the existing builder so we can register an additional scheme onto it.
        services.AddAuthentication()
            .AddJwtBearer(RuntimeTokenAuthenticationDefaults.SchemeName, options =>
            {
                options.RequireHttpsMetadata = false; // dev parity with the user-auth scheme
                options.SaveToken = true;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidIssuer = RuntimeTokenService.Issuer,
                    ValidAudience = RuntimeTokenService.Audience,
                    // The default kid-based resolver shortcuts to the matching KeyId; during
                    // rotation the *new* Current carries the same KeyId as the *old* Current
                    // did. Mirror RuntimeTokenService.ValidateAsync and try every key.
                    TryAllIssuerSigningKeys = true,
                    // IssuerSigningKeyResolver is wired in RuntimeTokenJwtBearerPostConfigure
                    // so it has access to the DI container.
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        // SignalR cannot send Authorization headers over the WebSocket
                        // transport (browsers won't allow it), so the daemon attaches the
                        // RuntimeToken as ?access_token=... on the negotiate request and
                        // every subsequent transport. Lift it into the bearer pipeline only
                        // for the runtime hub path — other endpoints stay header-only.
                        var path = ctx.HttpContext.Request.Path;
                        if (path.StartsWithSegments("/hubs/runtime"))
                        {
                            var qs = ctx.Request.Query["access_token"].ToString();
                            if (!string.IsNullOrEmpty(qs))
                            {
                                ctx.Token = qs;
                            }
                        }
                        return Task.CompletedTask;
                    },

                    OnTokenValidated = ctx =>
                    {
                        // Revocation gate. RuntimeTokenService.ValidateAsync applies the same
                        // check; duplicating it here at the middleware boundary means
                        // controllers using [Authorize(AuthenticationSchemes = "RuntimeToken")]
                        // never see a revoked principal.
                        var jtiClaim = ctx.Principal?.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
                        if (Guid.TryParse(jtiClaim, out var jti))
                        {
                            var cache = ctx.HttpContext.RequestServices.GetRequiredService<IRevocationCache>();
                            if (cache.IsRevoked(jti))
                            {
                                ctx.Fail("token_revoked");
                                return Task.CompletedTask;
                            }

                            // Record successful validation for batched usage metrics
                            // (LastUsedAt / RequestCount). Singleton accumulator —
                            // O(1) dict bump, never touches the DB on the hot path.
                            // Flushed every 30s by RuntimeTokenUsageFlushJob.
                            var recorder = ctx.HttpContext.RequestServices.GetRequiredService<RuntimeTokenUsageRecorder>();
                            recorder.Record(jti);
                        }
                        return Task.CompletedTask;
                    },
                };
            });

        // Wire the signing-key resolver via post-configure so it can resolve services
        // from the root container at validation time (matches the scope-resolution
        // pattern used by RevocationCacheWarmupService).
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, RuntimeTokenJwtBearerPostConfigure>();

        return services;
    }
}

/// <summary>
/// Constants shared by producers and consumers of the RuntimeToken auth scheme.
/// Keeping the scheme name in one place keeps callers from string-duplicating it.
/// </summary>
public static class RuntimeTokenAuthenticationDefaults
{
    public const string SchemeName = "RuntimeToken";
}

/// <summary>
/// Post-configures the <c>"RuntimeToken"</c> <see cref="JwtBearerOptions"/> with an
/// <see cref="TokenValidationParameters.IssuerSigningKeyResolver"/> that pulls the current
/// validation-key set from <see cref="IRuntimeTokenSigningKeyService"/> on each request.
///
/// <para>The resolver lambda fires on every JWT validation; we route it through DI so that
/// when the rotation event handler invalidates the signing-key cache, the very next request
/// reads the fresh keys without a restart.</para>
/// </summary>
internal sealed class RuntimeTokenJwtBearerPostConfigure : IPostConfigureOptions<JwtBearerOptions>
{
    private readonly IServiceProvider _sp;

    public RuntimeTokenJwtBearerPostConfigure(IServiceProvider sp)
    {
        _sp = sp;
    }

    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (name != RuntimeTokenAuthenticationDefaults.SchemeName) return;

        options.TokenValidationParameters.IssuerSigningKeyResolver =
            (token, securityToken, kid, parameters) =>
            {
                // IRuntimeTokenSigningKeyService is registered as a singleton, so the scope
                // is technically unnecessary — but matching the existing scope-resolution
                // pattern keeps the call site forward-compatible if registration changes.
                using var scope = _sp.CreateScope();
                var keys = scope.ServiceProvider.GetRequiredService<IRuntimeTokenSigningKeyService>();
                return keys.GetValidationKeys();
            };
    }
} 