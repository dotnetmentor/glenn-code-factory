using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Source.Infrastructure.AuthorizationModels;

namespace Source.Features.CiPublish.Extensions;

public static class CiPublishExtensions
{
    public static IServiceCollection AddCiPublishFeature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<CiPublishOptions>(configuration.GetSection(CiPublishOptions.SectionName));

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, CiPublishAuthenticationHandler>(
                CiPublishAuthenticationDefaults.SchemeName,
                _ => { });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(CiPublishAuthenticationDefaults.PublishPolicy, policy =>
            {
                policy.AddAuthenticationSchemes(
                    Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme,
                    CiPublishAuthenticationDefaults.SchemeName);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(ctx =>
                    ctx.User.IsInRole(RoleConstants.SuperAdmin)
                    || ctx.User.HasClaim(
                        CiPublishAuthenticationDefaults.ClaimType,
                        CiPublishAuthenticationDefaults.ClaimValue));
            });

            options.AddPolicy(CiPublishAuthenticationDefaults.CiPublishOnlyPolicy, policy =>
            {
                policy.AddAuthenticationSchemes(CiPublishAuthenticationDefaults.SchemeName);
                policy.RequireAuthenticatedUser();
                policy.RequireClaim(
                    CiPublishAuthenticationDefaults.ClaimType,
                    CiPublishAuthenticationDefaults.ClaimValue);
            });
        });

        return services;
    }
}
