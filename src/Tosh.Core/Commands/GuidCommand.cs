using System.Globalization;

namespace Tosh.Core.Commands;

[CommandCategory("System")]
public sealed class GuidCommand : ShellCommand
{
    public GuidCommand()
        : base(
            "guid",
            "Creates, parses, formats, and inspects GUID values.",
            "guid [new [v4|v7]|empty|parse|format <d|n|b|p|x>|info] [value ...]")
    { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            yield return Guid.NewGuid();
            yield break;
        }

        var mode = CommandArguments.RequireString(context.Arguments, 0, "mode");

        switch (mode.ToLowerInvariant())
        {
            case "new":
                {
                    var version = context.Arguments.Count > 1
                        ? CommandArguments.RequireString(context.Arguments, 1, "version")
                        : "v4";

                    yield return CreateGuid(version);
                    yield break;
                }

            case "empty":
                yield return Guid.Empty;
                yield break;

            case "parse":
                {
                    await foreach (var value in EnumerateGuidValuesAsync(context, 1))
                    {
                        yield return value;
                    }

                    yield break;
                }

            case "format":
                {
                    var format = NormalizeFormatSpecifier(CommandArguments.RequireString(context.Arguments, 1, "format"));

                    await foreach (var value in EnumerateGuidValuesAsync(context, 2))
                    {
                        yield return value.ToString(format, CultureInfo.InvariantCulture);
                    }

                    yield break;
                }

            case "info":
                {
                    await foreach (var value in EnumerateGuidValuesAsync(context, 1))
                    {
                        yield return CreateGuidInfo(value);
                    }

                    yield break;
                }
        }

        throw new InvalidOperationException("guid mode must be 'new', 'empty', 'parse', 'format', or 'info'.");
    }

    private static Guid CreateGuid(string version)
    {
        return version.ToLowerInvariant() switch
        {
            "v4" or "4" => Guid.NewGuid(),
            "v7" or "7" => Guid.CreateVersion7(),
            _ => throw new InvalidOperationException("guid new version must be 'v4' or 'v7'."),
        };
    }

    private static string NormalizeFormatSpecifier(string format)
    {
        var normalized = format.Trim().ToLowerInvariant();

        return normalized switch
        {
            "d" or "n" or "b" or "p" or "x" => normalized,
            _ => throw new InvalidOperationException("guid format must be one of 'd', 'n', 'b', 'p', or 'x'."),
        };
    }

    private async IAsyncEnumerable<Guid> EnumerateGuidValuesAsync(CommandContext context, int explicitStartIndex)
    {
        if (context.Arguments.Count > explicitStartIndex)
        {
            for (var index = explicitStartIndex; index < context.Arguments.Count; index++)
            {
                yield return ConvertToGuid(context.Arguments[index], $"value #{index - explicitStartIndex + 1}");
            }

            yield break;
        }

        var sawAny = false;

        await foreach (var item in context.Input.WithCancellation(context.CancellationToken))
        {
            sawAny = true;
            yield return ConvertToGuid(item, "pipeline value");
        }

        if (!sawAny)
        {
            throw new InvalidOperationException("guid parse/format/info expects one or more GUID values.");
        }
    }

    private static Guid ConvertToGuid(object? value, string label)
    {
        var normalized = value is ShellTextLine line ? line.Text : value;

        if (TypeConversion.TryConvert(normalized, typeof(Guid), out var converted) && converted is Guid guid)
        {
            return guid;
        }

        throw new InvalidOperationException($"Argument '{label}' could not be converted to Guid.");
    }

    private static IDictionary<string, object?> CreateGuidInfo(Guid value)
    {
        return ShellRecordUtilities.CreateExpando(
        [
            new KeyValuePair<string, object?>("Value", value.ToString("D", CultureInfo.InvariantCulture)),
            new KeyValuePair<string, object?>("Version", GuidUtilities.GetVersion(value)),
            new KeyValuePair<string, object?>("Variant", GuidUtilities.GetVariantName(value)),
            new KeyValuePair<string, object?>("Empty", value == Guid.Empty),
        ]);
    }
}
