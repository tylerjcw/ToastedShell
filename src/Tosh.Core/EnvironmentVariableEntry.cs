namespace Tosh.Core;

public sealed record EnvironmentVariableEntry(string Name, string? Value, bool IsSet);
