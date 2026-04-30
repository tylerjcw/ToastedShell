using Tosh.Runtime;

namespace Tosh.Stdlib.Time;

[Stdlib(StdlibCategory.Time)]
[CommandCategory("System")]
[CommandArgument("mode|operation", "Creation mode (`now`, `utc-now`, `today`, `tomorrow`, `yesterday`, `parse`, `from-unix`, `from-unix-ms`, `date-only`, `time-only`, or an ISO value) or pipeline operation (`add`, `sub`, `date-only`, `time-only`).", Required = false)]
[CommandArgument("value ...", "Values required by the chosen mode or operation, such as a parse string, Unix timestamp, or duration.", Required = false)]
[CommandOption("-d, --date-only, --dateonly", "Project results to DateOnly values.")]
[CommandOption("-t, --time-only, --timeonly", "Project results to TimeOnly values.")]
[CommandExample("date now -d -t")]
[CommandExample("date parse 2026-03-29T12:34:56Z -d")]
[CommandExample("date parse 2026-03-29T12:34:56Z | cast timeonly")]
[CommandOutput("DateTime, DateOnly, or TimeOnly values depending on the requested mode/operation and projection flags.")]
public sealed class DateCommand : ShellCommand
{
    public DateCommand()
        : base(
            "date",
            "Creates, parses, and adjusts date/time values.",
            "date [-d|--date-only] [-t|--time-only] <now|utc-now|today|tomorrow|yesterday|parse|from-unix|from-unix-ms|date-only|time-only|<iso-date>> ... or <date> | date [-d] [-t] <add|sub|date-only|time-only> ...")
    { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var emitDateOnly = parsed.HasFlag("d", "date-only", "dateonly");
        var emitTimeOnly = parsed.HasFlag("t", "time-only", "timeonly");

        await using var enumerator = context.Input.GetAsyncEnumerator(context.CancellationToken);

        if (await enumerator.MoveNextAsync())
        {
            var operation = CommandArguments.RequireString(parsed.Positionals, 0, "operation");

            do
            {
                foreach (var item in AdjustValue(enumerator.Current, parsed.Positionals, operation, emitDateOnly, emitTimeOnly))
                {
                    yield return item;
                }
            }
            while (await enumerator.MoveNextAsync());

            yield break;
        }

        if (parsed.Positionals.Count == 0)
        {
            foreach (var item in ProjectTemporalValue(DateTimeOffset.Now, emitDateOnly, emitTimeOnly))
            {
                yield return item;
            }

            yield break;
        }

        var mode = CommandArguments.RequireString(parsed.Positionals, 0, "mode");

        switch (mode.ToLowerInvariant())
        {
            case "now":
                foreach (var item in ProjectTemporalValue(DateTimeOffset.Now, emitDateOnly, emitTimeOnly))
                {
                    yield return item;
                }
                yield break;

            case "utc-now":
                foreach (var item in ProjectTemporalValue(DateTimeOffset.UtcNow, emitDateOnly, emitTimeOnly))
                {
                    yield return item;
                }
                yield break;

            case "today":
                foreach (var item in ProjectTemporalValue(StartOfLocalDay(DateTimeOffset.Now), emitDateOnly, emitTimeOnly))
                {
                    yield return item;
                }
                yield break;

            case "tomorrow":
                foreach (var item in ProjectTemporalValue(StartOfLocalDay(DateTimeOffset.Now).AddDays(1), emitDateOnly, emitTimeOnly))
                {
                    yield return item;
                }
                yield break;

            case "yesterday":
                foreach (var item in ProjectTemporalValue(StartOfLocalDay(DateTimeOffset.Now).AddDays(-1), emitDateOnly, emitTimeOnly))
                {
                    yield return item;
                }
                yield break;

            case "parse":
                {
                    var text = CommandArguments.RequireString(parsed.Positionals, 1, "value");

                    if (!TemporalParser.TryParseDateTimeOffset(text, out var parsedValue))
                    {
                        throw new InvalidOperationException(
                            $"Could not parse '{text}' as a date/time. Use ISO-style forms like 2026-03-23, 2026-03-23T14:05:00, or 2026-03-23T14:05:00Z.");
                    }

                    foreach (var item in ProjectTemporalValue(parsedValue, emitDateOnly, emitTimeOnly))
                    {
                        yield return item;
                    }
                    yield break;
                }

            case "from-unix":
                foreach (var item in ProjectTemporalValue(DateTimeOffset.FromUnixTimeSeconds(CommandArguments.RequireConverted<long>(parsed.Positionals, 1, "seconds")), emitDateOnly, emitTimeOnly))
                {
                    yield return item;
                }
                yield break;

            case "from-unix-ms":
                foreach (var item in ProjectTemporalValue(DateTimeOffset.FromUnixTimeMilliseconds(CommandArguments.RequireConverted<long>(parsed.Positionals, 1, "milliseconds")), emitDateOnly, emitTimeOnly))
                {
                    yield return item;
                }
                yield break;

            case "date-only":
            case "dateonly":
                if (parsed.Positionals.Count == 1)
                {
                    yield return DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
                    yield break;
                }

                yield return ConvertToDateOnly(parsed.Positionals[1]);
                yield break;

            case "time-only":
            case "timeonly":
                if (parsed.Positionals.Count == 1)
                {
                    yield return TimeOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);
                    yield break;
                }

                yield return ConvertToTimeOnly(parsed.Positionals[1]);
                yield break;
        }

