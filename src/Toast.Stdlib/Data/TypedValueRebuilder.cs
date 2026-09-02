using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Stdlib.Data;

/// <summary>
/// Rebuilds declared values from a `$type`-tagged object graph — <c>TOAST-0092</c>.
/// </summary>
/// <remarks>
/// <para>
/// A typed write claims the document can be read back as what went in. This is the half that
/// honours it: without it `--typed` writes a promise nothing keeps, which is a worse state than
/// not tagging at all.
/// </para>
/// <para>
/// <b>Only Tōast-declared types resolve.</b> The same rule TON enforces, and it matters more
/// here — JSON is the format that actually receives untrusted input. A `$type` naming a CLR type
/// is refused, so a document cannot name a type whose *construction* does something. That is the
/// `TypeNameHandling` class closed the same way: structurally, not by blocklist.
/// </para>
/// <para>
/// An untagged part of the document is left exactly as the format produced it. Tagging is
/// per-value, so a tagged record may hold plain dictionaries and a plain list may hold tagged
/// values.
/// </para>
/// </remarks>
internal static class TypedValueRebuilder
{
    internal static object? Rebuild(object? value, IShellNamedTypeView? types)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> map:
                return RebuildMap(map, types);

            case IDictionary<string, object?> mutable:
                return RebuildMap(mutable.AsReadOnly(), types);

            case object?[] array:
                return array.Select(item => Rebuild(item, types)).ToArray();

            case List<object?> list:
                return list.Select(item => Rebuild(item, types)).ToList();

            default:
                return value;
        }
    }

    private static object? RebuildMap(IReadOnlyDictionary<string, object?> map, IShellNamedTypeView? types)
    {
        var rebuilt = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, item) in map)
        {
            if (!string.Equals(key, ShellDataSerializer.TypeKey, StringComparison.Ordinal))
            {
                rebuilt[key] = Rebuild(item, types);
            }
        }

        if (!map.TryGetValue(ShellDataSerializer.TypeKey, out var tag) ||
            tag?.ToString() is not { Length: > 0 } typeName)
        {
            // Untagged: hand back what the format produced, with any tagged parts rebuilt.
            return rebuilt;
        }

        return Construct(typeName, rebuilt, types);
    }

    private static object Construct(
        string typeName,
        IReadOnlyDictionary<string, object?> fields,
        IShellNamedTypeView? types)
    {
        if (types is null || !types.TryGetNamedType(typeName, out var definition))
        {
            throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.runtime.typed_unknown_type",
                Title: $"'{typeName}' is not a type this program declares.",
                Help: "a tagged document may name only declared types — not a CLR type. Declare it, "
                    + "or read the document without --typed to get plain records."));
        }

        switch (definition)
        {
            case ToshEnumDefinition enumeration:
                {
                    // A tagged enum carries `$value` rather than fields: there is nothing for a
                    // member name to sit beside.
                    var member = fields.TryGetValue(ShellDataSerializer.ValueKey, out var raw)
                        ? raw?.ToString()
                        : null;

                    if (member is not null &&
                        enumeration.TryGetStaticMember(member, out var resolved) &&
                        resolved is not null)
                    {
                        return resolved;
                    }

                    throw ToshDiagnosticException.Create(new ToshDiagnostic(
                        Code: "tosh.runtime.typed_unknown_member",
                        Title: $"'{typeName}' has no member '{member ?? "(none)"}'.",
                        Help: $"expected one of: {string.Join(", ", enumeration.Members.Select(m => m.Name))}."));
                }

            case ToshRecordDefinition record:
                {
                    // A record's fields *are* its constructor parameters, so the document's names
                    // are matched to the declaration's order rather than trusted to be in it.
                    var arguments = record.Fields
                        .Select(field => fields.TryGetValue(field.Name, out var item) ? item : null)
                        .ToArray();

                    return record.CreateInstance(arguments);
                }

            case ToshClassDefinition classDefinition:
                {
                    var instance = classDefinition.CreateInstance(Array.Empty<object?>());

                    if (instance is IShellRecordObject target)
                    {
                        foreach (var (name, item) in fields)
                        {
                            target.TrySetMember(name, item);
                        }
                    }

                    return instance;
                }

            default:
                throw ToshDiagnosticException.Create(new ToshDiagnostic(
                    Code: "tosh.runtime.typed_unsupported_kind",
                    Title: $"'{typeName}' cannot be rebuilt from a tagged document yet.",
                    Help: "records, classes and enums are supported. Use 'from ton' for the full "
                        + "set of declared shapes."));
        }
    }
}
