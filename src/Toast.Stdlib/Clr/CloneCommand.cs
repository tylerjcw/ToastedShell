using System.Dynamic;

using Tosh.Runtime;

namespace Tosh.Stdlib.Clr;

[CommandCategory("CLR")]
[CommandExample("$obj | clone")]
[CommandExample("clone $obj")]
[CommandOutput("A shallow copy of each input/object: the same record/dictionary/list shape with copied top-level entries.")]
public sealed class CloneCommand : ShellCommand
{
    public CloneCommand()
        : base("clone", "Creates a shallow copy of an object.", "clone [object]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count > 0)
        {
            foreach (var argument in context.Arguments)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                yield return ShallowClone(argument);
            }

            yield break;
        }

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            yield return ShallowClone(item);
        }
    }

    private static object? ShallowClone(object? value)
    {
        if (value is null)
        {
            return null;
        }

        // Dynamic records — create a new ExpandoObject with copied fields
        if (value is IDictionary<string, object?> dictionary)
        {
            IDictionary<string, object?> clone = new ExpandoObject();

            foreach (var (key, val) in dictionary)
            {
                clone[key] = val;
            }

            return clone;
        }

        // ICloneable CLR objects
        if (value is ICloneable cloneable)
        {
            return cloneable.Clone();
        }

        // Value types are already copies
        if (value.GetType().IsValueType)
        {
            return value;
        }

        throw new InvalidOperationException($"Cannot clone {value.GetType().Name}. The object must be a table/dictionary value, implement ICloneable, or be a value type.");
    }
}
