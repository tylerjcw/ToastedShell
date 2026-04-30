namespace Tosh.Runtime;

public sealed record EnvironmentVariableEntry(string Name, string? Value, bool IsSet);
