using Tosh.Runtime;

namespace Tosh.Language;

/// <summary>
/// Places <c>name = value</c> arguments onto the fields of a record or struct — <c>TS-P2-21</c>.
/// </summary>
/// <remarks>
/// <para>
/// Records and structs bound their fields strictly by position, so a named argument arrived as an
/// <see cref="INamedArgument"/> wrapper and was assigned whole: <c>new R("w", Qty = 5)</c>
/// reported "'R.Qty' produced a value that could not be converted to 'int'" — a conversion
/// complaint about a value the reader never wrote. A class constructor already placed them
/// correctly, so the three spellings disagreed.
/// </para>
/// <para>
/// The rule lives here once because <c>ToshRecordDefinition</c> and <c>ToshStructDefinition</c>
/// carry byte-identical field binders, and this programme keeps finding that a rule written twice
/// is a rule that will diverge.
/// </para>
/// </remarks>
internal static class FieldArgumentPlacement
{
    /// <summary>
    /// Splits <paramref name="arguments"/> into the positional ones and a by-name lookup.
    /// </summary>
    /// <remarks>
    /// Case-insensitive on the name, matching the class-constructor binder it is modelled on, so
    /// <c>new R(qty = 5)</c> reaches <c>Qty</c>.
    /// </remarks>
    public static (List<object?> Positional, Dictionary<string, object?> Named) Split(
        IReadOnlyList<object?> arguments,
        string ownerKind,
        string ownerName)
    {
        var positional = new List<object?>(arguments.Count);
        var named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var argument in arguments)
        {
            if (argument is INamedArgument namedArgument)
            {
                // A repeat is reported rather than overwritten. Silently keeping the last one
                // left the earlier field looking unsupplied, so `new R(Qty = 5, Qty = 6)`
                // complained that `Name` was missing — a field the caller never mentioned.
                if (!named.TryAdd(namedArgument.Name, namedArgument.Value))
                {
                    throw new InvalidOperationException(
                        $"Named argument '{namedArgument.Name}' was supplied more than once to " +
                        $"{ownerKind.ToLowerInvariant()} '{ownerName}'.");
                }
            }
            else
            {
                positional.Add(argument);
            }
        }

        return (positional, named);
    }

    /// <summary>
    /// Reports a named argument that matches no field, naming the ones that exist.
    /// </summary>
    /// <remarks>
    /// Left unbound, an unknown name fell through to "no value was provided for required field",
    /// which describes a field the caller never mentioned rather than the name they did.
    /// </remarks>
    public static void EnsureNamesAreKnown(
        IReadOnlyDictionary<string, object?> named,
        IEnumerable<string> fieldNames,
        string ownerKind,
        string ownerName)
    {
        if (named.Count == 0) return;

        var known = new HashSet<string>(fieldNames, StringComparer.OrdinalIgnoreCase);

        foreach (var name in named.Keys)
        {
            if (known.Contains(name)) continue;

            throw new InvalidOperationException(
                $"{ownerKind} '{ownerName}' has no field named '{name}'. " +
                $"Known fields: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
        }
    }
}
