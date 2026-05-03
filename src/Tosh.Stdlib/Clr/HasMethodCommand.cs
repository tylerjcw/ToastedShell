using System.Reflection;

using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandExample("$obj | has-method ToString")]
[CommandExample("has-method $obj ToString")]
[CommandOutput("A bool — true when the target type/object exposes a method with the given name.", ClrType = typeof(bool))]
public sealed class HasMethodCommand : ShellCommand
{
    public HasMethodCommand()
        : base("has-method", "Checks whether an object or ToSh class has the named method.", "has-method [object] <name>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (target, name) = await ResolveTargetAndName(context);

        if (target is null)
        {
            yield return false;
            yield break;
        }

        if (target is IShellTypedObject typed)
        {
            yield return typed.ShellTypeDescriptor.GetShellMethods()
                .Any(method => !method.IsStatic &&
                               string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase));
            yield break;
        }

        if (target is IShellTypeDescriptor descriptor)
        {
            yield return descriptor.GetShellMethods()
                .Any(method => method.IsStatic &&
                               string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase));
            yield break;
        }

        var type = target.GetType();
        var hasMethod = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Any(method => !method.IsSpecialName &&
                           string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase));

        yield return hasMethod;
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

        throw new InvalidOperationException("has-method requires a method name. Usage: has-method [object] <name>");
    }
}
