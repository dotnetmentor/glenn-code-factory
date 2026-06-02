using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Source.Features.CiPublish;

/// <summary>
/// Validates <c>X-Ci-Publish-Key</c> against <see cref="CiPublishOptions.ApiKey"/>.
/// Does not read <c>Authorization: Bearer</c> — that header is reserved for user JWTs.
/// No-op when the key is not configured.
/// </summary>
public sealed class CiPublishAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly CiPublishOptions _options;

    public CiPublishAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<CiPublishOptions> options)
        : base(schemeOptions, logger, encoder)
    {
        _options = options.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = _options.ApiKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (configured.Length < 32)
        {
            Logger.LogError("CiPublish:ApiKey is configured but shorter than 32 characters — refusing CiPublish auth.");
            return Task.FromResult(AuthenticateResult.Fail("CiPublish misconfigured"));
        }

        if (!Request.Headers.TryGetValue(CiPublishAuthenticationDefaults.HeaderName, out var header)
            || string.IsNullOrWhiteSpace(header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var provided = header.ToString().Trim();
        if (!FixedTimeEquals(provided, configured))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid CI publish API key"));
        }

        var identity = new ClaimsIdentity(
            [new Claim(CiPublishAuthenticationDefaults.ClaimType, CiPublishAuthenticationDefaults.ClaimValue)],
            Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
