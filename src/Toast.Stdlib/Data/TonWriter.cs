using System.Collections;
using System.Globalization;
using System.Text;
using Tosh.Language;
using Tosh.Runtime;
using Tosh.Runtime.Units;

namespace Tosh.Stdlib.Data;

/// <summary>
/// Writes a value as Tōast Object Notation — <c>TOAST-0092</c>.
/// </summary>
/// <remarks>
/// <para>
/// TON is the subset of Tōast's own value syntax that means something without a schema, the way
/// JSON relates to JavaScript. Everything written here is therefore valid Tōast source, which is
/// the property the whole design rests on: there is one grammar and one parser, and a reader is
/// not a second implementation that can drift from the first.
/// </para>
/// <para>
/// <b>Named fields, never positional.</b> <c>new Exchange("Emerald", 1)</c> parses anywhere and
/// means nothing without knowing the field order, so reordering a record's fields would silently
/// corrupt every existing document. Named arguments are order-independent and survive it.
/// </para>
/// <para>
/// <b>`new` is kept.</b> The bare <c>Villager {| … |}</c> the item first sketched is
/// grammatically identical to a command invocation, which is why <c>TOAST-0091</c> settled on
/// <c>new</c>. TON keeps it rather than defining a terser grammar of its own — a document only
/// the notation's own parser accepts is not a subset of the language.
/// </para>
/// <para>
/// <b>Type arguments appear only where the payload cannot supply them.</b>
/// <c>Option::Some(5)</c> infers <c>T</c>; <c>Option::None&lt;int&gt;()</c> cannot, so it carries
/// its own. This is the shortest spelling that still reconstructs without a target type, which
/// matters because a heterogeneous stream has no single target to supply.
/// </para>
/// </remarks>
internal static class TonWriter
{
    private const string Indent = "    ";

    internal static string Write(object? value) => Write(value, null);

    /// <summary>
    /// Writes <paramref name="value"/>, naming a type only when <paramref name="types"/> confirms
    /// the reader can resolve it — <c>TOAST-0092</c>.
    /// </summary>
    /// <remarks>
    /// The writer must only emit what the reader accepts. Without the check it emitted
    /// `new Point2D {| … |}` for a class declared inside `module ToastLib.Math`, and its own
    /// reader refused the document: the bare name does not resolve, and there is no spelling for
    /// the qualified one that the notation admits. Such a value degrades to an *anonymous*
    /// record, which still carries the data and still reads back.
    /// </remarks>
    internal static string Write(object? value, IShellNamedTypeView? types) => Write(value, 0, types);

    private static string Write(object? value, int depth, IShellNamedTypeView? types = null)
    {
        switch (value)
        {
            case null:
                return "null";

            case bool flag:
                return flag ? "true" : "false";

            case string text:
                return Quote(text);

            case char character:
                return Quote(character.ToString());

            // Before the record-object case: a quantity implements `IShellRecordObject` too, and
            // its parts are not a shape to rebuild — `483.06`MW` is one value and its literal
            // says so.
            case Quantity quantity:
                return $"{Format((decimal)quantity.Magnitude)}`{quantity.UnitSymbol}";

            case IShellEnumValue shellEnum:
                // A path, not a member access. `TOAST-0090`'s operator is what makes the safety
                // rule syntactic: member access is not in the notation's grammar at all, so no
                // validator bug can admit `DateTime::Now`.
                return $"{shellEnum.EnumTypeName}::{shellEnum.Name}";

            case ToshUnionVariantInstance variant:
                return WriteVariant(variant, depth, types);

            case ToshRecordInstance record:
                // A record's fields *are* its constructor parameters, so it has no zero-argument
                // constructor to fill afterwards — named arguments are the only spelling.
                return Nameable(record.Definition.Name, types)
                    ? WriteCall($"new {record.Definition.Name}", record.GetMembers(), depth, types)
                    : WriteAnonymous(record.GetMembers(), depth, types);

            case ToshClassInstance instance:
                // Nameability is asked of the *bare* name, because that is the key the type
                // registry holds; what gets written is the descriptor's, which carries the
                // type arguments a generic class needs — `Box<Int32>`. Dropping them wrote a
                // `Box<string>` and a `Box<int>` as the same document.
                return Nameable(instance.Definition.Name, types)
                    ? WriteLiteral(
                        $"new {ShellSpelling(instance.ShellTypeDescriptor.ShellTypeName)}",
                        StateOnly(instance),
                        depth,
                        types)
                    : WriteAnonymous(StateOnly(instance), depth, types);

            case ToshStructInstance structure:
                return Nameable(structure.Definition.Name, types)
                    ? WriteLiteral(
                        $"new {ShellSpelling(structure.ShellTypeDescriptor.ShellTypeName)}",
                        structure.GetMembers(),
                        depth,
                        types)
                    : WriteAnonymous(structure.GetMembers(), depth, types);

            // Anything else with members — a `FileSystemEntry`, a CLR library's own type —
            // becomes an *anonymous* record. TON may only name types the reading program
            // declares, so naming this one would emit a document its own reader must refuse;
            // and its computed members (`Magnitude`, `IsEmpty`) could not be assigned back even
            // if it could. An anonymous record is in the notation, carries the data, and is what
            // `to json` does with the same value.
            case IShellRecordObject shellObject:
                return WriteAnonymous(shellObject.GetMembers(), depth, types);

            case IDictionary dictionary:
                return WriteDictionary(dictionary, depth, types);

            // Before the sequence case: a set *is* an `IEnumerable`, so without this it was
            // written as an array and read back as one — a shape change disguised as a round
            // trip, which is the one thing a notation must not do quietly.
            case IEnumerable set when IsSet(value):
                return WriteSet(set, depth, types);

            case IEnumerable sequence when value is not string:
                return WriteSequence(sequence, depth, types);

            default:
                return WriteFallback(value, depth, types);
        }
    }

