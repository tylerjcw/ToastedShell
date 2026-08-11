using System.Xml.Linq;

namespace Tosh.Runtime;

/// <summary>
/// An <see cref="XDocument"/> as shell data, symmetric with
/// <see cref="JsonValueConverter"/> (<c>TS-P1-44</c>).
/// </summary>
/// <remarks>
/// <para>
/// <c>from xml</c> used to yield the <see cref="XDocument"/> itself, so the display engine
/// reflected over it and printed <c>NodeType</c>, <c>BaseUri</c>, <c>Parent</c>,
/// <c>FirstNode</c> and a spray of <c>&lt;cycle&gt;</c> markers where the tree points back
/// at itself. Every other reader converts — <c>from json</c> yields a record,
/// <c>from csv</c> and <c>from tsv</c> a list of records, <c>from toml</c> a record — so
/// <c>to xml | from xml</c> was the only pair in the family that did not round-trip.
/// </para>
/// <para>
/// The shape follows the same conventions the rest of the ecosystem uses, because a
/// reader coming from any other tool should not have to learn a third:
/// </para>
/// <list type="bullet">
/// <item>An element with only text and no attributes becomes that text.</item>
/// <item>An empty element becomes <c>null</c>.</item>
/// <item>Child elements become fields named after them.</item>
/// <item>Repeated sibling names become a list, so <c>&lt;item/&gt;&lt;item/&gt;</c> is one
/// field holding two entries rather than a field silently overwritten.</item>
/// <item>Attributes become fields prefixed <c>@</c>, which cannot collide with an element
/// name because <c>@</c> is not legal at the start of one.</item>
/// <item>Text alongside attributes or children becomes <c>#text</c>, for the same
/// reason.</item>
/// </list>
/// </remarks>
public static class XmlValueConverter
{
    /// <summary>The document's root element, as shell data.</summary>
    public static object? Convert(XDocument document) => ConvertElement(document.Root);

    public static object? ConvertElement(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToArray();
        var children = element.Elements().ToArray();

        // The common case by a wide margin: <name>text</name> is its text, so a document
        // of leaves reads like the record anyone would have written by hand.
        if (children.Length == 0 && attributes.Length == 0)
        {
            return element.IsEmpty || string.IsNullOrEmpty(element.Value) ? null : element.Value;
        }

        var fields = new List<KeyValuePair<string, object?>>();

        foreach (var attribute in attributes)
        {
            fields.Add(new KeyValuePair<string, object?>("@" + attribute.Name.LocalName, attribute.Value));
        }

        foreach (var group in children.GroupBy(child => child.Name.LocalName, StringComparer.Ordinal))
        {
            var converted = group.Select(ConvertElement).ToArray();

            // One occurrence stays a value; repeats become a list. Collapsing repeats to
            // the last one is the failure this avoids — it loses data without saying so.
            fields.Add(new KeyValuePair<string, object?>(
                group.Key,
                converted.Length == 1 ? converted[0] : converted));
        }

        if (children.Length == 0)
        {
            var text = element.Value;

            if (!string.IsNullOrEmpty(text))
            {
                fields.Add(new KeyValuePair<string, object?>("#text", text));
            }
        }

        return ShellRecordUtilities.CreateExpando(fields);
    }
}
