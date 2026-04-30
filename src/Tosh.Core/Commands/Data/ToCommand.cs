using Tosh.Core.Formats;

namespace Tosh.Core.Commands.Data;

[Stdlib(StdlibCategory.Data)]
[CommandCategory("Data")]
[CommandOutput("Serialized text in the specified format.", Mode = "text")]
[CommandExample("ls | to json")]
[CommandExample("ls | to csv")]
[CommandExample("ls | to toml")]
[CommandExample("ls | to json --compact")]
[CommandNote("The `from` and `to` commands convert between text formats (json, csv, tsv, xml, toml) and CLR objects. Parsed values stay as CLR objects until you explicitly flatten them.")]
public sealed class ToCommand : ShellCommand
{
    private readonly DataFormatRegistry _formats;

    public ToCommand(DataFormatRegistry formats, string name = "to")
        : base(name, "Serializes objects into structured text.", "to <format> [options]")
    {
        _formats = formats;
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException(
                $"Usage: to <format> [options]\nAvailable formats: {string.Join(", ", _formats.GetAll().Select(f => f.Name))}.");
        }

        var formatName = context.Arguments[0]?.ToString()
            ?? throw new InvalidOperationException("Format name is required.");

        var format = _formats.Resolve(formatName);
        var remainingArgs = context.Arguments.Skip(1).ToArray();

        var values = await AsyncEnumerableExtensions.ToListAsync(context.Input, context.CancellationToken);

        if (values.Count == 0)
        {
            yield return new ShellTextLine("null");
            yield break;
        }

        await foreach (var value in format.SerializeAsync(values, remainingArgs))
        {
            yield return value;
        }
    }
}
