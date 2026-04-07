namespace Tosh.Core.Commands;

[CommandCategory("System")]
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
