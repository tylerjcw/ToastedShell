using System.Reflection;

using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[Stdlib(StdlibCategory.Clr)]
[CommandCategory("CLR")]
[CommandArgument("object", "The target object. If omitted, reads from the pipeline.", Required = false)]
[CommandArgument("name", "The property name to check.")]
[CommandExample("$obj | has-prop Name", Title = "Check if a piped object has a property")]
[CommandExample("has-prop $obj Name", Title = "Check by passing the object directly")]
[CommandOutput("A boolean: true if the property exists, false otherwise.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Uses the piped object as the target when the object argument is omitted.")]
public sealed class HasPropCommand : ShellCommand
{
    public HasPropCommand()
        : base("has-prop", "Checks whether an object has the named property.", "has-prop [object] <name>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (target, name) = await ResolveTargetAndName(context);

        if (target is null)
        {
            yield return false;
            yield break;
        }

        // Check table/dictionary values first
        if (ShellRecordUtilities.TryGetFields(target, out var fields))
        {
            yield return fields.Any(field => string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase));
            yield break;
        }

        // Check CLR properties and fields
        var type = target.GetType();
        var hasAdaptedMember = ObjectMemberAdapter.TryGetMember(type, name, out _);
        var hasProperty = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not null;
        var hasField = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) is not null;

        yield return hasAdaptedMember || hasProperty || hasField;
    }

    private static async Task<(object? Target, string Name)> ResolveTargetAndName(CommandContext context)
    {
        if (context.Arguments.Count >= 2)
        {
            return (context.Arguments[0], context.Arguments[1]?.ToString() ?? string.Empty);
        }

        if (context.Arguments.Count == 1)
        {
            var items = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
            return (items.Count > 0 ? items[0] : null, context.Arguments[0]?.ToString() ?? string.Empty);
        }

        throw new InvalidOperationException("has-prop requires a property name. Usage: has-prop [object] <name>");
    }
}
