using System.Globalization;

namespace Tosh.Runtime;

public sealed record FileSystemPrincipalInfo(long Id, string? Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? Id.ToString(CultureInfo.InvariantCulture)
        : Name;

    public override string ToString() => DisplayName;
}
