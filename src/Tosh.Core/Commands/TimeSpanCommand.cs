namespace Tosh.Core.Commands;

public sealed class TimeSpanCommand : ShellCommand
{
    public TimeSpanCommand()
        : base(
            "timespan",
            "Parses a duration into a CLR TimeSpan.",
            "timespan <duration>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var value = CommandArguments.RequireConverted<TimeSpan>(context.Arguments, 0, "duration");
        yield return value;
        await Task.CompletedTask;
    }
}
