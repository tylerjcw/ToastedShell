using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[Stdlib(StdlibCategory.Clr)]
[CommandCategory("CLR")]
[CommandArgument("object", "The target object. If omitted, reads from the pipeline.", Required = false)]
[CommandArgument("name", "The property name to set.")]
[CommandArgument("value", "The value to assign.")]
[CommandExample("$obj | set-prop Name \"value\"", Title = "Set a property on a piped object")]
[CommandExample("set-prop $obj Name \"value\"", Title = "Set a property by passing the object directly")]
[CommandOutput("The modified object with the property set.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Uses the piped object as the target when the object argument is omitted.")]
public sealed class SetPropCommand : ShellCommand
{
    public SetPropCommand()
        : base("set-prop", "Sets or adds a property on a dynamic record.", "set-prop [object] <name> <value>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (target, name, value) = await ResolveArguments(context);

        if (target is null)
        {
            throw new InvalidOperationException("set-prop requires an object. Usage: set-prop [object] <name> <value>");
        }

        if (ShellRecordUtilities.TrySetValue(target, name, value))
        {
            yield return target;
            yield break;
        }

        // Try CLR property via reflection
        var type = target.GetType();
        var property = type.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);

        if (property is not null && property.CanWrite)
        {
            var converted = TypeConversion.TryConvert(value, property.PropertyType, out var result) ? result : value;
            property.SetValue(target, converted);
            yield return target;
            yield break;
        }

        throw new InvalidOperationException($"Cannot set property '{name}' on {type.Name}. The object must be a dynamic record or have a writable CLR property.");
    }

    private static async Task<(object? Target, string Name, object? Value)> ResolveArguments(CommandContext context)
    {
        if (context.Arguments.Count >= 3)
        {
            return (context.Arguments[0], context.Arguments[1]?.ToString() ?? string.Empty, context.Arguments[2]);
        }

        if (context.Arguments.Count == 2)
        {
            var items = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
            return (items.Count > 0 ? items[0] : null, context.Arguments[0]?.ToString() ?? string.Empty, context.Arguments[1]);
        }

        throw new InvalidOperationException("set-prop requires a property name and value. Usage: set-prop [object] <name> <value>");
    }
}
