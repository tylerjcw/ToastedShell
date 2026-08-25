namespace Tosh.Runtime;

/// <summary>
/// Portable object/member operations for pure compiled artifacts. The ordinary runtime and
/// permissive profiles retain their engine-aware host path; this boundary covers CLR values,
/// emitted CLR shells, and runtime interfaces without referencing the compiler host.
/// </summary>
public static class PortableObjectBoundary
{
    private static readonly ReflectionInvoker Invoker = new();
    private static readonly ReflectionObjectAccessor Accessor = new();

    public static object? GetMember(object? target, string memberPath, bool nullSafe)
    {
        if (target is null)
        {
            return nullSafe
                ? null
                : throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.expression_failed",
                    Title: ToastMessages.MemberOfNull(memberPath),
                    Label: "while evaluating this expression"));
        }

        return Accessor.GetValue(target, memberPath);
    }

    public static object? SetMember(object? target, string memberPath, object? value, bool nullSafe)
    {
        if (target is null)
        {
            if (nullSafe) return null;
            throw new NullReferenceException($"member assignment '{memberPath}' on null target");
        }

        Accessor.SetValue(target, memberPath, value);
        return value;
    }

    public static object? InvokeMember(object? target, string methodName, object?[] arguments, bool nullSafe)
    {
        if (target is null)
        {
            if (nullSafe) return null;
            throw new NullReferenceException($"method call '{methodName}' on null target");
        }

        var result = Invoker.InvokeInstance(target, methodName, arguments);
        return result.ReturnedVoid ? null : result.Value;
    }

    public static object? GetIndex(object? target, object? index) =>
        ShellIndexingUtilities.GetIndexedValue(target, index);

    public static object? SetIndex(object? target, object? index, object? value)
    {
        ShellIndexingUtilities.SetIndexedValueAsync(
                target,
                index,
                value,
                IndexLookupKind.Default,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return value;
    }

    public static bool IsTruthy(object? value) => ToshTruthiness.IsTruthy(value);

    public static IEnumerable<object?> ToEnumerable(object? source)
    {
        if (source is null) yield break;
        if (source is string text)
        {
            foreach (var character in text) yield return character;
            yield break;
        }

        if (source is System.Collections.IEnumerable sequence)
        {
            foreach (var item in sequence) yield return item;
            yield break;
        }

        yield return source;
    }

    public static void ThrowValue(object? value)
    {
        if (value is ToshError tosh)
        {
            tosh.Data["tosh.thrown"] = true;
            throw tosh;
        }

        if (value is Exception exception && value is not ShellControlFlowException)
        {
            exception.Data["tosh.thrown"] = true;
            throw exception;
        }

        var signal = new ThrowSignalException(default, value);
        signal.Data["tosh.thrown"] = true;
        throw signal;
    }

    public static object? CaughtValueOf(Exception exception)
    {
        if (exception is ShellControlFlowException) throw exception;
        return exception switch
        {
            ThrowSignalException signal => signal.Value,
            ToshError tosh when tosh.Cause is { } cause
                && cause.GetType().FullName == "Tosh.Language.ToshClassInstance"
                => cause,
            _ => exception,
        };
    }
}
