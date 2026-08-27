namespace Tosh.Runtime;

public sealed record ShellNameRemovalResult(
    string Name,
    bool RemovedVariable,
    string VariableScope,
    bool RemovedCommand,
    string CommandKind,
    string CommandScope,
    bool RemovedEnvironment,
    bool FreedValue = false,
    string FreedValueKind = "");
