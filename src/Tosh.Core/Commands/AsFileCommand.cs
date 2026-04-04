namespace Tosh.Core.Commands;

public sealed class AsFileCommand : ShellCommand
{
    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "text",
        "json",
        "csv",
    };

    public AsFileCommand()
        : base("as-file", "Materializes values into a temporary file and returns it as a file system entry.", "as-file [text|json|csv] [value ...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var (format, values) = await ResolveFormatAndValuesAsync(context);
        yield return await PipelineFileMaterializer.MaterializeAsync(format, values, context.CancellationToken);
    }

    private static async Task<(string Format, IReadOnlyList<object?> Values)> ResolveFormatAndValuesAsync(CommandContext context)
    {
        var format = "text";
        IReadOnlyList<object?> explicitValues = context.Arguments;

        if (context.Arguments.Count > 0 &&
            context.Arguments[0] is not null &&
            SupportedFormats.Contains(context.Arguments[0]!.ToString() ?? string.Empty))
        {
            format = PipelineFileMaterializer.NormalizeFormat(context.Arguments[0]!.ToString());
            explicitValues = context.Arguments.Skip(1).ToArray();
        }

        var values = explicitValues.Count > 0
            ? explicitValues
            : await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        return (format, values);
    }
}
