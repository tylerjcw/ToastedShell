using System.Reflection;

using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandExample("$obj | get-methods")]
[CommandExample("get-methods $obj")]
[CommandOutput("Records describing each method: name, return type, parameter list, and arity flags.")]
public sealed class GetMethodsCommand : ShellCommand
{
    public GetMethodsCommand()
        : base("get-methods", "Lists method names for an object or ToSh class.", "get-methods [object]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var target = await ResolveTarget(context);

        if (target is null)
        {
            yield break;
        }

        if (target is IShellTypedObject typed)
        {
            foreach (var name in typed.ShellTypeDescriptor.GetShellMethods()
                         .Where(method => !method.IsStatic)
                         .Select(method => method.Name)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                yield return name;
            }

            yield break;
        }

        if (target is IShellTypeDescriptor descriptor)
        {
            foreach (var name in descriptor.GetShellMethods()
                         .Where(method => method.IsStatic)
                         .Select(method => method.Name)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                yield return name;
            }

            yield break;
        }

        var type = target.GetType();

        foreach (var name in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(m => !m.IsSpecialName)
                     .Select(m => m.Name)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            yield return name;
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
