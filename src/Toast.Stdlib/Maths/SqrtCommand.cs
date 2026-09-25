using System.Numerics;
using Tosh.Runtime;
using Tosh.Runtime.Units;

namespace Tosh.Stdlib.Maths;

[CommandCategory("Math")]
[CommandArgument("value", "The value to compute the square root of.", Required = false)]
[CommandExample("sqrt 16", Title = "Square root of a number")]
[CommandExample("echo 2 | sqrt", Title = "Square root from pipeline")]
[CommandOutput("The square root value.", ClrType = typeof(IAsyncEnumerable<object>))]
[PipelineInput(AcceptsScalar = true, Description = "Accepts numbers, quantities, or complex values to compute square roots of.")]
public sealed class SqrtCommand : ShellCommand
{
    public SqrtCommand()
        : base("sqrt", "Computes the square root of a number, quantity, or complex value.", "sqrt [value]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        bool hasPipelineInput = false;
        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            hasPipelineInput = true;
            yield return ComputeSqrt(item);
        }

        if (!hasPipelineInput)
        {
            if (context.Arguments.Count == 0)
                throw new InvalidOperationException("sqrt requires a value argument or piped input.");

            yield return ComputeSqrt(context.Arguments[0]);
        }
    }

    private static object? ComputeSqrt(object? value) => value switch
    {
        null => null,
        Quantity q when q.Dimension.IsDimensionless => Math.Sqrt(q.Magnitude),
        Quantity q => SqrtQuantity(q),
        double d when d >= 0 => Math.Sqrt(d),
        double d => Complex.Sqrt(new Complex(d, 0)),
        float f when f >= 0 => MathF.Sqrt(f),
        float f => Complex.Sqrt(new Complex(f, 0)),
        int i when i >= 0 => Math.Sqrt(i),
        int i => Complex.Sqrt(new Complex(i, 0)),
        long l when l >= 0 => Math.Sqrt(l),
        long l => Complex.Sqrt(new Complex(l, 0)),
        decimal m when m >= 0 => (decimal)Math.Sqrt((double)m),
        decimal m => Complex.Sqrt(new Complex((double)m, 0)),
        Complex c => Complex.Sqrt(c),
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) =>
            parsed >= 0 ? Math.Sqrt(parsed) : Complex.Sqrt(new Complex(parsed, 0)),
        _ => Math.Sqrt(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture))
    };

    private static object SqrtQuantity(Quantity q)
    {
        var res = q.Sqrt();
        return res.Dimension.IsDimensionless ? res.BaseValue : res;
    }
}
