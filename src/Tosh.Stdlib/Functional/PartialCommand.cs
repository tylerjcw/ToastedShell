using Tosh.Runtime;

namespace Tosh.Stdlib.Functional;

[CommandCategory("Functional")]
[CommandArgument("callable", "A callable value to partially apply.")]
[CommandArgument("arg ...", "Leading positional arguments to bind ahead of time.", Required = false)]
[CommandExample("var add = func(x, y) => ($x + $y); var inc = partial $add 1; invoke $inc 41", Title = "Bind the first argument of a callable")]
[CommandExample("invoke (partial (func(a, b, c) => ($\"{$a}-{$b}-{$c}\")) alpha beta) gamma", Title = "Bind multiple leading arguments")]
[CommandOutput("Returns a callable value that prepends the bound arguments before invoking the original callable.")]
[PipelineInput(Description = "`partial` is explicit-argument based and returns a new callable value.")]
public sealed class PartialCommand : ShellCommand
{
    public PartialCommand()
        : base("partial", "Binds leading arguments to a callable and returns a new callable value.", "partial <callable> [arg ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await Task.CompletedTask;

        if (context.Arguments.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.partial_requires_callable",
                title: "The 'partial' command requires a callable value.",
                label: "pass a lambda or function as the first argument");
        }

        if (context.Arguments[0] is not IShellCallable callable)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.value_not_callable",
                title: "The provided value is not callable.",
                argumentIndex: 0,
                label: "this value cannot be partially applied",
                help: "pass a lambda like 'func(x) => ...' or another callable shell value.");
        }

        var boundArguments = CommandArguments.Slice(context.Arguments, 1);

        if (callable.MaximumParameterCount is int maximum && boundArguments.Count > maximum)
        {
            throw context.CreateDiagnostic(
                code: "tosh.runtime.partial_argument_count_mismatch",
                title: $"Callable '{callable.CallableName}' accepts at most {maximum} argument(s) but received {boundArguments.Count}.",
                label: "too many arguments were supplied for partial application");
        }

        yield return new PartialShellCallable(callable, boundArguments);
    }
}
