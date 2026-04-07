namespace Tosh.Core.Commands;

[CommandCategory("Functional")]
public sealed class InvokeCommand : ShellCommand
{
    public InvokeCommand()
        : base("invoke", "Invokes a callable value such as a lambda or function object.", "invoke <callable> [arg ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::invoke_requires_callable",
                title: "The 'invoke' command requires a callable value.",
                label: "pass a lambda or callable object as the first argument");
        }

        if (context.Arguments[0] is not IShellCallable callable)
        {
            throw context.CreateDiagnostic(
                code: "tosh::runtime::value_not_callable",
                title: "The provided value is not callable.",
                argumentIndex: 0,
                label: "this value cannot be invoked",
                help: "pass a lambda like 'func(x) => ...' or another callable shell value.");
        }

        var invokeContext = context with
        {
            Arguments = CommandArguments.Slice(context.Arguments, 1),
            Input = AsyncEnumerableExtensions.Empty<object?>(),
            IsPipelined = false,
        };

        await foreach (var value in callable.InvokeAsync(invokeContext).WithCancellation(context.CancellationToken))
        {
            yield return value;
        }
    }
}
