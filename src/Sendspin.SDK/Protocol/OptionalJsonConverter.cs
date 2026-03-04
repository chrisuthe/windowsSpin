using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Sendspin.SDK.Protocol;

/// <summary>
/// JSON converter factory for <see cref="Optional{T}"/> that distinguishes
/// absent fields from explicit nulls.
/// </summary>
/// <remarks>
/// <para>
/// In JSON, a field can be:
/// <list type="bullet">
///   <item><description><b>Present with value</b>: <c>{"progress": {...}}</c></description></item>
///   <item><description><b>Present but null</b>: <c>{"progress": null}</c></description></item>
///   <item><description><b>Absent</b>: <c>{}</c> (no progress field)</description></item>
/// </list>
/// </para>
/// <para>
/// Standard C# nullable types collapse the latter two into <c>null</c>.
/// This converter preserves the distinction using <see cref="Optional{T}"/>.
/// </para>
/// </remarks>
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(OptionalJsonConverter<>).MakeGenericType(valueType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

/// <summary>
/// JSON converter for <see cref="Optional{T}"/>.
/// </summary>
/// <typeparam name="T">The type of the optional value.</typeparam>
internal sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    /// <inheritdoc />
    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // If we're reading, the field IS present in the JSON
        // (System.Text.Json only calls Read when the property exists)
        if (reader.TokenType == JsonTokenType.Null)
        {
            // Field is present with explicit null value
            return Optional<T>.Present(default);
        }

        // Field is present with a value
        // Use JsonTypeInfo to avoid RequiresUnreferencedCode warning (AOT-friendly)
        var typeInfo = (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
        var value = JsonSerializer.Deserialize(ref reader, typeInfo);
        return Optional<T>.Present(value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        if (value.IsAbsent)
        {
            // Don't write anything - the field should be omitted
            // Note: This requires the containing object to use JsonIgnoreCondition
            // or custom serialization to actually omit the property
            return;
        }

        if (value.Value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}
