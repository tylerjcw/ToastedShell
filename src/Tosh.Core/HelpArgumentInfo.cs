namespace Tosh.Core;

public sealed record HelpArgumentInfo(
    string Name,
    string Description,
    bool Required = true,
    string? TypeName = null);
