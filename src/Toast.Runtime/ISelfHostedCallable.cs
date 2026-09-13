namespace Tosh.Runtime;

/// <summary>A callable that can run itself, with no context supplied by the caller.</summary>
/// <remarks>
/// <para>
/// Invoking a script function normally needs a <see cref="CommandContext"/>, which only a
/// command already being executed has. That is fine when the caller is the shell, and
/// impossible when the caller is a CLR type calling back — a widget raising an event, a
/// comparator, a native library's callback. Such a caller knows nothing about the shell
/// and has nowhere to get a context from.
/// </para>
/// <para>
/// A function knows which engine defined it, so it can build that context itself. This
/// interface is how it says so. It is the whole reason a script can write
/// <c>$field.Changed = func(text) => (…)</c> and have a CLR <c>Action&lt;string&gt;</c>
/// come out the other side.
/// </para>
/// </remarks>
public interface ISelfHostedCallable
{
    /// <summary>
    /// Runs the callable to completion on the calling thread and returns its result.
    /// </summary>
    /// <remarks>
    /// A callable that produces several values answers with the list, matching how a
    /// function's result reads anywhere else it is used as a value.
    /// </remarks>
    object? InvokeWithoutContext(IReadOnlyList<object?> arguments);
}
