namespace Tosh.Core;

public interface IShellEvaluator
{
    IAsyncEnumerable<object?> EvaluateAsync(string source, string sourceName, CancellationToken cancellationToken = default);

    bool TryGetVariableValue(string name, out object? value);

    ShellNameRemovalResult Forget(string name);

    IReadOnlyList<ShellNameRemovalResult> ForgetValue(object? value);

    IReadOnlyList<KeyValuePair<string, object?>> GetVisibleVariables() => [];
}
