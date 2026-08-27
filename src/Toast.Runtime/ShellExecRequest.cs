namespace Tosh.Runtime;

public sealed record ShellExecRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);
