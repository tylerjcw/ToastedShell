using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tosh.Runtime;

/// <summary>
/// Turns a Tōast value into a string — the language operation behind <c>$"{x}"</c>.
/// </summary>
/// <remarks>
/// <para>
/// `TOAST-0014`, Phase A. **Rendering is not display.** Display is how TōSh paints a value
/// on a terminal: tables, colour, column widths, profiles, themes. Rendering is what a
/// program gets back from an interpolation hole and can write to a file or send over a
/// socket. Display may call rendering; rendering must never call display.
/// </para>
/// <para>
/// That separation is enforced by construction rather than by discipline: this type is
/// static, takes no registry, holds no preferences, and has no reference to
/// <c>DisplayProfileRegistry</c>, <c>DisplayPreferences</c> or <c>DisplayEngine</c>. There
/// is no configuration it *could* consult. That is the whole point — the defect being fixed
/// is that <c>$"{$d}"</c> produced three different strings depending on
/// <c>$tosh.Config.Display.DateTime.ScalarMode</c>, changed mid-script.
/// </para>
/// <para>
/// Rendering is **total**: every value renders, and no value renders as a failure. A
/// diagnostic that cannot render the value it is about is worse than an imperfect string.
/// The one exception is a format clause the value cannot honour, which is an error —
/// see <see cref="Render(object?, string?)"/>.
/// </para>
/// <para>
/// Written against `docs/plan/SPEC_DRAFT_value_rendering.md` §3–§8, and **nothing calls it
/// yet**. It is built and pinned against the specification first, so the behaviour change
/// lands as one reviewable flip of the four call sites rather than as a rewrite whose
/// correctness is argued from the diff.
/// </para>
/// </remarks>
public static class ToastRenderer
{
    /// <summary>
    /// How deep rendering descends before eliding. Fixed rather than configurable, because
    /// a configurable depth would make a program's output depend on configuration — the
    /// defect this type exists to remove.
    /// </summary>
    public const int MaximumDepth = 8;

    /// <summary>
    /// The trait a type implements to control its own rendering, and the method it
    /// requires.
    /// </summary>
    /// <remarks>
    /// A trait rather than a magic method name, because rendering is a capability a type
    /// *declares*: the compiler can check it, and a native target can dispatch it without
    /// reflection. The check goes through <see cref="IShellTypeCheckable"/>, so the value
    /// answers for itself and this type needs neither the engine nor a registered hook.
    /// </remarks>
    public const string DisplayTraitName = "Display";

    private const string DisplayMethodName = "render";

    /// <summary>Renders <paramref name="value"/> with its default format.</summary>
    public static string Render(object? value) => Render(value, format: null);

    /// <summary>
    /// Renders <paramref name="value"/>, optionally with a format clause.
    /// </summary>
    /// <remarks>
    /// A bare hole and a hole with a clause are the *same operation* with a different
    /// argument — a bare hole is one whose format is the value's specified default. They are
    /// not two mechanisms, which is what let them drift far enough apart that
    /// <c>$"{$d}"</c> and <c>$"{$d:HH:mm:ss}"</c> disagreed about the same value.
    /// </remarks>
    /// <exception cref="FormatException">
    /// The value cannot honour <paramref name="format"/>. Deliberate: a clause is an
    /// explicit instruction, and silently ignoring one — which is what happens today —
    /// produces text nobody asked for from a program that reports success.
    /// </exception>
    public static string Render(object? value, string? format)
    {
        var builder = new StringBuilder();
        Write(builder, value, format, depth: 0, nested: false, visited: null);
        return builder.ToString();
    }

