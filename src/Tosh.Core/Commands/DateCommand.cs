namespace Tosh.Core.Commands;

public sealed class DateCommand : ShellCommand
{
    public DateCommand()
        : base(
            "date",
            "Creates, parses, and adjusts date/time values.",
            "date <now|utc-now|today|tomorrow|yesterday|parse|from-unix|from-unix-ms> ... or <date> | date <add|sub> <timespan>") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (await enumerator.MoveNextAsync())
        {
            var operation = CommandArguments.RequireString(context.Arguments, 0, "operation");
            var delta = CommandArguments.RequireConverted<TimeSpan>(context.Arguments, 1, "timespan");

            do
            {
                yield return AdjustValue(enumerator.Current, operation, delta);
            }
            while (await enumerator.MoveNextAsync());

            yield break;
        }

        var mode = CommandArguments.RequireString(context.Arguments, 0, "mode");

        switch (mode.ToLowerInvariant())
        {
            case "now":
                yield return DateTimeOffset.Now;
                yield break;

            case "utc-now":
                yield return DateTimeOffset.UtcNow;
                yield break;

            case "today":
                yield return StartOfLocalDay(DateTimeOffset.Now);
                yield break;

            case "tomorrow":
                yield return StartOfLocalDay(DateTimeOffset.Now).AddDays(1);
                yield break;

            case "yesterday":
                yield return StartOfLocalDay(DateTimeOffset.Now).AddDays(-1);
                yield break;

            case "parse":
            {
                var text = CommandArguments.RequireString(context.Arguments, 1, "value");

                if (!TemporalParser.TryParseDateTimeOffset(text, out var parsed))
                {
                    throw new InvalidOperationException(
                        $"Could not parse '{text}' as a date/time. Use ISO-style forms like 2026-03-23, 2026-03-23T14:05:00, or 2026-03-23T14:05:00Z.");
                }

                yield return parsed;
                yield break;
            }

            case "from-unix":
                yield return DateTimeOffset.FromUnixTimeSeconds(CommandArguments.RequireConverted<long>(context.Arguments, 1, "seconds"));
                yield break;

            case "from-unix-ms":
                yield return DateTimeOffset.FromUnixTimeMilliseconds(CommandArguments.RequireConverted<long>(context.Arguments, 1, "milliseconds"));
                yield break;
        }

        throw new InvalidOperationException("date mode must be 'now', 'utc-now', 'today', 'tomorrow', 'yesterday', 'parse', 'from-unix', or 'from-unix-ms'.");
    }

    private static object AdjustValue(object? value, string operation, TimeSpan delta)
    {
        return value switch
        {
            DateTime dateTime => Apply(dateTime, operation, delta),
            DateTimeOffset dateTimeOffset => Apply(dateTimeOffset, operation, delta),
            _ => throw new InvalidOperationException("date add/sub expects DateTime or DateTimeOffset values from the pipeline."),
        };
    }

    private static DateTime Apply(DateTime value, string operation, TimeSpan delta)
    {
        return operation.ToLowerInvariant() switch
        {
            "add" => value.Add(delta),
            "sub" or "subtract" => value.Subtract(delta),
            _ => throw new InvalidOperationException("date pipeline operations must be 'add' or 'sub'."),
        };
    }

    private static DateTimeOffset Apply(DateTimeOffset value, string operation, TimeSpan delta)
    {
        return operation.ToLowerInvariant() switch
        {
            "add" => value.Add(delta),
            "sub" or "subtract" => value.Subtract(delta),
            _ => throw new InvalidOperationException("date pipeline operations must be 'add' or 'sub'."),
        };
    }

    private static DateTimeOffset StartOfLocalDay(DateTimeOffset instant)
    {
        var local = instant.ToLocalTime();
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
    }
}