    /// <summary>
    /// A value with no shape of its own: normalised the way every other format normalises it,
    /// then written as whatever that yields.
    /// </summary>
    /// <remarks>
    /// Without this, `ls | to ton` refused — a `FileSystemEntry` is neither a declared shape nor
    /// a scalar, so it fell through to the refusal. `to json` has handled it all along, because
    /// `ShellDataSerializer.Normalize` knows the shape; TON now asks the same question of the
    /// same method rather than keeping a second opinion about what a value is.
    /// </remarks>
    private static string WriteFallback(object value, int depth, IShellNamedTypeView? types)
    {
        var normalized = ShellDataSerializer.Normalize(value);

        // A normalised dictionary here means the value was *record-shaped* — this branch is only
        // reached for values that were not dictionaries to begin with, since those are handled
        // above. So it is written as an anonymous record, which reads as the object it was and
        // gives `.Name` access back, rather than as a map wanting `["Name"]`.
        if (normalized is IDictionary<string, object?> fields)
        {
            return WriteAnonymous(fields.ToArray(), depth, types);
        }

        if (!ReferenceEquals(normalized, value))
        {
            return Write(normalized, depth, types);
        }

        return WriteScalar(value);
    }

    private static string WriteAnonymous(
        IReadOnlyList<KeyValuePair<string, object?>> members,
        int depth,
        IShellNamedTypeView? types)
    {
        if (members.Count == 0)
        {
            return "{| |}";
        }

        var written = members
            .Select(member => $"{member.Key} = {Write(member.Value, depth + 1, types)}")
            .ToArray();

        return Fits(written)
            ? $"{{| {string.Join(", ", written)} |}}"
            : $"{{|\n{Block(written, depth, separator: "")}\n{Pad(depth)}|}}";
    }

