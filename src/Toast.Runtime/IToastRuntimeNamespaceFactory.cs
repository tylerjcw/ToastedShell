namespace Tosh.Runtime;

/// <summary>The evaluator-backed values exposed under <c>$tosh.Script</c>.</summary>
public interface IToastScriptNamespace : IShellRecordObject
{
    string Path { get; }

    string Name { get; }

    string Directory { get; }

    object?[] Args { get; }
}

/// <summary>The evaluator-backed values exposed under <c>$tosh.Function</c>.</summary>
public interface IToastFunctionNamespace : IShellRecordObject
{
    string Name { get; }

    object?[] Args { get; }

    object? Input { get; }
}

/// <summary>
/// Composes a host runtime namespace from the live language execution views.
/// </summary>
/// <remarks>
/// Tōast knows the current script and function call; TōSh owns configuration,
/// session, job and host state. Passing the two language views into a host factory keeps
/// the evaluator from constructing TōSh's <c>$tosh</c> object (`TOAST-0006`).
/// </remarks>
public interface IToastRuntimeNamespaceFactory
{
    IShellRecordObject CreateRuntimeNamespace(
        IToastScriptNamespace script,
        IToastFunctionNamespace function);
}
