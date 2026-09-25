namespace Tosh.Language;

/// <summary>
/// Describes a dynamically-added property on a fluid class or record instance.
/// </summary>
public sealed class DynamicPropertyDescriptor
{
    public required string Name { get; init; }
    public string? TypeName { get; init; }
    public object? Value { get; set; }
    public object? Getter { get; init; }
    public object? Setter { get; init; }
    public bool IsShy { get; init; }
    public bool IsFixed { get; init; }
    public string? SourceName { get; init; }
    public string? SourceText { get; init; }

    public bool IsComputed => Getter is not null;
    public bool IsWritable => !IsFixed && (Setter is not null || Getter is null);
}
