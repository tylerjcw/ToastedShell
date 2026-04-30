using Tosh.Core.Formats;

namespace Tosh.Core.Commands;

[Stdlib(StdlibCategory.Data)]
[CommandCategory("Data")]
[CommandOutput("Parsed CLR objects from the text format.", Mode = "structured")]
[CommandNote("The `from` and `to` commands convert between text formats (json, csv, tsv, xml, toml) and CLR objects. Parsed values stay as CLR objects until you explicitly flatten them.")]
[CommandExample("echo \"{\\\"name\\\":\\\"toast\\\"}\" | from json")]
[CommandExample("curl https://example/api | from json | flatten")]
[CommandExample("cat data.toml | from toml")]
[CommandExample("cat data.csv | from csv")]
public sealed class FromCommand : ShellCommand
{
    private readonly DataFormatRegistry _formats;

    public FromCommand(DataFormatRegistry formats, string name = "from")
        : base(name, "Parses structured text into objects.", "from <format> [options] [text]")
    {
        _formats = formats;
    }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Count == 0)
        {
            throw new InvalidOperationException(
                $"Usage: from <format> [options] [text]\nAvailable formats: {string.Join(", ", _formats.GetAll().Select(f => f.Name))}.");
        }

        var formatName = context.Arguments[0]?.ToString()
            ?? throw new InvalidOperationException("Format name is required.");

        var format = _formats.Resolve(formatName);
        var remainingArgs = context.Arguments.Skip(1).ToArray();

        // Separate flag arguments from explicit text positionals.
        // Anything starting with - is a flag (or flag value if preceded by a flag).
        var explicitText = new List<object?>();
        for (var i = 0; i < remainingArgs.Length; i++)
        {
            var arg = remainingArgs[i]?.ToString() ?? string.Empty;

            if (arg.StartsWith('-') && arg.Length > 1)
            {
                // Skip the flag and its value argument
                i++;
                continue;
            }

            explicitText.Add(remainingArgs[i]);
        }

        var text = await StructuredTextInput.ReadAllTextAsync(
            context,
            explicitText.Count > 0 ? explicitText : null,
            $"'from {formatName}' expects text from the pipeline or an explicit argument.");

        await foreach (var value in format.DeserializeAsync(text, remainingArgs))
        {
            yield return value;
        }
    }
}
