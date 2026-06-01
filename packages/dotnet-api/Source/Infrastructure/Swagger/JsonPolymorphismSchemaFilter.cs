using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Source.Infrastructure.Swagger;

/// <summary>
/// Projects System.Text.Json's <see cref="JsonPolymorphicAttribute"/> +
/// <see cref="JsonDerivedTypeAttribute"/> declarations into OpenAPI 3
/// <c>discriminator</c> + <c>oneOf</c> shape, so Orval / NSwag / generic
/// codegen produce a real discriminated union on the TypeScript side.
///
/// <para>Swashbuckle out-of-the-box does <i>not</i> read the STJ attributes —
/// it falls back to the bare base schema, which leaves the frontend stuck with
/// the abstract type and no way to narrow on the discriminator. This filter
/// reads the attributes off the base type, registers each subtype as a
/// component schema (with the discriminator property + an <c>allOf</c> back to
/// the base), and decorates the base with <c>discriminator</c> + the
/// type-tag -> subtype mapping.</para>
///
/// <para>Mirrors the on-wire serialization the STJ runtime produces: the
/// discriminator key is emitted as the first JSON property of every concrete
/// payload (we re-emit it here as a required string with the type-tag as the
/// only allowed value), and the value matches the <c>typeDiscriminator</c>
/// supplied in <c>[JsonDerivedType(typeof(Foo), "fooTag")]</c>.</para>
/// </summary>
public class JsonPolymorphismSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        var polymorphic = context.Type.GetCustomAttribute<JsonPolymorphicAttribute>();
        if (polymorphic is null)
            return;

        var derivedTypes = context.Type.GetCustomAttributes<JsonDerivedTypeAttribute>().ToList();
        if (derivedTypes.Count == 0)
            return;

        var discriminatorName = polymorphic.TypeDiscriminatorPropertyName ?? "$type";

        // Register each subtype as its own component schema with allOf->base
        // and a constant discriminator property. We must use GenerateSchema so
        // Swashbuckle registers the subtype's own properties; the call returns
        // a $ref and registers the component on first use.
        var oneOf = new List<OpenApiSchema>();
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var derived in derivedTypes)
        {
            var typeTag = derived.TypeDiscriminator?.ToString();
            if (string.IsNullOrEmpty(typeTag))
                continue;

            // Generate (or fetch cached) schema for the subtype — this registers
            // the subtype in components.schemas with all its own properties.
            var subSchema = context.SchemaGenerator.GenerateSchema(derived.DerivedType, context.SchemaRepository);

            // Inject the discriminator constant property into the registered
            // subtype schema so the on-wire shape matches what STJ emits. The
            // generator gave us a $ref; mutate the underlying component.
            if (subSchema.Reference is not null &&
                context.SchemaRepository.Schemas.TryGetValue(subSchema.Reference.Id, out var registered))
            {
                registered.Properties[discriminatorName] = new OpenApiSchema
                {
                    Type = "string",
                    Enum = new List<IOpenApiAny> { new OpenApiString(typeTag) }
                };
                if (!registered.Required.Contains(discriminatorName))
                    registered.Required.Add(discriminatorName);
            }

            oneOf.Add(subSchema);
            mapping[typeTag] = subSchema.Reference?.ReferenceV3 ?? string.Empty;
        }

        // Decorate the base schema with the discriminator + oneOf so codegen
        // can build a real union. The base still carries the shared properties
        // (sessionId, sequence, createdAt) — leaving them inline means each
        // subtype inherits them via the allOf-back-to-base linkage Swashbuckle
        // already wires up.
        schema.Discriminator = new OpenApiDiscriminator
        {
            PropertyName = discriminatorName,
            Mapping = mapping
        };
        schema.OneOf = oneOf;

        // The discriminator property itself must be declared + required on the
        // base — OpenAPI 3 requires the discriminator field exist on the parent
        // schema for the union to be valid.
        if (!schema.Properties.ContainsKey(discriminatorName))
        {
            schema.Properties[discriminatorName] = new OpenApiSchema { Type = "string" };
        }
        if (!schema.Required.Contains(discriminatorName))
            schema.Required.Add(discriminatorName);
    }
}
