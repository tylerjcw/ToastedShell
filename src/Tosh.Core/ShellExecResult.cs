namespace Tosh.Core;

public sealed record ShellExecResult(
    bool ReplacedCurrentProcess,
    int ExitCode);
