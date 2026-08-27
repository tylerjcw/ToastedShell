using System.Reflection;
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
    /// How many properties a CLR object shows before the rest are dropped. An
    /// interpolation hole is a sentence, not a report.
    /// </summary>
    public const int MaximumProperties = 24;

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

    /// <summary>The method <see cref="DisplayTraitName"/> requires.</summary>
    public const string DisplayMethodName = "render";

    /// <summary>Renders <paramref name="value"/> with its default format.</summary>
    public static string Render(object? value) => Render(value, format: null);

    /// <summary>
    /// Whether the language renders <paramref name="value"/> as a scalar — a thing with a
    /// name rather than a structure with parts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked by display, so that one place decides what a scalar is instead of two guessing
    /// separately. `TOAST-0021`: `DisplayEngine` expanded anything with readable properties,
    /// and an enum member has them — so a `Color.Red` cell became a nested table of
    /// <c>EnumTypeName</c>, <c>ShellTypeDescriptor</c> and <c>UnderlyingValue</c>, which is
    /// the value's implementation rather than the value.
    /// </para>
    /// <para>
    /// Implemented by *running* the scalar writer rather than by restating its cases, which
    /// costs a small builder per call and buys the guarantee that the two can never
    /// disagree. A restated list is a list that drifts.
    /// </para>
    /// </remarks>
    public static bool RendersAsScalar(object? value)
        => value is not null && TryWriteScalar(new StringBuilder(), value, format: null, nested: false);

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
    /// <summary>
    /// Renders an interpolation hole: the format clause, then the alignment — `TOAST-0022`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One entry point for both backends, for the same reason <see cref="Render(object?, string?)"/>
    /// is one: the compiled side reached the renderer but never passed the hole's clauses to
    /// it, so `$"{42:X}"` was `2A` interpreted and `42` compiled. A second implementation is
    /// how that happened; a shared one is what stops it happening again.
    /// </para>
    /// <para>
    /// Alignment pads the rendered text and is applied after formatting, so a clause that
    /// changes the text's length is padded at its final width. Zero means no padding, which
    /// lets the compiled caller pass a plain <c>int</c> rather than a nullable.
    /// </para>
    /// </remarks>
    public static string RenderHole(object? value, string? format, int alignment)
        => Align(Render(value, format), alignment);

    /// <summary>Pads rendered text to an interpolation hole's alignment.</summary>
    /// <remarks>Positive pads on the left, negative on the right, zero not at all.</remarks>
    public static string Align(string text, int alignment) => alignment switch
    {
        0 => text,
        > 0 => text.PadLeft(alignment),
        _ => text.PadRight(-alignment),
    };

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

            // Temporal defaults are named rather than inherited. The invariant culture's
            // own default is `08/17/2026 12:00:00` — month-first, which is a locale
            // convention wearing "invariant" as a disguise. A specified default has to be
            // one an author in any country reads the same way.
            case DateTime dateTime when string.IsNullOrEmpty(format):
                builder.Append(dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                return true;

            case DateTimeOffset dateTimeOffset when string.IsNullOrEmpty(format):
                builder.Append(dateTimeOffset.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture));
                return true;

            case DateOnly dateOnly when string.IsNullOrEmpty(format):
                builder.Append(dateOnly.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                return true;

            case TimeOnly timeOnly when string.IsNullOrEmpty(format):
                builder.Append(timeOnly.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                return true;

            case DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan or Guid:
                builder.Append(FormatInvariant(value, format));
                return true;

            // Value types that are their own rendering. Each is `IFormattable` with a
            // meaningful `ToString`, and each is *also* record-shaped — a Quantity exposes
            // value, unit and category as readable members — so without naming them here
            // the container walk claims them and `$"{$power}"` gives
            // `{| value = 483.06, unit = "MW", … |}` where the reader wanted `483.06 MW`.
            case Units.Quantity or ToshVector or ToshMatrix or StorageSize or TemporalAmount:
                builder.Append(FormatInvariant(value, format));
                return true;

            // A line of text from a command *is* its text. It had only a display profile,
            // so once rendering stopped going through the formatter a redirected line
            // became `ShellTextLine { Text = "alpha" }` — the wrapper, not the line.
            case ShellTextLine line:
                RejectFormat(format, "text line");
                WriteString(builder, line.Text, format: null, nested);
                return true;

            // A type used as a value names itself. `TS-P1-23`: a descriptor exposes Name,
            // FullName and Namespace as ordinary readable properties, so the record walk
            // claimed it and interpolation rendered a structure where the reader wrote a
            // type.
            case IShellNamedType namedType:
                RejectFormat(format, "type");
                builder.Append(((IShellStaticType)namedType).ShellTypeName);
                return true;

            case Type clrType:
                RejectFormat(format, "type");
                builder.Append(clrType.FullName ?? clrType.Name);
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

            case IShellInvocableObject invocable when TryWriteDeclaredRendering(builder, invocable, depth, visited):
                return;

            // `TOAST-0022`. A compiled class is a real emitted CLR type, not a
            // `ToshClassInstance`, so it cannot answer `IShellInvocableObject` — and fell
            // through to the CLR-object path, which prints the type name. `Display` was the
            // first place that showed, but the gap is the object model, not the renderer.
            case not null when TryWriteEmittedRendering(builder, value, depth, visited):
                return;

            // And with no declaration to render through, an emitted class still describes
            // itself the way the interpreted one does — `Plain { N = 5 }`, not `Plain`.
            case not null when TryWriteEmittedStructure(builder, value, depth, visited):
                return;

            // Records before dictionaries. A `{| … |}` literal is an `ExpandoObject`, and
            // **a string-keyed dictionary is a record** — that is the existing convention,
            // encoded in `ShellRecordUtilities`, and a Tōast dictionary literal is
            // object-keyed (`Dictionary<object, object>`) precisely so the two are
            // distinguishable. Getting this backwards rendered `{| power = 483.06 MW |}` as
            // `{% "power" => 483.06 MW %}`.
            case IShellRecordObject
                 or System.Dynamic.ExpandoObject
                 or IDictionary<string, object?>
                 or IReadOnlyDictionary<string, object?>:
                WriteRecordLike(builder, value, depth, visited);
                return;

            case IDictionary dictionary:
                WritePairs(builder, dictionary, depth, visited);
                return;

            case IEnumerable sequence:
                WriteSequence(builder, sequence.Cast<object?>(), "[", "]", depth, visited);
                return;

            // An exception is a value a program can hold — `catch` binds one — so it
            // renders like any other named value rather than as its `ToString`, which is a
            // stack trace and belongs in a diagnostic, not in a sentence.
            case Exception exception:
                builder.Append(TypeNameOf(exception)).Append(" { Message = ");
                WriteString(builder, exception.Message, format: null, nested: true);
                builder.Append(" }");
                return;

            default:
                WriteClrObject(builder, value, depth, visited);
                return;
        }
    }

    /// <summary>
    /// A CLR object with no rendering of its own renders like a class: its type name and
    /// its readable state.
    /// </summary>
    /// <remarks>
    /// **A type that overrides <c>ToString</c> is taken at its word, whole.** That is the
    /// CLR's own way of saying how a value reads, and overruling it produced nonsense:
    /// <c>pwd</c> returns a <c>DirectoryInfo</c>, whose <c>ToString</c> is the path, and
    /// walking its properties instead gave <c>DirectoryInfo { Attributes = Directory, … }</c>
    /// where a program wanted <c>/home/…</c>.
    ///
    /// Taken *whole* is the important half: a C# <c>record</c>'s generated <c>ToString</c>
    /// renders its strings unquoted, and the answer is to accept its rendering entirely
    /// rather than to mix its conventions into ours.
    ///
    /// Only a type that has not overridden it is walked, and the property count is capped —
    /// an interpolation hole is a sentence, and a value with two hundred properties should
    /// not silently become a paragraph.
    /// </remarks>
    private static void WriteClrObject(StringBuilder builder, object value, int depth, HashSet<object>? visited)
    {
        var type = value.GetType();

        if (type.GetMethod(nameof(ToString), Type.EmptyTypes)?.DeclaringType != typeof(object))
        {
            builder.Append(value.ToString() ?? type.Name);
            return;
        }

        var properties = type
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Take(MaximumProperties)
            .ToArray();

        if (properties.Length == 0)
        {
            var text = value.ToString();
            builder.Append(string.Equals(text, type.FullName, StringComparison.Ordinal) || text is null
                ? type.Name
                : text);
            return;
        }

        builder.Append(type.Name).Append(" { ");

        for (var index = 0; index < properties.Length; index++)
        {
            if (index > 0) { builder.Append(", "); }
            builder.Append(properties[index].Name).Append(" = ");

            object? propertyValue;

            try
            {
                propertyValue = properties[index].GetValue(value);
            }
            catch (Exception error)
            {
                // A getter that throws is the property's problem, not the reader's, and
                // rendering is total.
                propertyValue = $"<{error.GetType().Name}>";
            }

            Write(builder, propertyValue, format: null, depth + 1, nested: true, visited);
        }

        builder.Append(" }");
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
    /// <summary>
    /// Renders a *compiled* class through its own declaration — <c>TOAST-0022</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies the same rule <see cref="ToshClassInstance"/> applies interpreted, and in the
    /// same order: the <c>Display</c> trait's method first, then a declared <c>ToString</c>.
    /// A trait is emitted as a CLR interface and the class implements it, so "uses Display"
    /// is a real interface check rather than a guess from the presence of a method — a class
    /// that happens to declare <c>render</c> without the trait is not a Display.
    /// </para>
    /// <para>
    /// Only types the compiler emitted are considered. Without that guard this would change
    /// how every CLR object renders, which is a far larger claim than the one being fixed.
    /// </para>
    /// </remarks>
    private static bool TryWriteEmittedRendering(
        StringBuilder builder,
        object value,
        int depth,
        HashSet<object>? visited)
    {
        var type = value.GetType();
        if (type.GetCustomAttribute<ToshTypeAttribute>() is null)
        {
            return false;
        }

        var method = FindEmittedRenderMethod(type);
        if (method is null)
        {
            return false;
        }

        var rendered = method.Invoke(value, Array.Empty<object?>());

        if (rendered is string text)
        {
            builder.Append(text);
            return true;
        }

        if (rendered is null)
        {
            return false;
        }

        Write(builder, rendered, format: null, depth + 1, nested: false, visited);
        return true;
    }

    /// <summary>
    /// Renders a compiled class or record structurally — <c>TOAST-0022</c>.
    /// </summary>
    /// <remarks>
    /// The interpreted instance is an <see cref="IShellRecordObject"/>, so the renderer walks
    /// its members; an emitted type is a plain CLR type and walked nothing, printing its name.
    /// The properties are read reflectively here rather than by teaching
    /// <c>ShellRecordUtilities</c> about CLR objects, because that utility answers for member
    /// access as well as rendering and this is a claim about rendering only.
    /// </remarks>
    private static bool TryWriteEmittedStructure(
        StringBuilder builder,
        object value,
        int depth,
        HashSet<object>? visited)
    {
        var type = value.GetType();
        if (type.GetCustomAttribute<ToshTypeAttribute>() is not { } marker ||
            marker.Kind is not ("class" or "record" or "struct"))
        {
            return false;
        }

        // A stored `prop` is emitted as a field and a computed one as a real CLR property, so
        // both are walked.
        //
        // Two rules, and they pull in opposite directions. A `prop` that shadows a base
        // class's is *two* CLR members and reflection returns both, so the most-derived one
        // must win — otherwise `class Circle extends Shape` with `K` on each rendered
        // `Circle { K = c, K = s }`. But the interpreted instance lists inherited members
        // *first*, so `Derived { A = 1, B = 2 }` rather than `{ B = 2, A = 1 }`. So the
        // hierarchy is walked twice: downwards to decide which member each name resolves to,
        // then upwards to lay them out.
        const BindingFlags declared =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var levels = new List<Type>();
        for (var level = type; level is not null && level != typeof(object); level = level.BaseType)
        {
            levels.Add(level);
        }

        var chosen = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        foreach (var level in levels)
        {
            foreach (var member in VisibleMembers(level, declared))
            {
                chosen.TryAdd(member.Name, member);
            }
        }

        var fields = new List<KeyValuePair<string, object?>>(chosen.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        for (var index = levels.Count - 1; index >= 0; index--)
        {
            foreach (var member in VisibleMembers(levels[index], declared))
            {
                if (!emitted.Add(member.Name))
                {
                    continue;
                }

                var resolved = chosen[member.Name];
                fields.Add(new KeyValuePair<string, object?>(
                    resolved.Name,
                    resolved is FieldInfo field
                        ? field.GetValue(value)
                        : ((PropertyInfo)resolved).GetValue(value)));
            }
        }

        // A struct describes itself the way a class does — `Pt { X = 1 }` — rather than the
        // way a record does. Excluding it from this path left it on the CLR-object fallback,
        // rendering `p.Pt`: the assembly's namespace, for a type the reader declared.
        WriteFields(builder, fields, marker.Kind == "record", type.Name, depth, visited);
        return true;
    }

    /// <summary>The renderable members one level of an emitted type declares.</summary>
    /// <remarks>
    /// Fields and properties both, because a stored `prop` becomes a field and a computed one
    /// a property. Compiler-generated backing fields are skipped — they carry the same value
    /// under a name nobody wrote.
    /// </remarks>
    private static IEnumerable<MemberInfo> VisibleMembers(Type level, BindingFlags flags)
    {
        foreach (var field in level.GetFields(flags))
        {
            if (!field.IsSpecialName && !field.Name.StartsWith('<'))
            {
                yield return field;
            }
        }

        foreach (var property in level.GetProperties(flags))
        {
            if (property.GetIndexParameters().Length == 0 && property.CanRead)
            {
                yield return property;
            }
        }
    }

    /// <summary>The declaration an emitted type renders through, or null for neither.</summary>
    private static MethodInfo? FindEmittedRenderMethod(Type type)
    {
        var usesDisplay = Array.Exists(
            type.GetInterfaces(),
            candidate => string.Equals(candidate.Name, DisplayTraitName, StringComparison.Ordinal));

        if (usesDisplay)
        {
            var render = type.GetMethod(
                DisplayMethodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                Type.EmptyTypes,
                modifiers: null);

            if (render is not null)
            {
                return render;
            }
        }

        // A declared `ToString` — one the author wrote. The compiler emits a default that
        // answers the class's name (`TOAST-0065`), and that is the fallback structural
        // rendering exists to beat rather than a rendering declaration to prefer over it, so
        // it is told apart by the compiler-generated marker rather than by who declared it.
        var toString = type.GetMethod(nameof(ToString), Type.EmptyTypes);
        if (toString is null ||
            toString.DeclaringType != type ||
            toString.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null)
        {
            return null;
        }

        return toString;
    }

    private static bool TryWriteDeclaredRendering(
        StringBuilder builder,
        IShellInvocableObject invocable,
        int depth,
        HashSet<object>? visited)
    {
        if (!invocable.TryGetOwnRendering(out var rendered))
        {
            return false;
        }

        if (rendered is string text)
        {
            builder.Append(text);
            return true;
        }

        // Written as a nested value at the next depth rather than pasted in, so a
        // declaration that returns its own receiver elides through the cycle guard instead
        // of recursing forever.
        Write(builder, rendered, format: null, depth + 1, nested: false, visited);
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
        // Through the shared utility rather than `GetMembers` directly, so the sentinel
        // keys it filters stay filtered in one place.
        ShellRecordUtilities.TryGetVisibleFields(value, out var fields);

        WriteFields(
            builder,
            fields,
            isRecord: value is not IShellInvocableObject,
            TypeNameOf(value),
            depth,
            visited);
    }

    /// <summary>
    /// Writes the <c>Name { … }</c> / <c>{| … |}</c> body once, for either backend.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="WriteRecordLike"/> so a compiled class can render in the same
    /// shape without a second copy of it — `TOAST-0022`. A second copy is how the two
    /// backends came to disagree about interpolation clauses, and this is the same file.
    /// </remarks>
    private static void WriteFields(
        StringBuilder builder,
        IReadOnlyList<KeyValuePair<string, object?>> fields,
        bool isRecord,
        string typeName,
        int depth,
        HashSet<object>? visited)
    {
        if (!isRecord)
        {
            builder.Append(typeName).Append(' ');
        }

        builder.Append(isRecord ? "{| " : "{ ");
        var first = true;

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
        => new($"A value of type '{kindName}' cannot be formatted with '{format}'.");
}
