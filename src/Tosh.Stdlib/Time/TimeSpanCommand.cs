using Tosh.Runtime;

namespace Tosh.Stdlib.Time;

[Stdlib(StdlibCategory.Time)]
[CommandCategory("System")]
[CommandArgument("duration", "Duration text such as 250ms, 5s, 2m, 1h, or 1d.")]
[CommandExample("timespan 250ms", Title = "Parse milliseconds")]
[CommandExample("timespan 1h30m", Title = "Parse a compound duration")]
[CommandOutput("A System.TimeSpan value parsed/constructed from the supplied components.")]
public sealed class TimeSpanCommand : ShellCommand
{
    public TimeSpanCommand()
        : base(
            "timespan",
            "Parses a duration into a CLR duration value.",
            "timespan <duration>")
    { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var text = CommandArguments.RequireString(context.Arguments, 0, "duration");

        if (!TemporalParser.TryParseTemporalAmount(text, out var value))
        {
            throw new InvalidOperationException($"Could not parse '{text}' as a duration.");
        }

        yield return value.IsPureTimeSpan ? value.Duration : value;
        await Task.CompletedTask;
    }
}
