using Tosh.Runtime.Formats;

using Tosh.Runtime;

namespace Tosh.Stdlib.Data;

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

    /// <summary>
    /// Flags that stand alone rather than taking the next argument as their value.
    /// </summary>
    /// <remarks>
    /// Named explicitly because the separation above has no per-flag arity to consult. A flag
    /// missing from this set merely eats an argument it should not, which is exactly the failure
    /// `--typed` arrived with, so the list is worth keeping current.
    /// </remarks>
    private static bool IsValuelessFlag(string argument) => argument.TrimStart('-').ToLowerInvariant() switch
    {
        "typed" or "raw" or "r" or "compact" or "c" or "headers" or "no-headers" => true,
        _ => false,
    };

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
        //
        // `TOAST-0092`. A flag consumed the following argument *unconditionally*, which was
        // invisible while every flag `from` accepted took a value — `-d ,` and friends. The
        // first boolean one broke it: `from json --typed $document` read `$document` as the
        // value of `--typed` and then reported that no text had been supplied, naming the
        // argument that was right there.
        var explicitText = new List<object?>();
        for (var i = 0; i < remainingArgs.Length; i++)
        {
            var arg = remainingArgs[i]?.ToString() ?? string.Empty;

            if (arg.StartsWith('-') && arg.Length > 1)
            {
                if (!IsValuelessFlag(arg) &&
                    i + 1 < remainingArgs.Length &&
                    (remainingArgs[i + 1]?.ToString() ?? string.Empty) is var next &&
                    !next.StartsWith('-'))
                {
                    i++;
                }

                continue;
            }

            explicitText.Add(remainingArgs[i]);
        }

        var text = await StructuredTextInput.ReadAllTextAsync(
            context,
            explicitText.Count > 0 ? explicitText : null,
            $"'from {formatName}' expects text from the pipeline or an explicit argument.");

        // `TOAST-0092`. A format that resolves names against the program's own types needs the
        // context; one that only transforms text does not, and is not made to accept one.
        var values = format is IContextualDataFormat contextual
            ? contextual.DeserializeAsync(text, remainingArgs, context)
            : format.DeserializeAsync(text, remainingArgs);

        await foreach (var value in values)
        {
            yield return value;
        }
    }
}
