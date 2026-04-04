namespace Tosh.Core.Commands;

public sealed class WriteCommand : ShellCommand, IImplicitGlobCommand
{
    public WriteCommand()
        : base("write", "Writes rendered values without a trailing newline.", "write [value...]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var rendered = await RenderAsync(context);

        if (rendered.Length > 0)
        {
            await context.Runtime.Output.WriteAsync(rendered);
        }

        yield break;
    }

    internal static async Task<string> RenderAsync(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Arguments.Count > 0)
        {
            return string.Join(" ", context.Arguments.Select(ExternalTextSerializer.Serialize));
        }

        var values = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (values.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(
            Environment.NewLine,
            values.Select(value => value is ShellTextLine line ? line.Text : ExternalTextSerializer.Serialize(value)));
    }
}
