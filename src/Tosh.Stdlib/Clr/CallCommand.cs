using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[Stdlib(StdlibCategory.Clr)]
[CommandCategory("CLR")]
[CommandArgument("method-name", "The method to invoke (or a type name for static calls).")]
[CommandArgument("args", "Arguments to pass to the method.", Required = false)]
[CommandExample("\"hello\" | call ToUpper", Title = "Call an instance method on a piped string")]
[CommandExample("call System.Math Sqrt 144", Title = "Call a static method")]
[CommandNote("Prefer the fluent `$obj.Method()` expression syntax for instance calls and `TypeName.Method()` for static calls. The `call` command form is a legacy fallback.")]
[CommandOutput("The return value of the invoked method.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Uses the piped object as the instance for method invocation.")]
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

        var parsedTypeName = CommandArguments.RequireParsedTypeName(context.Arguments, 0, "type name");
        var typeName = parsedTypeName.TypeName;
        var method = CommandArguments.RequireString(context.Arguments, parsedTypeName.ConsumedArgumentCount, "method name");
        var type = context.TypeResolver.Resolve(typeName)
                   ?? throw new InvalidOperationException($"Unable to resolve type '{typeName}'.");
        var staticInvocation = context.Runtime.Invoker.InvokeStatic(type, method, CommandArguments.Slice(context.Arguments, parsedTypeName.ConsumedArgumentCount + 1));

        if (!staticInvocation.ReturnedVoid)
        {
            yield return staticInvocation.Value;
        }
    }
}
