using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandDeprecated("26.05.0.10")]
[CommandNote("Deprecated. Prefer member-access syntax: `$obj.Method($args)`.")]
[CommandExample("echo hello | call-method ToUpper")]
[CommandExample("call-method $obj MethodName arg1")]
[CommandOutput("Streams whatever the invoked method returns (single value, an enumeration of values, or nothing for void methods).")]
public sealed class CallMethodCommand : ShellCommand
{
    public CallMethodCommand()
        : base("call-method", "Invokes a method by dynamic name.", "call-method [object] <method-name> [args...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (target, methodName, methodArgs) = await ResolveArguments(context);

        if (target is null)
        {
            throw new InvalidOperationException("call-method requires an object. Usage: call-method [object] <method-name> [args...]");
        }

        if (target is ShellTextLine textLine)
        {
            target = textLine.Text;
        }

        var invocation = await context.Runtime.Invoker.InvokeInstanceMethodAsync(
            target,
            methodName,
            methodArgs,
            context.CancellationToken);

        if (!invocation.ReturnedVoid)
        {
            yield return invocation.Value;
        }
    }

    private static async Task<(object? Target, string MethodName, object?[] Args)> ResolveArguments(CommandContext context)
    {
        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);
        var hasInput = await enumerator.MoveNextAsync();

        if (context.Arguments.Count >= 2 && !hasInput)
        {
            // call-method $obj MethodName arg1 arg2
            var target = context.Arguments[0];
            var methodName = context.Arguments[1]?.ToString() ?? string.Empty;
            var args = context.Arguments.Skip(2).ToArray();
            return (target, methodName, args);
        }

        if (context.Arguments.Count >= 1 && hasInput)
        {
            // $obj | call-method MethodName arg1 arg2
            var target = enumerator.Current;
            var methodName = context.Arguments[0]?.ToString() ?? string.Empty;
            var args = context.Arguments.Skip(1).ToArray();
            return (target, methodName, args);
        }

        throw new InvalidOperationException("call-method requires an object and method name. Usage: call-method [object] <method-name> [args...]");
    }
}
