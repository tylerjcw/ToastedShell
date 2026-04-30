namespace Tosh.Core.Commands.Maths;

[Stdlib(StdlibCategory.Maths)]
[CommandCategory("Math")]
[CommandArgument("value-or-decimals", "A number to round (with optional decimals), or just the decimal count when input is piped.")]
[CommandArgument("decimals", "Number of decimal places to round to. Defaults to 0.", Required = false)]
[CommandExample("round 3.14159 2", Title = "Round a literal to 2 decimal places")]
[CommandExample("echo 3.14159 | round 2", Title = "Round a piped value to 2 decimal places")]
[CommandExample("echo 3.7 | round", Title = "Round a piped value to nearest integer")]
[CommandOutput("The rounded value as a double.")]
[PipelineInput(AcceptsScalar = true, Description = "Accepts a numeric value to round when only the decimal count is passed as an argument.")]
public sealed class RoundCommand : ShellCommand
{
    public RoundCommand()
        : base("round", "Rounds a number to the specified number of decimal places.", "round [value] [decimals]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        bool hasPipelineInput = false;
        object? pipelineValue = null;

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            hasPipelineInput = true;
            pipelineValue = item;
            break;
        }

        double value;
        int decimals;

        if (hasPipelineInput)
        {
            value = ToDouble(pipelineValue);
            decimals = context.Arguments.Count >= 1 ? ToInt(context.Arguments[0]) : 0;
        }
        else
        {
            if (context.Arguments.Count == 0)
                throw new InvalidOperationException("round requires a value argument or piped input.");

            value = ToDouble(context.Arguments[0]);
            decimals = context.Arguments.Count >= 2 ? ToInt(context.Arguments[1]) : 0;
        }

        if (decimals < 0)
            throw new InvalidOperationException("round: decimal places must be non-negative.");

        yield return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
    }

    private static double ToDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        int i => i,
        long l => l,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
        null => throw new InvalidOperationException("round: received a null value."),
        _ => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        double d => (int)d,
        string s when int.TryParse(s, out var parsed) => parsed,
        null => throw new InvalidOperationException("round: received a null argument."),
        _ => Convert.ToInt32(value),
    };
}
