using System.Reflection;

namespace Tosh.Core.Commands;

[CommandCategory("CLR")]
[CommandExample("$obj | get-props")]
[CommandExample("get-props $obj")]
public sealed class GetPropsCommand : ShellCommand
{
    public GetPropsCommand()
        : base("get-props", "Lists property names for an object.", "get-props [object]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var target = await ResolveTarget(context);

        if (target is null)
        {
            yield break;
        }

        // Check table/dictionary values first
        if (ShellRecordUtilities.TryGetFields(target, out var fields))
        {
            foreach (var field in fields)
            {
                yield return field.Key;
            }

            yield break;
        }

        // CLR properties and fields
        var type = target.GetType();
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0)
                     .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (yielded.Add(property.Name))
            {
                yield return property.Name;
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (yielded.Add(field.Name))
            {
                yield return field.Name;
            }
        }

        foreach (var memberName in ObjectMemberAdapter.GetMemberNames(type))
        {
            if (yielded.Add(memberName))
            {
                yield return memberName;
            }
        }
    }

    private static async Task<object?> ResolveTarget(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            return context.Arguments[0];
        }

        var items = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);
        return items.Count > 0 ? items[0] : null;
    }
}
