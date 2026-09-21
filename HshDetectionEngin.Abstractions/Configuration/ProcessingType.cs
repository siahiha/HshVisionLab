namespace HshDetectionEngin;

/// <summary>
/// Stable identifier of a processing capability. The string value is kept only
/// at the configuration/serialization boundary so existing settings files stay
/// compatible; runtime code compares this value type instead of raw strings.
/// </summary>
public readonly struct ProcessingType : IEquatable<ProcessingType>
{
    public static readonly ProcessingType Plate = new("Plate");
    public static readonly ProcessingType Face = new("Face");

    public string Value { get; }

    public ProcessingType(string value)
    {
        Value = value?.Trim() ?? string.Empty;
    }

    public static ProcessingType Parse(string? value) => new(value ?? string.Empty);

    public bool Equals(ProcessingType other) =>
        StringComparer.OrdinalIgnoreCase.Equals(Value, other.Value);

    public override bool Equals(object? obj) =>
        obj is ProcessingType other && Equals(other);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Value ?? string.Empty);

    public override string ToString() => Value;

    public static bool operator ==(ProcessingType left, ProcessingType right) => left.Equals(right);
    public static bool operator !=(ProcessingType left, ProcessingType right) => !left.Equals(right);
}
