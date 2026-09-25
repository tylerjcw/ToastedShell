using Tosh.Runtime.Formats;
using Tosh.Runtime.Units;
using Tosh.Runtime;

namespace Tosh.Stdlib.Data;

[CommandCategory("Data")]
[CommandOutput("Serialized text in the specified format, or converted physical quantity.", Mode = "text")]
[CommandExample("ls | to json")]
[CommandExample("ls | to csv")]
[CommandExample("ls | to toml")]
[CommandExample("10km | to miles")]
[CommandExample("100degC | to degF")]
[CommandNote("The `to` command converts between text formats (json, csv, toml) and serializes CLR objects, or converts physical quantities to target units (e.g. `10km | to miles`).")]
public sealed class ToCommand : ShellCommand
{
    private readonly DataFormatRegistry _formats;

    public ToCommand(DataFormatRegistry formats, string name = "to")
        : base(name, "Serializes objects into structured text or converts physical quantities to another unit.", "to <format-or-unit> [options]")
    {
        _formats = formats;
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException(
                $"Usage: to <format-or-unit> [options]\nAvailable formats: {string.Join(", ", _formats.GetAll().Select(f => f.Name))}.");
        }

        var formatName = context.Arguments[0]?.ToString()
            ?? throw new InvalidOperationException("Format or unit name is required.");

        var remainingArgs = context.Arguments.Skip(1).ToArray();

        IReadOnlyList<object?> values =
            await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (values.Count == 0)
        {
            if (TryTakeArgumentValues(remainingArgs, out var argumentValues))
            {
                values = argumentValues;
                remainingArgs = [];
            }
            else
            {
                throw context.CreateDiagnostic(
                    code: "tosh.runtime.to_requires_input",
                    title: $"'to {formatName}' has nothing to serialize or convert.",
                    label: "no pipeline input reached this command",
                    help: $"pipe the value in: `$value | to {formatName}`, or pass it as the "
                        + $"only argument: `to {formatName} $value`.");
            }
        }

        if (!_formats.TryResolve(formatName, out var format))
        {
            // Check if this is a physical unit conversion: e.g. `$dist | to miles` or `10km | to m`
            if (values.Any(v => v is Quantity) ||
                UnitRegistry.Instance.TryResolve(formatName) != null)
            {
                foreach (var val in values)
                {
                    if (val is Quantity qty)
                    {
                        yield return qty.To(formatName);
                    }
                    else
                    {
                        yield return val;
                    }
                }
                yield break;
            }

            // Not a unit, let Resolve throw the diagnostic listing available formats
            format = _formats.Resolve(formatName);
        }

        var serialized = format is IContextualDataFormat contextual
            ? contextual.SerializeAsync(values, remainingArgs, context)
            : format.SerializeAsync(values, remainingArgs);

        await foreach (var value in serialized)
        {
            yield return value;
        }
    }

    /// <summary>
    /// Treats the command's arguments as the values to serialize, when they can only be data.
    /// </summary>
    private static bool TryTakeArgumentValues(
        IReadOnlyList<object?> arguments,
        out IReadOnlyList<object?> values)
    {
        values = [];

        if (arguments.Count == 0)
        {
            return false;
        }

        foreach (var argument in arguments)
        {
            if (argument is string text && text.StartsWith('-'))
            {
                return false;
            }
        }

        values = arguments;
        return true;
    }
}
