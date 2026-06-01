using Microsoft.OpenApi.Models;
using Source.Infrastructure.Swagger;

namespace Source.Infrastructure.Extensions;

public static class SwaggerExtensions
{
    public static IServiceCollection AddSwaggerServices(this IServiceCollection services)
    {
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "API",
                Version = "v1",
                Description = "Clean Architecture API with JWT Authentication"
            });

            // Support nullable reference types properly
            options.SupportNonNullableReferenceTypes();

            // Ensure non-nullable properties are marked as required
            options.SchemaFilter<RequiredNotNullableSchemaFilter>();

            // Project [JsonPolymorphic] + [JsonDerivedType] into OpenAPI 3
            // discriminator + oneOf, so Orval generates a real discriminated
            // union on the TS side instead of a bare base type.
            options.SchemaFilter<JsonPolymorphismSchemaFilter>();

            // Add JWT Authentication to Swagger
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.ApiKey,
                Scheme = "Bearer"
            });

            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    Array.Empty<string>()
                }
            });
        });

        return services;
    }

    public static WebApplication UseSwaggerInDevelopment(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/swagger/v1/swagger.json", "API v1");
                options.RoutePrefix = "swagger";
                options.DisplayRequestDuration();
            });
        }

        return app;
    }
} 