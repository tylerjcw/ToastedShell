using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A script function assigned where a CLR delegate is wanted becomes one.
/// </summary>
/// <remarks>
/// <para>
/// This is the gap that made event handlers impossible from a script: a widget raises
/// <c>Action&lt;string&gt;</c>, and a lambda is not one, so assigning it failed with
/// "Cannot assign value of type 'ToshLambda' to member 'Changed'".
/// </para>
/// <para>
/// Tested against the bridge rather than through a script, because a bare runtime resolves
/// CLR types out of a curated index that a test assembly's own types are not in. What the
/// script path adds beyond this is the engine building the context, which
/// <see cref="ToshEngine.InvokeCallableOnThisThread"/> does the same way the tick and
/// binding invokers already do.
/// </para>
/// </remarks>
public sealed class ShellCallableDelegateTests
{
    [Fact]
    public void A_callable_becomes_the_delegate_a_property_asks_for()
    {
        var seen = new List<object?>();
        var callable = new Callable(arguments =>
        {
            seen.AddRange(arguments);
            return null;
        });

        Assert.True(TypeConversion.TryConvert(callable, typeof(Action<string>), out var converted));

        Assert.IsType<Action<string>>(converted).Invoke("typed");
        Assert.Equal(["typed"], seen);
    }

    [Fact]
    public void A_returning_delegate_converts_what_the_callable_produced()
    {
        // A handler answering `4` for a `Func<string, double>` has not made a mistake.
        var callable = new Callable(arguments => ((string)arguments[0]!).Length);

        Assert.True(ShellCallableDelegates.TryCreate(callable, typeof(Func<string, double>), out var created));

        Assert.Equal(4d, Assert.IsType<Func<string, double>>(created).Invoke("four"));
    }

    [Fact]
    public void A_handler_that_ignores_its_arguments_need_not_declare_them()
    {
        // The same courtesy the pull bindings extend: a form's submit handler usually reads
        // widgets it already holds and has no use for the sender it is passed.
        var count = -1;
        var callable = new Callable(arguments => count = arguments.Count) { Maximum = 0 };

        Assert.True(ShellCallableDelegates.TryCreate(callable, typeof(Action<string, int>), out var created));

        Assert.IsType<Action<string, int>>(created).Invoke("ignored", 7);
        Assert.Equal(0, count);
    }

    [Fact]
    public void A_signature_that_cannot_be_expressed_as_objects_is_refused()
    {
        // A ref struct cannot be boxed into the `object?[]` the callable is handed. Saying
        // so at assignment is better than failing when the event eventually fires.
        Assert.False(ShellCallableDelegates.TryCreate(new Callable(_ => null), typeof(SpanHandler), out _));

        // `Delegate` names the family rather than a signature: there is nothing to conform to.
        Assert.False(ShellCallableDelegates.TryCreate(new Callable(_ => null), typeof(Delegate), out _));
    }

    [Fact]
    public void A_value_that_cannot_host_itself_is_not_a_delegate()
    {
        Assert.False(ShellCallableDelegates.TryCreate("text", typeof(Action), out _));
        Assert.False(TypeConversion.TryConvert("text", typeof(Action), out _));
    }

    [Fact]
    public void A_handler_that_answers_with_nothing_usable_says_so_where_it_was_called()
    {
        var callable = new Callable(_ => "not a number");

        Assert.True(ShellCallableDelegates.TryCreate(callable, typeof(Func<int>), out var created));

        var error = Assert.Throws<InvalidOperationException>(() => Assert.IsType<Func<int>>(created).Invoke());
        Assert.Contains("Int32", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bound method reference can cross to the platform, exactly as a lambda can.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>&amp;$obj.Method</c> could be passed everywhere inside the language and nowhere
    /// outside it. Handing it to <c>List&lt;T&gt;.Sort</c> failed overload resolution while
    /// the lambda beside it — same call, same signature, same arguments — sorted the list.
    /// </para>
    /// <para>
    /// The difference was never the signature: a lambda could run itself without a context
    /// and a bound reference could not, and only a callable that can do that is convertible
    /// to a delegate. Taking a reference in order to hand it somewhere else is the point of
    /// taking one, and "somewhere else" includes a CLR API.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_bound_method_reference_converts_to_a_delegate_like_a_lambda()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            class Helper { func ByLen(a, b) { return $a.Length - $b.Length } }
            var h = new Helper()

            # Built from the sequence rather than with Add, because a bare void call emits
            # its receiver and three Lists would land in the results.
            var viaLambda = new System.Collections.Generic.List<string>(["bbb", "a", "cc"])
            $viaLambda.Sort(func(a, b) => ($a.Length - $b.Length))
            echo $"{$viaLambda[0]},{$viaLambda[1]},{$viaLambda[2]}"

            var viaReference = new System.Collections.Generic.List<string>(["bbb", "a", "cc"])
            $viaReference.Sort(&$h.ByLen)
            echo $"{$viaReference[0]},{$viaReference[1]},{$viaReference[2]}"
            """);

        // The echoed lines only: `Sort` returns void, and a bare void call emits its
        // receiver, so the two Lists are in the results too.
        Assert.Equal(["a,cc,bbb", "a,cc,bbb"], results.OfType<string>());
    }

    private delegate void SpanHandler(ReadOnlySpan<char> text);

    private sealed class Callable(Func<IReadOnlyList<object?>, object?> body) : IShellCallable, ISelfHostedCallable
    {
        public string CallableName => "fake";

        public int RequiredParameterCount => 0;

        public int? MaximumParameterCount => Maximum;

        public int? Maximum { get; init; }

        public IAsyncEnumerable<object?> InvokeAsync(CommandContext context)
            => throw new NotSupportedException("This fake is only ever invoked without a context.");

        public object? InvokeWithoutContext(IReadOnlyList<object?> arguments) => body(arguments);
    }
}
