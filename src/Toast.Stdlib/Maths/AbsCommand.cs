using System.Numerics;
using Tosh.Runtime;
using Tosh.Runtime.Units;

namespace Tosh.Stdlib.Maths;

[CommandCategory("Math")]
[CommandArgument("value", "The value to compute the absolute value of.", Required = false)]
[CommandExample("abs -42", Title = "Absolute value of an integer")]
[CommandExample("abs -3.14", Title = "Absolute value of a floating-point number")]
[CommandExample("echo -15 | abs", Title = "Absolute value from pipeline")]
[CommandOutput("The absolute value.", ClrType = typeof(IAsyncEnumerable<object>))]
[PipelineInput(AcceptsScalar = true, Description = "Accepts numbers, quantities, or complex values to compute absolute values of.")]
public sealed class AbsCommand : ShellCommand
{
    public AbsCommand()
        : base("abs", "Computes the absolute value of a number, quantity, or complex magnitude.", "abs [value]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        bool hasPipelineInput = false;
        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            hasPipelineInput = true;
            yield return ComputeAbs(item);
        }

        if (!hasPipelineInput)
        {
            if (context.Arguments.Count == 0)
                throw new InvalidOperationException("abs requires a value argument or piped input.");

            yield return ComputeAbs(context.Arguments[0]);
        }
    }

    private static object? ComputeAbs(object? value) => value switch
    {
        null => null,
        int i => Math.Abs(i),
        long l => Math.Abs(l),
        double d => Math.Abs(d),
        float f => Math.Abs(f),
        decimal m => Math.Abs(m),
        BigInteger bi => BigInteger.Abs(bi),
        Complex c => c.Magnitude,
        Quantity q => UnitRegistry.Instance.CreateTyped(Math.Abs(q.Magnitude), q.Dimension, q.UnitSymbol),
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => Math.Abs(parsed),
        _ => Math.Abs(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture))
    };
}
