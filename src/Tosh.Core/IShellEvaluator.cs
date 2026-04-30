namespace Tosh.Core;

public interface IShellEvaluator
{
    IAsyncEnumerable<object?> EvaluateAsync(string source, string sourceName, CancellationToken cancellationToken = default);

    bool TryGetVariableValue(string name, out object? value);

    ShellNameRemovalResult Forget(string name);

    IReadOnlyList<ShellNameRemovalResult> ForgetValue(object? value);

    IReadOnlyList<KeyValuePair<string, object?>> GetVisibleVariables() => [];

    /// <summary>
    /// Adds a diagnostic <paramref name="code"/> to the innermost active scope's
    /// hush set, or to <c>$tosh.Config.Diagnostics.Hushed</c> at top level.
    /// </summary>
    void HushDiagnosticCode(string code) => throw new NotSupportedException(
        "This evaluator does not support diagnostic suppression.");
}
