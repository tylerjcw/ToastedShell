namespace Tosh.Core.Commands;

public sealed class NewObjectCommand : ShellCommand
{
    public NewObjectCommand()
        : base("new", "Constructs a new CLR object.", "new <type-name> [ctor-args...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var typeName = CommandArguments.RequireString(context.Arguments, 0, "type name");
        var type = context.Runtime.TypeResolver.Resolve(typeName)
                   ?? throw new InvalidOperationException($"Unable to resolve type '{typeName}'.");

        yield return context.Runtime.Invoker.CreateInstance(type, CommandArguments.Slice(context.Arguments, 1));
    }
}
