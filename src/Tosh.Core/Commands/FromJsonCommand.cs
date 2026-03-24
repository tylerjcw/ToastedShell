using System.Text.Json;

namespace Tosh.Core.Commands;

public sealed class FromJsonCommand : ShellCommand
{
    public FromJsonCommand()
        : base("from-json", "Parses JSON text into CLR values and projected objects.", "from-json [json-text]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var text = await StructuredTextInput.ReadAllTextAsync(
            context,
            parsed.Positionals,
            "from-json expects JSON text from the pipeline or an explicit argument.");

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Could not parse JSON input. {exception.Message}");
        }

        using (document)
        {
            yield return JsonValueConverter.Convert(document.RootElement);
        }
    }
}
