using Tosh.Runtime;

namespace Tosh.Stdlib.Functional;

[CommandCategory("Functional")]
[CommandArgument("callable1", "The first callable in the chain.")]
[CommandArgument("callable2", "The second callable in the chain.")]
[CommandArgument("callable", "Additional callables to chain.", Required = false)]
[CommandExample("compose func(x) => ($x + 1) func(x) => ($x * 2)", Title = "Compose increment and double")]
[CommandExample("$f = compose $parse $validate $transform; $f $input", Title = "Build a processing pipeline")]
[CommandOutput("A single callable that applies all given callables in left-to-right order.")]
public sealed class ComposeCommand : ShellCommand
{
    public ComposeCommand()
        : base("compose", "Composes two or more callables into a single callable that chains them left-to-right.", "compose <callable1> <callable2> [callable ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        if (context.Arguments.Count < 2)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.compose_requires_two_callables",
                title: "The 'compose' command requires at least two callable values.",
                label: "pass two or more lambdas or functions to compose");
        }

        var callables = new List<IShellCallable>(context.Arguments.Count);

        for (var i = 0; i < context.Arguments.Count; i++)
        {
            if (context.Arguments[i] is not IShellCallable callable)
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.compose_requires_callable",
                    title: $"Argument {i + 1} is not callable.",
                    argumentIndex: i,
                    label: "this value cannot be composed",
                    help: "pass a lambda like 'func(x) => ...' or a function reference like '&name'.");
            }

            callables.Add(callable);
        }

        yield return new ComposedShellCallable(callables);
    }
}
