namespace Tosh.Core.Commands;

[CommandCategory("CLR")]
[CommandArgument("type-name", "The CLR type name, ToSh named type, or shell collection type to construct.")]
[CommandArgument("ctor-args", "Arguments to pass to the constructor.", Required = false)]
[CommandExample("var rng = new System.Random()", Title = "Construct a Random instance")]
[CommandExample("var items = new list<String>(\"one\", \"two\")", Title = "Generic collection construction")]
[CommandExample("new System.Text.StringBuilder(\"hello\").Append(\" world\").ToString()", Title = "Method chaining on a new object")]
[CommandNote("Tosh supports both the legacy `new <Type> ...` command form and the newer C#-style `new Type(...)` expression syntax. Shell collection types also support generic construction like `new list<String>(...)`.")]
[CommandOutput("The newly constructed object.")]
public sealed class NewObjectCommand : ShellCommand
{
    public NewObjectCommand()
        : base("new", "Constructs a new CLR object, ToSh named type, or shell collection.", "new <type-name> [ctor-args...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsedTypeName = CommandArguments.RequireParsedTypeName(context.Arguments, 0, "type name");
        var typeName = parsedTypeName.TypeName;
        var arguments = CommandArguments.Slice(context.Arguments, parsedTypeName.ConsumedArgumentCount);

        if (context.Runtime.Classes.TryGetValue(typeName, out var runtimeType) &&
            runtimeType is IShellStaticType runtimeShellType)
        {
            yield return context.Runtime.Invoker.CreateInstance(runtimeShellType, arguments);
            yield break;
        }

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