    private static void Write(
        StringBuilder builder,
        object? value,
        string? format,
        int depth,
        bool nested,
        HashSet<object>? visited)
    {
        if (value is null)
        {
            RejectFormat(format, "null");
            builder.Append("null");
            return;
        }

        if (TryWriteScalar(builder, value, format, nested))
        {
            return;
        }

        if (depth >= MaximumDepth)
        {
            RejectFormat(format, value.GetType().Name);
            builder.Append('…');
            return;
        }

        // Reference identity, not equality: a record that merely equals an ancestor is a
        // different value and must render in full.
        var tracked = !value.GetType().IsValueType;

        if (tracked)
        {
            visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);

            if (!visited.Add(value))
            {
                builder.Append('…');
                return;
            }
        }

        try
        {
            WriteComposite(builder, value, format, depth, visited);
        }
        finally
        {
            if (tracked)
            {
                visited!.Remove(value);
            }
        }
    }

    // ---------------------------------------------------------------- scalars

    private static bool TryWriteScalar(StringBuilder builder, object value, string? format, bool nested)
    {
        switch (value)
        {
            case bool boolean:
                RejectFormat(format, "bool");
                builder.Append(boolean ? "true" : "false");
                return true;

            case string text:
                WriteString(builder, text, format, nested);
                return true;

            case char character:
                RejectFormat(format, "char");
                if (nested) { builder.Append('\'').Append(character).Append('\''); }
                else { builder.Append(character); }
                return true;

            // A shell-defined enum member renders as its member name. Without this it fell
            // through to the generic object walk and rendered its own implementation —
            // Definition, EnumTypeName, ShellTypeDescriptor, UnderlyingValue — where the
            // reader had written `Color.Red`.
            case IShellEnumValue enumValue:
                RejectFormat(format, "enum");
                // `Name` already carries a flags combination's composed member names —
                // the declaration composes them when it builds the value (`TS-P3-14`), so
                // rendering does not re-derive them and cannot disagree.
                builder.Append(enumValue.Name);
                return true;

            case Enum clrEnum:
                RejectFormat(format, "enum");
                builder.Append(clrEnum.ToString());
                return true;

            case float single:
                WriteDouble(builder, single, format);
                return true;

            case double real:
                WriteDouble(builder, real, format);
                return true;

            case byte or sbyte or short or ushort or int or uint or long or ulong or decimal or BigInteger:
                builder.Append(FormatInvariant(value, format));
                return true;

            case DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan or Guid:
                builder.Append(FormatInvariant(value, format));
                return true;

            case Uri uri:
                RejectFormat(format, "uri");
                builder.Append(uri.ToString());
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// A string is its own characters at the top level, and quoted when nested.
    /// </summary>
    /// <remarks>
    /// The asymmetry is deliberate and is the standard one. At the top level the caller is
    /// putting text into a sentence; nested, they are looking at a structure, and unquoted
    /// elements make it ambiguous — <c>["a b", "c"]</c> is indistinguishable from three
    /// elements, and the string <c>"null"</c> from the value <c>null</c>.
    /// </remarks>
    private static void WriteString(StringBuilder builder, string text, string? format, bool nested)
    {
        RejectFormat(format, "string");

        if (!nested)
        {
            builder.Append(text);
            return;
        }

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
    }

    /// <summary>
    /// Floating point renders in the shortest form that round-trips, with the special
    /// values named rather than spelled by the platform.
    /// </summary>
    /// <remarks>
    /// Negative zero keeps its sign. It is a different value from positive zero, and a
    /// rendering that collapsed them would make output depend on how a zero was reached —
    /// exactly the kind of thing an implementation loses by accident and nobody notices
    /// until a test does.
    /// </remarks>
    private static void WriteDouble(StringBuilder builder, double value, string? format)
    {
        if (format is { Length: > 0 })
        {
            builder.Append(FormatInvariant(value, format));
            return;
        }

        if (double.IsNaN(value)) { builder.Append("NaN"); return; }
        if (double.IsPositiveInfinity(value)) { builder.Append("Infinity"); return; }
        if (double.IsNegativeInfinity(value)) { builder.Append("-Infinity"); return; }

        if (value == 0 && double.IsNegative(value))
        {
            builder.Append("-0");
            return;
        }

        builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------- composites

    private static void WriteComposite(
        StringBuilder builder,
        object value,
        string? format,
        int depth,
        HashSet<object>? visited)
    {
        RejectFormat(format, value.GetType().Name);

        switch (value)
        {
            case ToshRange range:
                builder.Append(range.Start).Append("..");
                if (range.End is { } end) { builder.Append(end); }
                return;

            // Both tuple shapes, and before the record path: `ToshTuple` is an
            // `IShellRecordObject` whose members are `Count`, `Item1`, `Item2` … so the
            // record writer would render `(1, "a")` as `{| Count = 2, Item1 = 1, … |}` —
            // the ValueTuple internals the reader never wrote.
            case ToshTuple toshTuple:
                WriteSequence(builder, toshTuple, "(", ")", depth, visited);
                return;

            case ITuple tuple:
                WriteSequence(builder, EnumerateTuple(tuple), "(", ")", depth, visited);
                return;

            case IShellInvocableObject invocable when TryWriteDeclaredRendering(builder, value, invocable, depth, visited):
                return;

            // Records before dictionaries, and `ExpandoObject` named outright, because a
            // `{| … |}` literal *is* one. It implements `IDictionary<string, object>` but
            // not the non-generic `IDictionary`, so without this it fell past the pair
            // writer into the sequence writer and rendered as a list of key-value pairs.
            case IShellRecordObject or System.Dynamic.ExpandoObject:
                WriteRecordLike(builder, value, depth, visited);
                return;

            case IDictionary dictionary:
                WritePairs(builder, dictionary, depth, visited);
                return;

            case IEnumerable sequence:
                WriteSequence(builder, sequence.Cast<object?>(), "[", "]", depth, visited);
                return;

            default:
                builder.Append(value.ToString() ?? value.GetType().Name);
                return;
        }
    }

    private static void WriteSequence(
        StringBuilder builder,
        IEnumerable<object?> items,
        string open,
        string close,
        int depth,
        HashSet<object>? visited)
    {
        builder.Append(open);
        var first = true;

        foreach (var item in items)
        {
            if (!first) { builder.Append(", "); }
            first = false;
            Write(builder, item, format: null, depth + 1, nested: true, visited);
        }

        builder.Append(close);
    }

    private static void WritePairs(StringBuilder builder, IDictionary dictionary, int depth, HashSet<object>? visited)
    {
        builder.Append("{% ");
        var first = true;

        foreach (DictionaryEntry entry in dictionary)
        {
            if (!first) { builder.Append(", "); }
            first = false;
            Write(builder, entry.Key, format: null, depth + 1, nested: true, visited);
            builder.Append(" => ");
            Write(builder, entry.Value, format: null, depth + 1, nested: true, visited);
        }

        builder.Append(first ? "%}" : " %}");
    }

    /// <summary>
    /// Renders through the type's own <c>Display</c> implementation, or its declared
    /// <c>ToString</c>, if it has either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ToString</c> is accepted as a fallback so that classes written before the trait
    /// existed keep rendering as their authors intended. <c>Display</c> is what the
    /// specification teaches; <c>ToString</c> is what it tolerates.
    /// </para>
    /// <para>
    /// The result is written as a *nested* value at the next depth rather than pasted in,
    /// so a <c>render</c> that returns its own receiver elides through the cycle guard
    /// instead of recursing forever. A well-behaved implementation returns a string, which
    /// this writes verbatim.
    /// </para>
    /// </remarks>
    private static bool TryWriteDeclaredRendering(
        StringBuilder builder,
        object value,
        IShellInvocableObject invocable,
        int depth,
        HashSet<object>? visited)
    {
        string method;

        if (value is IShellTypeCheckable checkable &&
            checkable.IsInstanceOf(DisplayTraitName) &&
            invocable.HasInstanceMember(DisplayMethodName))
        {
            method = DisplayMethodName;
        }
        else if (invocable.HasInstanceMember(nameof(ToString)))
        {
            method = nameof(ToString);
        }
        else
        {
            return false;
        }

        var result = invocable.InvokeInstanceMethod(method, Array.Empty<object?>());

        if (result.ReturnedVoid)
        {
            return false;
        }

        if (result.Value is string text)
        {
            builder.Append(text);
            return true;
        }

        Write(builder, result.Value, format: null, depth + 1, nested: false, visited);
        return true;
    }

    /// <summary>
    /// A record renders in record-literal syntax; a class or struct renders with its type
    /// name in front.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type name is what makes a class distinguishable from a record. Without it both
    /// render <c>{| N = 5 |}</c> and a reader cannot tell a <c>Point</c> from an anonymous
    /// record with the same fields.
    /// </para>
    /// <para>
    /// **The discriminator is imperfect and knowingly so.** `IShellTypedObject`'s descriptor
    /// answers `ShellIsClass` *true* for records as well as classes, so it cannot separate
    /// them; what separates them here is that a class and a struct carry methods
    /// (<see cref="IShellInvocableObject"/>) and a record does not. That is a proxy for the
    /// question rather than the question, and it wants a real discriminator on the
    /// descriptor.
    /// </para>
    /// </remarks>
    private static void WriteRecordLike(
        StringBuilder builder,
        object value,
        int depth,
        HashSet<object>? visited)
    {
        var isRecord = value is not IShellInvocableObject;

        if (!isRecord)
        {
            builder.Append(TypeNameOf(value)).Append(' ');
        }

        builder.Append(isRecord ? "{| " : "{ ");
        var first = true;

        // Through the shared utility rather than `GetMembers` directly, so the sentinel
        // keys it filters stay filtered in one place.
        ShellRecordUtilities.TryGetVisibleFields(value, out var fields);

        foreach (var field in fields)
        {
            if (!first) { builder.Append(", "); }
            first = false;
            builder.Append(field.Key).Append(" = ");
            Write(builder, field.Value, format: null, depth + 1, nested: true, visited);
        }

        if (first) { builder.Append(isRecord ? "|}" : "}"); }
        else { builder.Append(isRecord ? " |}" : " }"); }
    }

    // ----------------------------------------------------------------- pieces

    private static string TypeNameOf(object value)
        => value is IShellTypedObject typed
            ? typed.ShellTypeDescriptor.ShellTypeName
            : value.GetType().Name;

    private static IEnumerable<object?> EnumerateTuple(ITuple tuple)
    {
        for (var index = 0; index < tuple.Length; index++)
        {
            yield return tuple[index];
        }
    }

    /// <summary>
    /// Formats through the value's own formatter, always invariant.
    /// </summary>
    /// <remarks>
    /// Invariant rather than current-culture, so a machine whose locale uses a comma
    /// decimal separator still renders <c>3.14</c>. A program's output must not depend on
    /// where it runs, for the same reason it must not depend on how the shell is
    /// configured.
    /// </remarks>
    private static string FormatInvariant(object value, string? format)
    {
        if (value is not IFormattable formattable)
        {
            RejectFormat(format, value.GetType().Name);
            return value.ToString() ?? value.GetType().Name;
        }

        try
        {
            return formattable.ToString(NormalizeFormat(format), CultureInfo.InvariantCulture)
                   ?? value.ToString()
                   ?? value.GetType().Name;
        }
        catch (FormatException)
        {
            throw UnhonourableFormat(format, value.GetType().Name);
        }
    }

    private static string? NormalizeFormat(string? format)
        => string.IsNullOrEmpty(format) ? null : format;

    private static void RejectFormat(string? format, string kindName)
    {
        if (format is { Length: > 0 })
        {
            throw UnhonourableFormat(format, kindName);
        }
    }

    private static FormatException UnhonourableFormat(string? format, string kindName)
        => new($"'{format}' is not a format {kindName} can honour.");
}
