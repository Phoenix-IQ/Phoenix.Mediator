using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Options;
using Phoenix.Mediator.Abstractions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Phoenix.Mediator.Web;

/// <summary>
/// Keeps mediator-request members marked <c>[FromRoute]</c>, <c>[FromQuery]</c> or <c>[FromHeader]</c> out of the
/// Minimal API JSON body: they are not read from or written to it, and they don't appear in its OpenAPI schema.
/// Minimal APIs bind a request parameter from the body as a whole and only honor those attributes under
/// <c>[AsParameters]</c>, so without this such a member is documented both as a parameter and as a body property.
/// </summary>
internal sealed class RequestBodyJsonOptionsSetup : IPostConfigureOptions<JsonOptions>
{
    // Post-configure so the wrapper also covers a resolver the app sets in its own ConfigureHttpJsonOptions call.
    public void PostConfigure(string? name, JsonOptions options)
    {
        var serializerOptions = options.SerializerOptions;
        if (serializerOptions.IsReadOnly || serializerOptions.TypeInfoResolver is null)
            return;

        // Wrap a copy of the chain. Once something adds to TypeInfoResolverChain (AddProblemDetails() does),
        // TypeInfoResolver returns that live chain, and on .NET 8/9 the setter refills the same chain with the
        // wrapper, so wrapping the live chain makes the wrapper call itself until the stack overflows.
        var resolver = JsonTypeInfoResolver.Combine([.. serializerOptions.TypeInfoResolverChain]);
        serializerOptions.TypeInfoResolver = new NonBodyMemberResolver(resolver);
    }

    private sealed class NonBodyMemberResolver(IJsonTypeInfoResolver inner) : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            var typeInfo = inner.GetTypeInfo(type, options);
            if (typeInfo is not { Kind: JsonTypeInfoKind.Object } || !MediatorRequestTypes.IsRequest(type))
                return typeInfo;

            var constructorParameters = type.GetConstructors().SelectMany(static c => c.GetParameters()).ToArray();
            foreach (var property in typeInfo.Properties)
            {
                if (IsBoundOutsideBody(property, constructorParameters))
                    ExcludeFromBody(property);
            }

            return typeInfo;
        }
    }

    private static bool IsBoundOutsideBody(JsonPropertyInfo property, ParameterInfo[] constructorParameters)
    {
        if (property.AttributeProvider is not MemberInfo member)
            return false;

        if (HasNonBodySource(Attribute.GetCustomAttributes(member, inherit: true)))
            return true;

        // Positional records put the attribute on the constructor parameter, not on the generated property.
        return constructorParameters.Any(parameter =>
            string.Equals(parameter.Name, member.Name, StringComparison.OrdinalIgnoreCase)
            && HasNonBodySource(Attribute.GetCustomAttributes(parameter, inherit: true)));
    }

    private static bool HasNonBodySource(Attribute[] attributes)
    {
        return attributes.Any(static attribute => attribute is IFromRouteMetadata or IFromQueryMetadata or IFromHeaderMetadata);
    }

    private static void ExcludeFromBody(JsonPropertyInfo property)
    {
        // Same contract shape [JsonIgnore] produces. Removing the property instead makes System.Text.Json throw for
        // positional records, because each constructor parameter must match a property.
        property.Get = null;
        property.Set = null;
        property.IsRequired = false;
#if NET9_0_OR_GREATER
        // The discarding converter below hands back default, i.e. null for a reference type. With
        // RespectNullableAnnotations that would be rejected for a non-nullable member ("the constructor
        // parameter 'Slug' doesn't allow null values"), turning a member the client shouldn't have sent
        // into a 400. The value is thrown away either way, so allow null on the way in. Only types that
        // can hold null accept this; for the rest (int, Guid, ...) default is never null anyway.
        if (!property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null)
            property.IsSetNullable = true;
#endif
        // A constructor parameter still reads its matching JSON value when the property has no setter; discard it.
        property.CustomConverter = (JsonConverter)Activator.CreateInstance(
            typeof(DiscardValueConverter<>).MakeGenericType(property.PropertyType))!;
    }

    private sealed class DiscardValueConverter<T> : JsonConverter<T>
    {
        public override bool HandleNull => true;

        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // TrySkip, not Skip. Reading a request body is a STREAMING deserialization, so the reader can
            // sit on a buffer that does not yet hold the rest of the payload, and Skip() throws there
            // ("Cannot skip tokens on partial JSON"). That surfaced as a 400 for every positional record
            // whose body actually carried the excluded member — the exact case this whole type exists to
            // neutralize — while a synchronous Deserialize(string) never hit it.
            // System.Text.Json reads ahead so a custom value converter always sees the complete value,
            // which is why TrySkip succeeds; the guard keeps a silent mis-bind from replacing a loud failure.
            if (!reader.TrySkip())
                throw new JsonException($"Could not skip the body value of an excluded '{typeToConvert}' member.");

            return default;
        }

        // Never called: the property has no getter, so it is never written.
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => throw new NotSupportedException();
    }
}
