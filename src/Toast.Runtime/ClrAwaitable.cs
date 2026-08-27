using System.Reflection;

namespace Tosh.Runtime;

/// <summary>
/// Recognises and awaits CLR awaitables — <see cref="Task"/>,
/// <see cref="Task{TResult}"/>, <see cref="ValueTask"/>, and
/// <see cref="ValueTask{TResult}"/> — so ToastScript's <c>await</c> means what a
/// .NET reader expects it to mean.
/// </summary>
/// <remarks>
/// <para>
/// Before this, a CLR method returning a task handed the task itself into the
/// pipeline and there was no non-blocking way to get the value out.
/// <c>$p.SendPingAsync(…)</c> produced a value that displayed as
/// <c>AsyncStateMachineBox`1</c> — the internal state machine type, since
/// <see cref="Task"/> does not override <c>ToString</c> — and <c>await</c> refused it
/// with <c>await_requires_future</c> because it only understood its own
/// <c>ShellFuture</c>. The only route was <c>.Result</c>, which blocks the calling
/// thread and can deadlock.
/// </para>
/// <para>
/// The awaited type is read from the *declared* generic argument rather than from the
/// runtime type, which matters more than it looks. A method declared to return plain
/// <see cref="Task"/> is implemented as <c>AsyncStateMachineBox&lt;VoidTaskResult&gt;</c>
/// at run time, so asking the runtime type for a <c>Result</c> property finds one —
/// holding an internal struct that means "no value". Yielding that would turn every
/// void-returning async method into a command that emits garbage.
/// </para>
/// </remarks>
public static class ClrAwaitable
{
    private const string VoidResultTypeName = "System.Threading.Tasks.VoidTaskResult";

    /// <summary>True when <paramref name="value"/> is something this can await.</summary>
    public static bool IsAwaitable(object? value) =>
        value is Task or ValueTask || IsValueTaskOfT(value);

    /// <summary>
    /// Awaits <paramref name="value"/> and returns its result, or
    /// <see langword="null"/> with <paramref name="hasResult"/> false when the
    /// awaitable produces no value.
    /// </summary>
    public static async Task<(object? Result, bool HasResult)> AwaitAsync(
        object value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var task = value switch
        {
            Task existing => existing,
            ValueTask valueTask => valueTask.AsTask(),
            _ => AsTaskViaReflection(value),
        };

        // WaitAsync rather than a bare await, so Ctrl-C during an await behaves the
        // same here as on the ShellFuture path — which uses exactly this call. A bare
        // await would leave the caller stuck until the task itself finished.
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);

        // The result type is read from the awaitable's declared generic argument, not
        // from a `Result` property on the runtime type. Those differ: a method
        // declared to return plain `Task` is compiled to
        // AsyncStateMachineBox<VoidTaskResult>, whose inherited Result property holds
        // an internal struct meaning "no value". Trusting the property would make
        // every void async method emit garbage.
        if (FindAwaitedResultType(task.GetType()) is null)
        {
            return (null, false);
        }

        var property = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
        return property is null ? (null, false) : (property.GetValue(task), true);
    }

    /// <summary>
    /// Describes an awaitable for display, so an un-awaited task reads as something a
    /// user recognises instead of an internal state machine type name.
    /// </summary>
    public static string Describe(object value)
    {
        var task = value as Task;
        var resultType = FindAwaitedResultType(value.GetType());
        var name = resultType is null ? "Task" : $"Task<{FriendlyName(resultType)}>";

        if (task is null)
        {
            return name;
        }

        var status = task.Status switch
        {
            TaskStatus.RanToCompletion => "completed",
            TaskStatus.Faulted => "faulted",
            TaskStatus.Canceled => "canceled",
            _ => "pending",
        };

        return $"{name} ({status})";
    }

    private static bool IsValueTaskOfT(object? value) =>
        value is not null &&
        value.GetType() is { IsGenericType: true } type &&
        type.GetGenericTypeDefinition() == typeof(ValueTask<>);

    private static Task AsTaskViaReflection(object value)
    {
        var asTask = value.GetType().GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance);

        if (asTask?.Invoke(value, null) is Task converted)
        {
            return converted;
        }

        throw new InvalidOperationException(
            $"'{value.GetType().FullName}' is not an awaitable this runtime understands.");
    }

    /// <summary>
    /// The declared result type of a task, or <see langword="null"/> when it produces
    /// no value. Walks the base chain because the runtime type is a state machine box
    /// deriving from <c>Task&lt;T&gt;</c>, not <c>Task&lt;T&gt;</c> itself.
    /// </summary>
    private static Type? FindAwaitedResultType(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (!current.IsGenericType)
            {
                continue;
            }

            var definition = current.GetGenericTypeDefinition();

            if (definition != typeof(Task<>) && definition != typeof(ValueTask<>))
            {
                continue;
            }

            var argument = current.GetGenericArguments()[0];

            // `Task` is implemented as Task<VoidTaskResult>; that is not a value.
            return string.Equals(argument.FullName, VoidResultTypeName, StringComparison.Ordinal)
                ? null
                : argument;
        }

        return null;
    }

    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var name = type.Name;
        var tick = name.IndexOf('`');

        if (tick >= 0)
        {
            name = name[..tick];
        }

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyName))}>";
    }
}