    /// <summary>
    /// Whether the reader will resolve <paramref name="name"/>, so the writer may use it.
    /// </summary>
    /// <remarks>
    /// A type declared inside a module — `ToastLib.Math.Point2D` — has a bare `ShellTypeName`
    /// the reader cannot resolve, and the notation has no spelling for the qualified one. Naming
    /// it produced a document this very implementation refused to read back. With no view to ask
    /// (a bare `Write` from a test) the answer is yes, which preserves the old behaviour for
    /// values that were always nameable.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> ShellSpellings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Boolean"] = "bool",
            ["Byte"] = "byte",
            ["SByte"] = "sbyte",
            ["Int16"] = "short",
            ["UInt16"] = "ushort",
            ["Int32"] = "int",
            ["UInt32"] = "uint",
            ["Int64"] = "long",
            ["UInt64"] = "ulong",
            ["Single"] = "float",
            ["Double"] = "double",
            ["Decimal"] = "decimal",
            ["String"] = "string",
            ["Char"] = "char",
            ["Object"] = "dynamic",
        };

    /// <summary>
    /// The descriptor's name with its type arguments in the spelling the language uses.
    /// </summary>
    /// <remarks>
    /// A bound generic describes itself as `Box&lt;Int32&gt;`, which reads back but names a CLR
    /// type in a notation whose whole rule is that it names none. `Box&lt;int&gt;` is the
    /// spelling a reader would write by hand and the one another implementation can make sense
    /// of without knowing .NET.
    /// </remarks>
    private static string ShellSpelling(string descriptorName)
    {
        var open = descriptorName.IndexOf('<');

        if (open < 0 || !descriptorName.EndsWith('>'))
        {
            return descriptorName;
        }

        var arguments = descriptorName[(open + 1)..^1]
            .Split(',')
            .Select(argument => argument.Trim())
            .Select(argument => ShellSpellings.TryGetValue(argument, out var alias) ? alias : argument);

        return $"{descriptorName[..open]}<{string.Join(", ", arguments)}>";
    }

    private static bool Nameable(string name, IShellNamedTypeView? types) =>
        types is null || types.TryGetNamedType(name, out _);

    /// <summary>
    /// A class instance's *state*, without its computed properties.
    /// </summary>
    /// <remarks>
    /// `prop Magnitude: double => System.Math.Sqrt(…)` is derived, not stored. Writing it out
    /// produced a document asserting a value nothing can accept back — and one that would be
    /// wrong the moment `X` or `Y` were edited by hand, which is exactly what a notation invites
    /// a reader to do.
    /// </remarks>
    private static IReadOnlyList<KeyValuePair<string, object?>> StateOnly(ToshClassInstance instance)
    {
        var computed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var definition = instance.Definition; definition is not null; definition = definition.BaseClass)
        {
            foreach (var property in definition.Properties.Where(property => property.IsComputed))
            {
                computed.Add(property.Name);
            }
        }

        return instance.GetMembers()
            .Where(member => !computed.Contains(member.Key))
            .ToArray();
    }

    private static string WriteVariant(ToshUnionVariantInstance variant, int depth, IShellNamedTypeView? types)
    {
        var union = variant.UnionDefinition;
        var members = variant.GetMembers()
            .Where(member => !string.Equals(member.Key, "Variant", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(member.Key, "Tag", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Written only when the payload cannot supply them: a unit variant carries nothing to
        // infer from, and a variant that names only some of the union's parameters leaves the
        // rest unknowable.
        var head = $"{union.Name}::{variant.VariantName}";

        if (NeedsTypeArguments(variant, members.Length) &&
            variant.TypeArguments is { Count: > 0 } arguments)
        {
            var ordered = union.TypeParameterNames
                .Select(name => arguments.TryGetValue(name, out var bound) ? bound : name);
            head += $"<{string.Join(", ", ordered)}>";
        }

        // Always positional. A union variant's field names are for *pattern matching* and
        // member access — construction takes positions only, so `Shape::Circle(radius = 2)`
        // parses and then fails to convert the named argument to the field's type. The
        // conformance corpus caught that: the writer was emitting a form the language could not
        // read back, which is the one bug a round trip is supposed to make impossible.
        //
        // Portability is not weakened the way it would be for a record. A variant's arity and
        // order are fixed by its declaration and there is no named alternative to fall back to,
        // so there is nothing a document could have said more robustly.
        return WritePositional(head, members, depth, types);
    }

    private static string WritePositional(
        string head,
        IReadOnlyList<KeyValuePair<string, object?>> members,
        int depth,
        IShellNamedTypeView? types)
    {
        var written = members.Select(member => Write(member.Value, depth + 1, types)).ToArray();

        return Fits(written)
            ? $"{head}({string.Join(", ", written)})"
            : $"{head}(\n{Block(written, depth, separator: ",")}\n{Pad(depth)})";
    }

    /// <summary>
    /// Whether the variant's own fields leave any of the union's type parameters unknown.
    /// </summary>
    private static bool NeedsTypeArguments(ToshUnionVariantInstance variant, int memberCount)
    {
        if (variant.UnionDefinition.TypeParameterNames.Count == 0)
        {
            return false;
        }

        // A unit variant infers nothing. Beyond that, a variant can only ever pin the parameters
        // its own fields name, so anything with fewer fields than the union has parameters
        // leaves at least one open — `Result::Ok(3)` says nothing about `E`.
        return memberCount < variant.UnionDefinition.TypeParameterNames.Count;
    }

    private static string WriteCall(
        string head,
        IReadOnlyList<KeyValuePair<string, object?>> members,
        int depth,
        IShellNamedTypeView? types)
    {
        if (members.Count == 0)
        {
            return $"{head}()";
        }

        var written = members
            .Select(member => $"{member.Key} = {Write(member.Value, depth + 1, types)}")
            .ToArray();

        return Fits(written)
            ? $"{head}({string.Join(", ", written)})"
            : $"{head}(\n{Block(written, depth, separator: ",")}\n{Pad(depth)})";
    }

    private static string WriteLiteral(
        string head,
        IReadOnlyList<KeyValuePair<string, object?>> members,
        int depth,
        IShellNamedTypeView? types)
    {
        if (members.Count == 0)
        {
            return $"{head} {{| |}}";
        }

        var written = members
            .Select(member => $"{member.Key} = {Write(member.Value, depth + 1, types)}")
            .ToArray();

        return Fits(written)
            ? $"{head} {{| {string.Join(", ", written)} |}}"
            : $"{head} {{|\n{Block(written, depth, separator: "")}\n{Pad(depth)}|}}";
    }

    private static string WriteDictionary(IDictionary dictionary, int depth, IShellNamedTypeView? types)
    {
        var written = new List<string>();

        foreach (DictionaryEntry entry in dictionary)
        {
            written.Add($"{Write(entry.Key, depth + 1, types)} => {Write(entry.Value, depth + 1, types)}");
        }

        if (written.Count == 0)
        {
            return "{% %}";
        }

        return Fits(written)
            ? $"{{% {string.Join(", ", written)} %}}"
            : $"{{%\n{Block(written, depth, separator: ",")}\n{Pad(depth)}%}}";
    }

    private static bool IsSet(object value) =>
        value.GetType().GetInterfaces().Any(contract =>
            contract.IsGenericType &&
            contract.GetGenericTypeDefinition() == typeof(ISet<>));

    private static string WriteSet(IEnumerable set, int depth, IShellNamedTypeView? types)
    {
        var written = set.Cast<object?>()
            .Select(item => Write(item, depth + 1, types))
            .ToArray();

        if (written.Length == 0)
        {
            return "{: :}";
        }

        return Fits(written)
            ? $"{{: {string.Join(", ", written)} :}}"
            : $"{{:\n{Block(written, depth, separator: ",")}\n{Pad(depth)}:}}";
    }

    private static string WriteSequence(IEnumerable sequence, int depth, IShellNamedTypeView? types)
    {
        var written = sequence.Cast<object?>()
            .Select(item => Write(item, depth + 1, types))
            .ToArray();

        if (written.Length == 0)
        {
            return "[]";
        }

        return Fits(written)
            ? $"[{string.Join(", ", written)}]"
            : $"[\n{Block(written, depth, separator: ",")}\n{Pad(depth)}]";
    }

    private static string WriteScalar(object value)
    {
        return value switch
        {
            byte or sbyte or short or ushort or int or uint or long or ulong
                => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
            float or double or decimal => Format(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
            // Round-trip format, not the current culture's. `ToString()` gave
            // "1/8/2026 8:57:52 PM -05:00" — unparseable anywhere else, and different on a
            // machine with different regional settings, which is not a thing a notation may do.
            DateTime moment => Quote(moment.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeOffset moment => Quote(moment.ToString("O", CultureInfo.InvariantCulture)),
            TimeSpan span => Quote(span.ToString("c", CultureInfo.InvariantCulture)),
            Guid or Uri => Quote(value.ToString() ?? string.Empty),
            string text => Quote(text),

            // Refused rather than stringified. `value.ToString()` on an unrecognised object
            // yields its type name, so the document would have said `"Some.Namespace.Thing"` and
            // looked like data — the same silent-wrongness as a dropped initialiser, and exactly
            // what "round-trips or refuses" exists to prevent. It is how the compiled backend's
            // divergence showed up: there a declared record is an emitted CLR class, so none of
            // the shell-shape cases above match it.
            _ => throw ToshDiagnosticException.Create(new ToshDiagnostic(
                Code: "tosh.ton.unrepresentable",
                Title: $"A value of type '{value.GetType().Name}' has no TON spelling.",
                Help: "TON writes declared records, classes, structs, enums, union variants, "
                    + "quantities and the built-in scalars and collections. Project the parts you "
                    + "need, or use `to json`.")),
        };
    }

    private static string Format(decimal value) =>
        value.ToString("0.################", CultureInfo.InvariantCulture);

    private static bool Fits(IReadOnlyCollection<string> parts) =>
        parts.Count <= 3 && parts.All(part => !part.Contains('\n') && part.Length <= 40);

    private static string Block(IReadOnlyList<string> parts, int depth, string separator)
    {
        var pad = Pad(depth + 1);
        return string.Join("\n", parts.Select(part => $"{pad}{part}{separator}"));
    }

    private static string Pad(int depth) => string.Concat(Enumerable.Repeat(Indent, depth));

    private static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        builder.Append('"');

        foreach (var character in text)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(character); break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
