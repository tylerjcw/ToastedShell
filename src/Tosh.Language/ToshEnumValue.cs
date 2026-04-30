using Tosh.Runtime;

namespace Tosh.Language;

public readonly record struct ToshEnumValue(ToshEnumDefinition Definition, string Name, object? UnderlyingValue)
    : IShellTypedObject, IFormattable, IShellTypeCheckable
{
    public IShellTypeDescriptor ShellTypeDescriptor => Definition;

    public override string ToString() => Name;

    public string ToString(string? format, IFormatProvider? formatProvider) => Name;

    public bool IsInstanceOf(string typeName)
    {
        return string.Equals(Definition.Name, typeName, StringComparison.OrdinalIgnoreCase);
    }
}
