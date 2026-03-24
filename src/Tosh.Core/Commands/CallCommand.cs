namespace Tosh.Core.Commands;

public sealed class CallCommand : ShellCommand
{
    public CallCommand()
        : base("call", "Invokes an instance or static CLR method.", "call <method-name> [args...] or call <type-name> <method-name> [args...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (await enumerator.MoveNextAsync())
        {
            var methodName = CommandArguments.RequireString(context.Arguments, 0, "method name");
            var methodArguments = CommandArguments.Slice(context.Arguments, 1);

            do
            {
                if (enumerator.Current is null)
                {
                    throw new InvalidOperationException("Cannot invoke an instance method on null.");
                }

                var instanceInvocation = context.Runtime.Invoker.InvokeInstance(enumerator.Current, methodName, methodArguments);
                yield return instanceInvocation.ReturnedVoid ? enumerator.Current : instanceInvocation.Value;
            }
            while (await enumerator.MoveNextAsync());

            yield break;
        }

        var typeName = CommandArguments.RequireString(context.Arguments, 0, "type name");
        var method = CommandArguments.RequireString(context.Arguments, 1, "method name");
        var type = context.Runtime.TypeResolver.Resolve(typeName)
                   ?? throw new InvalidOperationException($"Unable to resolve type '{typeName}'.");
        var staticInvocation = context.Runtime.Invoker.InvokeStatic(type, method, CommandArguments.Slice(context.Arguments, 2));

        if (!staticInvocation.ReturnedVoid)
        {
            yield return staticInvocation.Value;
        }
    }
}
