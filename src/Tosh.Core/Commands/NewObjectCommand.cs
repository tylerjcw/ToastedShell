namespace Tosh.Core.Commands;

[CommandCategory("CLR")]
public sealed class NewObjectCommand : ShellCommand
{
    public NewObjectCommand()
        : base("new", "Constructs a new CLR object, ToSh named type, or shell collection.", "new <type-name> [ctor-args...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsedTypeName = CommandArguments.RequireParsedTypeName(context.Arguments, 0, "type name");
        var typeName = parsedTypeName.TypeName;
        var arguments = CommandArguments.Slice(context.Arguments, parsedTypeName.ConsumedArgumentCount);

        if (BuiltInShellTypes.TryResolveStaticType(typeName, context.TypeResolver, out var shellType))
        {
            yield return context.Runtime.Invoker.CreateInstance(shellType, arguments);
            yield break;
        }

        var type = context.TypeResolver.Resolve(typeName)
                   ?? throw new InvalidOperationException($"Unable to resolve type '{typeName}'.");

        yield return context.Runtime.Invoker.CreateInstance(type, arguments);
    }
}
