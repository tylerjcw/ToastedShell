namespace Tosh.Runtime;

public sealed record ShellExecResult(
    bool ReplacedCurrentProcess,
    int ExitCode);
