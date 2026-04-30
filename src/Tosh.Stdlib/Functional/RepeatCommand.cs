using Tosh.Runtime;

namespace Tosh.Stdlib.Functional;

[Stdlib(StdlibCategory.Functional)]
[CommandCategory("Functional")]
[CommandArgument("value", "The value to repeat infinitely.")]
[CommandArgument("count", "Optional maximum number of repetitions.", Required = false)]
[CommandExample("repeat 0 | first 5", Title = "Five zeros")]
[CommandExample("repeat hello 3", Title = "Three hellos")]
[CommandNote("Without a count, produces an infinite sequence. Pair with `first`, `take-while`, or `take-until` to bound.")]
[CommandOutput("The same value repeated.")]
public sealed class RepeatCommand : ShellCommand
{
    public RepeatCommand()
        : base("repeat", "Produces a sequence of the same value repeated. Without a count argument, repeats infinitely.", "repeat <value> [count]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count < 1 || context.Arguments.Count > 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.repeat_requires_value",
                title: "'repeat' requires a value and an optional count.",
                label: "use 'repeat <value> [count]'");
        }

        var value = context.Arguments[0];

        if (context.Arguments.Count == 2)
        {
            var countArg = context.Arguments[1];
            var count = Convert.ToInt32(countArg);
            if (count < 0)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.repeat_negative_count",
                    title: "Repeat count cannot be negative.",
                    argumentIndex: 1,
                    label: "must be >= 0");
            }

            for (var i = 0; i < count; i++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return value;
            }
        }
        else
        {
            while (true)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return value;
            }
        }

        await Task.CompletedTask;
    }
}