        if (parsed.Positionals.Count == 1 && TemporalParser.TryParseDateTimeOffset(mode, out var directValue))
        {
            foreach (var item in ProjectTemporalValue(directValue, emitDateOnly, emitTimeOnly))
            {
                yield return item;
            }
            yield break;
        }

        throw new InvalidOperationException("date mode must be 'now', 'utc-now', 'today', 'tomorrow', 'yesterday', 'parse', 'from-unix', 'from-unix-ms', or an ISO-style date/time value.");
    }

    private static IEnumerable<object> AdjustValue(
        object? value,
        IReadOnlyList<object?> arguments,
        string operation,
        bool emitDateOnly,
        bool emitTimeOnly)
    {
        var result = value switch
        {
            _ => ApplyOperation(value, arguments, operation),
        };

        foreach (var item in ProjectTemporalValue(result, emitDateOnly, emitTimeOnly))
        {
            yield return item;
        }
    }

    private static object ApplyOperation(object? value, IReadOnlyList<object?> arguments, string operation)
    {
        var normalizedOperation = operation.ToLowerInvariant();

        return normalizedOperation switch
        {
            "date-only" or "dateonly" => ConvertToDateOnly(value),
            "time-only" or "timeonly" => ConvertToTimeOnly(value),
            "add" or "sub" or "subtract" => ApplyTemporalAdjustment(value, normalizedOperation, arguments),
            _ => throw new InvalidOperationException("date pipeline operations must be 'add', 'sub', 'date-only', or 'time-only'."),
        };
    }

    private static object ApplyTemporalAdjustment(object? value, string operation, IReadOnlyList<object?> arguments)
    {
        var delta = CommandArguments.RequireConverted<TemporalAmount>(arguments, 1, "duration");

        return value switch
        {
            DateTime dateTime => Apply(dateTime, operation, delta),
            DateTimeOffset dateTimeOffset => Apply(dateTimeOffset, operation, delta),
            _ => throw new InvalidOperationException("date add/sub expects DateTime or DateTimeOffset values from the pipeline."),
        };
    }

    private static DateTime Apply(DateTime value, string operation, TemporalAmount delta)
    {
        return operation switch
        {
            "add" => delta.AddTo(value),
            "sub" or "subtract" => delta.SubtractFrom(value),
            _ => throw new InvalidOperationException("date pipeline operations must be 'add' or 'sub'."),
        };
    }

    private static DateTimeOffset Apply(DateTimeOffset value, string operation, TemporalAmount delta)
    {
        return operation switch
        {
            "add" => delta.AddTo(value),
            "sub" or "subtract" => delta.SubtractFrom(value),
            _ => throw new InvalidOperationException("date pipeline operations must be 'add' or 'sub'."),
        };
    }

    private static DateOnly ConvertToDateOnly(object? value)
    {
        var normalized = NormalizeTemporalInput(value);

        if (TypeConversion.TryConvert(normalized, typeof(DateOnly), out var converted) && converted is DateOnly dateOnly)
        {
            return dateOnly;
        }

        throw new InvalidOperationException("date date-only expects DateTime, DateTimeOffset, or parseable date/time values.");
    }

    private static TimeOnly ConvertToTimeOnly(object? value)
    {
        var normalized = NormalizeTemporalInput(value);

        if (TypeConversion.TryConvert(normalized, typeof(TimeOnly), out var converted) && converted is TimeOnly timeOnly)
        {
            return timeOnly;
        }

        throw new InvalidOperationException("date time-only expects DateTime, DateTimeOffset, or parseable date/time values.");
    }

    private static object? NormalizeTemporalInput(object? value)
    {
        return value is ShellTextLine line ? line.Text : value;
    }

    private static IEnumerable<object> ProjectTemporalValue(object value, bool emitDateOnly, bool emitTimeOnly)
    {
        if (!emitDateOnly && !emitTimeOnly)
        {
            yield return value;
            yield break;
        }

        if (emitDateOnly)
        {
            yield return ConvertToDateOnly(value);
        }

        if (emitTimeOnly)
        {
            yield return ConvertToTimeOnly(value);
        }
    }

    private static DateTimeOffset StartOfLocalDay(DateTimeOffset instant)
    {
        var local = instant.ToLocalTime();
        return new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
    }
}
