using System.Globalization;

using Tosh.Runtime;

namespace Tosh.Stdlib.Sys;

[Stdlib(StdlibCategory.Sys)]
[CommandCategory("System")]
[CommandArgument("stop", "Final value when one argument is provided.")]
[CommandArgument("start", "Initial value when two or three arguments are provided.", Required = false)]
[CommandArgument("step", "Step size when three arguments are provided. Cannot be zero.", Required = false)]
[CommandExample("seq 5", Title = "Generate 1 through 5")]
[CommandExample("seq 0 2 10", Title = "Generate an stepped sequence")]
[CommandExample("seq 5 -1 1", Title = "Generate a descending sequence")]
[CommandOutput("An int (or numeric) sequence — one value per element of the requested range.")]
public sealed class SeqCommand : ShellCommand
{
    public SeqCommand()
        : base("seq", "Generates a numeric sequence.", "seq <stop> | seq <start> <stop> | seq <start> <step> <stop>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var values = context.Arguments
            .Select(argument => ParseDecimal(argument?.ToString()))
            .ToArray();

        if (values.Length is < 1 or > 3)
        {
            throw new InvalidOperationException("seq expects 1, 2, or 3 numeric arguments.");
        }

        var (start, step, stop) = values.Length switch
        {
            1 => (1m, 1m, values[0]),
            2 => (values[0], 1m, values[1]),
            _ => (values[0], values[1], values[2]),
        };

        if (step == 0)
        {
            throw new InvalidOperationException("seq step cannot be zero.");
        }

        if (step > 0)
        {
            for (var current = start; current <= stop; current += step)
            {
                yield return ToNumber(current);
            }

            yield break;
        }

        for (var current = start; current >= stop; current += step)
        {
            yield return ToNumber(current);
        }
    }

    private static decimal ParseDecimal(string? text)
    {
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new InvalidOperationException($"'{text}' is not a valid number.");
        }

        return value;
    }

    private static object ToNumber(decimal value)
    {
        return decimal.Truncate(value) == value &&
               value >= long.MinValue &&
               value <= long.MaxValue
            ? (object)(long)value
            : value;
    }
}
