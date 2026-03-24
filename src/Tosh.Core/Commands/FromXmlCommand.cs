using System.Xml;
using System.Xml.Linq;

namespace Tosh.Core.Commands;

public sealed class FromXmlCommand : ShellCommand
{
    public FromXmlCommand()
        : base("from-xml", "Parses XML text into CLR XDocument values.", "from-xml [xml-text]") { }

    public override async IAsyncEnumerable<object?> ExecuteAsync(CommandContext context)
    {
        var parsed = ParsedCommandArguments.Parse(context.Arguments);
        var text = await StructuredTextInput.ReadAllTextAsync(
            context,
            parsed.Positionals,
            "from-xml expects XML text from the pipeline or an explicit argument.");

        XDocument document;

        try
        {
            document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new InvalidOperationException($"Could not parse XML input. {exception.Message}");
        }

        yield return document;
    }
}
