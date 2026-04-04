using Tosh.Core;

namespace Tosh.Language;

public readonly record struct ToshEnumValue(ToshEnumDefinition Definition, string Name, object? UnderlyingValue)
    : IShellTypedObject, IFormattable
{
    public IShellTypeDescriptor ShellTypeDescriptor => Definition;

    public override string ToString() => Name;

    public string ToString(string? format, IFormatProvider? formatProvider) => Name;
}
