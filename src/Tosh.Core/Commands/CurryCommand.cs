namespace Tosh.Core.Commands;

[CommandCategory("Functional")]
public sealed class CurryCommand : ShellCommand
{
    public CurryCommand()
        : base("curry", "Converts a fixed-arity callable into a curried callable value.", "curry <callable>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        if (context.Arguments.Count != 1)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::curry_requires_callable",
                title: "The 'curry' command requires exactly one callable value.",
                label: "pass a lambda or function as the only argument");
        }

        if (context.Arguments[0] is not IShellCallable callable)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::value_not_callable",
                title: "The provided value is not callable.",
                argumentIndex: 0,
                label: "this value cannot be curried",
                help: "pass a lambda like 'func(x) => ...' or another callable shell value.");
        }

        if (callable.MaximumParameterCount is not int maximum || maximum != callable.RequiredParameterCount)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::curry_requires_fixed_arity_callable",
                title: "The 'curry' command currently requires a fixed-arity callable.",
                argumentIndex: 0,
                label: "this callable has optional or variadic parameters",
                help: "use 'partial' for optional/rest-parameter callables, or curry a lambda/function with a fixed parameter count.");
        }

        if (maximum == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::curry_requires_nonzero_arity",
                title: "The 'curry' command requires a callable that accepts at least one argument.",
                argumentIndex: 0,
                label: "this callable does not accept any arguments");
        }

        yield return new CurriedShellCallable(callable, Array.Empty<object?>(), maximum);
    }
}
