namespace Sendspin.SDK.Protocol;

/// <summary>
/// Represents a JSON field that distinguishes between "absent" (not in JSON)
/// and "present" (in JSON, possibly null).
/// </summary>
/// <remarks>
/// Used for JSON fields where explicit null has semantic meaning different from absence.
/// For example, <c>progress: null</c> means "track ended" while progress absent means "no update".
/// This matches the CLI's <c>UndefinedField</c> pattern in Python.
/// </remarks>
/// <typeparam name="T">The type of the optional value.</typeparam>
public readonly struct Optional<T>
{
    private readonly T? _value;
    private readonly bool _isPresent;

    private Optional(T? value, bool isPresent)
    {
        _value = value;
        _isPresent = isPresent;
    }

    /// <summary>
    /// Gets whether the field was present in the JSON (value may be null).
    /// </summary>
    public bool IsPresent => _isPresent;

    /// <summary>
    /// Gets whether the field was absent from the JSON.
    /// </summary>
    public bool IsAbsent => !_isPresent;

    /// <summary>
    /// Gets the value. Throws if absent.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the field was absent.</exception>
    public T? Value => _isPresent
        ? _value
        : throw new InvalidOperationException("Cannot access Value of an absent Optional field");

    /// <summary>
    /// Gets the value if present, otherwise returns the fallback.
    /// </summary>
    /// <param name="fallback">The fallback value to return if absent.</param>
    /// <returns>The value if present, otherwise the fallback.</returns>
    public T? GetValueOrDefault(T? fallback = default) => _isPresent ? _value : fallback;

    /// <summary>
    /// Creates an Optional representing an absent field.
    /// </summary>
    public static Optional<T> Absent() => new(default, false);

    /// <summary>
    /// Creates an Optional representing a present field (may be null).
    /// </summary>
    /// <param name="value">The value (can be null for explicit JSON null).</param>
    public static Optional<T> Present(T? value) => new(value, true);

    /// <inheritdoc />
    public override string ToString() => _isPresent
        ? $"Present({_value})"
        : "Absent";
}
