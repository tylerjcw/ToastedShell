using System.Collections;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Tosh.Runtime.Formats;

public sealed class XmlDataFormat : IDataFormat
{
    public string Name => "xml";
    public IReadOnlyList<string> Aliases => Array.Empty<string>();
    public string Description => "Extensible Markup Language";

    public async IAsyncEnumerable<object?> DeserializeAsync(string text, IReadOnlyList<object?> arguments)
    {
        await Task.CompletedTask;

        XDocument document;

        try
        {
            document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new InvalidOperationException($"Could not parse XML input. {exception.Message}");
        }

        // `TS-P1-44`. Converted rather than handed over raw: yielding the `XDocument`
        // meant the display engine reflected over it and printed `NodeType`, `BaseUri`,
        // `Parent` and `<cycle>` markers, and `to xml | from xml` was the only pair in
        // the format family that did not round-trip.
        //
        // `--raw` keeps the document, because handing it over *was* a deliberate choice —
        // it is how a caller reaches the XML API for namespaces or node-level navigation,
        // and a pinned test relied on it. The change is which of the two is the default,
        // not the removal of one.
        if (ParsedCommandArguments.Parse(arguments).HasFlag("raw"))
        {
            yield return document;
            yield break;
        }

        yield return XmlValueConverter.Convert(document);
    }

    public async IAsyncEnumerable<object?> SerializeAsync(IReadOnlyList<object?> values, IReadOnlyList<object?> arguments)
    {
        await Task.CompletedTask;

        var args = ParsedCommandArguments.Parse(arguments);
        var rootName = args.Positionals.Count > 0
            ? args.Positionals[0]?.ToString() ?? "root"
            : "root";
        var indent = !args.HasFlag("c", "compact");

        // `TOAST-0092`. The tag rides as an ordinary `$type` member, which XML renders like any
        // other, so the placement rule is the same one json and toml follow.
        var typed = args.HasFlag("typed");
        var normalized = values.Count == 1
            ? ShellDataSerializer.Normalize(values[0], typed)
            : values.Select(value => ShellDataSerializer.Normalize(value, typed)).ToArray();

        var root = ToXElement(rootName, normalized);
        var document = new XDocument(root);

        var settings = new XmlWriterSettings
        {
            Indent = indent,
            OmitXmlDeclaration = true,
            NewLineOnAttributes = false,
        };

        var builder = new StringBuilder();

        using (var writer = XmlWriter.Create(builder, settings))
        {
            document.WriteTo(writer);
        }

        yield return new ShellTextLine(builder.ToString());
    }

    private static object? ConvertElement(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        if (!element.HasElements)
        {
            if (!element.HasAttributes)
            {
                return element.Value;
            }

            var leaf = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var attr in element.Attributes())
            {
                leaf[$"@{attr.Name.LocalName}"] = attr.Value;
            }

            if (!string.IsNullOrEmpty(element.Value))
            {
                leaf["#text"] = element.Value;
            }

            return ShellRecordUtilities.CreateExpando(leaf);
        }

        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var attr in element.Attributes())
        {
            fields[$"@{attr.Name.LocalName}"] = attr.Value;
        }

        var grouped = element.Elements().GroupBy(e => e.Name.LocalName, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var items = group.ToArray();

            if (items.Length == 1)
            {
                fields[group.Key] = ConvertElement(items[0]);
            }
            else
            {
                fields[group.Key] = items.Select(ConvertElement).ToArray();
            }
        }

        return ShellRecordUtilities.CreateExpando(fields);
    }

    private static XElement ToXElement(string name, object? value)
    {
        switch (value)
        {
            case null:
                return new XElement(SanitizeXmlName(name));

            case IDictionary<string, object?> dictionary:
                {
                    var element = new XElement(SanitizeXmlName(name));

                    foreach (var (key, val) in dictionary)
                    {
                        if (key.StartsWith('@'))
                        {
                            element.Add(new XAttribute(key[1..], ExternalTextSerializer.Serialize(val)));
                        }
                        else if (key == ShellDataSerializer.TypeKey)
                        {
                            // `TOAST-0092`. `$` is not legal in an XML name, so the tag rides as
                            // an attribute rather than being sanitised into a `__type` element
                            // that no XML reader would recognise as a type.
                            element.Add(new XAttribute("type", ExternalTextSerializer.Serialize(val)));
                        }
                        else if (key == ShellDataSerializer.ValueKey)
                        {
                            // A tagged scalar — an enum member — has no fields to sit beside, so
                            // its value is the element's text: `<Job type="Profession">Librarian</Job>`.
                            element.Add(new XText(ExternalTextSerializer.Serialize(val)));
                        }
                        else if (key == "#text")
                        {
                            element.Add(new XText(ExternalTextSerializer.Serialize(val)));
                        }
                        else
                        {
                            element.Add(ToXElement(key, val));
                        }
                    }

                    return element;
                }

            case IEnumerable enumerable when value is not string:
                {
                    var wrapper = new XElement(SanitizeXmlName(name));

                    foreach (var item in enumerable)
                    {
                        wrapper.Add(ToXElement("item", item));
                    }

                    return wrapper;
                }

            default:
                return new XElement(SanitizeXmlName(name), ExternalTextSerializer.Serialize(value));
        }
    }

    private static string SanitizeXmlName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "element";
        }

        var builder = new StringBuilder(name.Length);

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (i == 0 && !char.IsLetter(c) && c != '_')
            {
                builder.Append('_');
            }

            if (char.IsLetterOrDigit(c) || c is '_' or '-' or '.')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('_');
            }
        }

        return builder.Length == 0 ? "element" : builder.ToString();
    }
}
