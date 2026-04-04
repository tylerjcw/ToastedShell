namespace Tosh.Core;

public sealed record ShellExecRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory);
