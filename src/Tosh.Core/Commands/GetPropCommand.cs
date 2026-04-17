using System.Reflection;

namespace Tosh.Core.Commands;

[CommandCategory("CLR")]
[CommandArgument("object", "The target object. If omitted, reads from the pipeline.", Required = false)]
[CommandArgument("name", "The property name to retrieve.")]
[CommandExample("$obj | get-prop $propName", Title = "Get a dynamically-named property from a piped object")]
[CommandExample("get-prop $obj Name", Title = "Get a property by passing the object as an argument")]
[CommandOutput("The value of the named property.")]
[PipelineInput(AcceptsScalar = true, AcceptsRecord = true, Description = "Uses the piped object as the target when the object argument is omitted.")]
public sealed class GetPropCommand : ShellCommand
{
    public GetPropCommand()
        : base("get-prop", "Gets a property value by dynamic name.", "get-prop [object] <name>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (target, name) = await ResolveArguments(context);

        if (target is null)
        {
            yield return null;
            yield break;
        }

        // Check table/dictionary values first
        if (ShellRecordUtilities.TryGetValue(target, name, out var recordValue))
        {
            yield return recordValue;
            yield break;
        }

        if (ObjectMemberAdapter.TryGetValue(target, name, out var adaptedValue))
        {
            yield return adaptedValue;
            yield break;
        }

        // CLR property
        var type = target.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (property is not null)
        {
            yield return property.GetValue(target);
            yield break;
        }

        // CLR field
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (field is not null)
        {
            yield return field.GetValue(target);
            yield break;
        }

        throw new InvalidOperationException($"Property '{name}' was not found on {type.Name}.");
    }

    private static async Task<(object? Target, string Name)> ResolveArguments(CommandContext context)
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

        throw new InvalidOperationException("get-prop requires a property name. Usage: get-prop [object] <name>");
    }
}
