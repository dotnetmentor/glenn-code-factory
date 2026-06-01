using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Source.Infrastructure.Swagger;

/// <summary>
/// Ensures that non-nullable properties are marked as required in the OpenAPI schema
/// This fixes Orval/TypeScript generation to have proper required vs optional properties
/// </summary>
public class RequiredNotNullableSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (schema.Properties == null)
            return;

        // Find all properties that are:
        // 1. Not nullable
        // 2. Don't have a default value
        // 3. Not already in the required list
        var notNullableProperties = schema.Properties
            .Where(x => !x.Value.Nullable &&
                       x.Value.Default == null &&
                       !schema.Required.Contains(x.Key))
            .Select(x => x.Key)
            .ToList();

        foreach (var property in notNullableProperties)
        {
            schema.Required.Add(property);
        }
    }
}
