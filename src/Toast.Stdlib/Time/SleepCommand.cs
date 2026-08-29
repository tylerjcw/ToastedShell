using Tosh.Runtime;

namespace Tosh.Stdlib.Time;

[CommandCategory("System")]
[CommandArgument("duration", "A duration: integer (seconds), decimal, TimeSpan, quantity (e.g. 500`ms), or string like '1s', '2m'.")]
[CommandExample("sleep 2", Title = "Sleep for 2 seconds")]
[CommandExample("sleep 500`ms", Title = "Sleep for 500 milliseconds using a unit literal")]
[CommandExample("sleep 1 2 3", Title = "Sleep for a total of 6 seconds")]
[CommandOutput("No output. Resumes after the specified duration.")]
public sealed class SleepCommand : ShellCommand
{
    public SleepCommand()
        : base("sleep", "Pauses execution for a duration.", "sleep <duration> [duration...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException("sleep requires at least one duration.");
        }

        var total = TimeSpan.Zero;

        foreach (var argument in context.Arguments)
        {
            total += ParseDuration(argument);
        }

        if (total > TimeSpan.Zero)
        {
            await Task.Delay(total, context.CancellationToken);
        }

        yield break;
    }

    private static TimeSpan ParseDuration(object? value)
    {
        if (TypeConversion.TryConvert(value, typeof(TimeSpan), out var converted) &&
            converted is TimeSpan duration)
        {
            return duration;
        }

        throw new InvalidOperationException($"'{value}' is not a valid sleep duration.");
    }
}
