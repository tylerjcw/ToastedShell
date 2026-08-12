using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace Tosh.Runtime;

public sealed class ObjectFormatter
{
    private readonly DisplayProfileRegistry _profiles;

    public ObjectFormatter(DisplayProfileRegistry? profiles = null)
    {
        _profiles = profiles ?? DisplayProfileRegistry.CreateDefault();
    }

    public DisplayProfileRegistry Profiles => _profiles;

    public ObjectRenderStyle Style { get; set; } = ObjectRenderStyle.Compact;

    public string Format(object? value)
    {
        return Format(value, new ObjectFormattingOptions(Style));
    }

    public string Format(object? value, ObjectFormattingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return FormatValue(value, options, depth: 0, visited, isRoot: true);
    }

    public string FormatMany(IReadOnlyList<object?> values)
    {
        return new DisplayEngine(this).RenderMany(values);
    }

    private string FormatValue(
        object? value,
        ObjectFormattingOptions options,
        int depth,
        HashSet<object> visited,
        bool isRoot)
    {
        if (value is null)
        {
            return "null";
        }

        if (TryRenderProfile(value, options, isRoot ? DisplaySurface.Root : DisplaySurface.Nested, out var profileText))
        {
            return profileText;
        }

        if (TryFormatSimple(value, isRoot, out var simpleText))
        {
            return simpleText;
        }

        if (value is ShellCommandDescriptor descriptor)
        {
            return FormatShellCommandDescriptor(descriptor);
        }

        if (value is FormatterStatus formatterStatus)
        {
            return FormatFormatterStatus(formatterStatus);
        }

        if (value is CommandHistoryEntry historyEntry)
        {
            return FormatHistoryEntry(historyEntry);
        }

        if (value is ObjectInspection inspection)
        {
            return FormatInspection(inspection);
        }

        if (value is ObjectInspectionMember inspectionMember)
        {
            return FormatInspectionMember(inspectionMember);
        }

        if (value is FileSystemEntry entry)
        {
            return FormatFileSystemEntry(entry, options);
        }

        if (value is FileSystemInfo fileSystemInfo)
        {
            return FormatFileSystemInfo(fileSystemInfo, options);
        }

        // A shell type descriptor names a type, so displaying one shows that
        // name — the same rule the CLR `Type` case below applies. This must sit
        // above the record-field check: a descriptor exposes Name, FullName,
        // Namespace and friends as ordinary readable properties, so it would
        // otherwise render as a record dump. Giving the descriptor a `ToString`
        // fixed the paths that stringify, but not the structural ones, which is
        // where interpolation and nested rendering go (TS-P1-23).
        // The cast picks IShellStaticType's ShellTypeName: IShellNamedType
        // inherits the name from both of its bases, so the reference is
        // otherwise ambiguous.
        if (value is IShellNamedType shellType)
        {
            return ((IShellStaticType)shellType).ShellTypeName;
        }

        // An un-awaited CLR task. Without this it renders as its runtime type —
        // `AsyncStateMachineBox`1` — because Task does not override ToString and the
        // compiler's state machine box is what a task actually is at run time. That
        // told a user nothing, and it is the visible half of what TS-P1-27 fixed;
        // a forgotten `await` has to be legible now that `await` is explicit.
        if (ClrAwaitable.IsAwaitable(value))
        {
            return ClrAwaitable.Describe(value);
        }

        if (ShellRecordUtilities.TryGetFields(value, out var recordFields))
        {
            return FormatRecordFields(recordFields, options, depth, visited);
        }

        if (value is Type type)
        {
            return type.FullName ?? type.Name;
        }

        if (value is Exception exception)
        {
            return $"{GetTypeName(exception.GetType())} {{ Message = {QuoteString(exception.Message)} }}";
        }

        var runtimeType = value.GetType();

        if (depth >= options.MaxDepth)
        {
            return $"{GetTypeName(runtimeType)} {{ ... }}";
        }

        var trackReferences = !runtimeType.IsValueType;

        if (trackReferences && !visited.Add(value))
        {
            return "<cycle>";
        }

        try
        {
            if (value is IDictionary dictionary)
            {
                return FormatDictionary(dictionary, options, depth, visited);
            }

            if (value is IEnumerable enumerable and not string)
            {
                return FormatEnumerable(enumerable, runtimeType, options, depth, visited, isRoot);
            }

            return FormatObject(value, runtimeType, options, depth, visited);
        }
        finally
        {
            if (trackReferences)
            {
                visited.Remove(value);
            }
        }
    }

    internal static bool TryFormatSimple(object value, bool isRoot, out string text)
    {
        if (value is string stringValue)
        {
            text = isRoot ? stringValue : QuoteString(stringValue);
            return true;
        }

        if (value is char character)
        {
            text = isRoot ? character.ToString() : QuoteString(character.ToString());
            return true;
        }

        if (value is bool boolean)
        {
            text = boolean.ToString().ToLowerInvariant();
            return true;
        }

        if (value is Enum)
        {
            text = value.ToString() ?? value.GetType().Name;
            return true;
        }

        if (value is DateTime dateTime)
        {
            text = dateTime.ToString("O", CultureInfo.InvariantCulture);
            return true;
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            text = dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
            return true;
        }

        if (value is Uri uri)
        {
            text = uri.ToString();
            return true;
        }

        // Some scalar shell values also expose structured members for explicit
        // introspection. Classify them here before IShellRecordObject is handled,
        // otherwise interpolation expands a Quantity/ToshVector into a record.
        if (value is IFormattable formattable &&
            (value.GetType().IsPrimitive || value is decimal || value is Guid || value is TimeSpan ||
             value is BigInteger || value is ToshVector || value is ToshMatrix || value is Units.Quantity))
        {
            text = formattable.ToString(null, CultureInfo.InvariantCulture) ?? value.ToString() ?? value.GetType().Name;
            return true;
        }

        text = string.Empty;
        return false;
    }

    internal bool TryRenderProfile(
        object value,
        ObjectFormattingOptions options,
        DisplaySurface surface,
        out string text)
    {
        var profile = _profiles.Resolve(value.GetType());

        if (profile is not null &&
            profile.TryRender(new DisplayValueContext(value, surface, options.Style, FormattingOptions: options), out text))
        {
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static string FormatShellCommandDescriptor(ShellCommandDescriptor descriptor)
    {
        return $"{descriptor.Name.PadRight(12)} {descriptor.Description} ({descriptor.Usage})";
    }

    private static string FormatFormatterStatus(FormatterStatus formatterStatus)
    {
        return $"view: {formatterStatus.Style.ToString().ToLowerInvariant()}";
    }

    private static string FormatHistoryEntry(CommandHistoryEntry historyEntry)
    {
        return $"{historyEntry.Id,4}  {historyEntry.Timestamp:yyyy-MM-dd HH:mm:ss}  {historyEntry.Text}";
    }

    private static string FormatInspectionMember(ObjectInspectionMember inspectionMember)
    {
        return $"{inspectionMember.MemberKind} {inspectionMember.Name} : {inspectionMember.TypeName} = {inspectionMember.Display}";
    }

    private static string FormatInspection(ObjectInspection inspection)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"inspect {inspection.Index}: {inspection.TypeName}");

        if (!string.IsNullOrWhiteSpace(inspection.AssemblyName))
        {
            builder.AppendLine($"  assembly: {inspection.AssemblyName}");
        }

        if (!string.IsNullOrWhiteSpace(inspection.BaseTypeName))
        {
            builder.AppendLine($"  base: {inspection.BaseTypeName}");
        }

        builder.AppendLine($"  display: {inspection.Display}");

        if (inspection.Interfaces.Count > 0)
        {
            builder.AppendLine($"  interfaces: {string.Join(", ", inspection.Interfaces)}");
        }

        if (inspection.IsEnumerable)
        {
            var countText = inspection.ItemCount?.ToString(CultureInfo.InvariantCulture) ?? "?";
            builder.AppendLine($"  enumerable: true (count = {countText})");

            if (inspection.ItemsPreview.Count > 0)
            {
                builder.AppendLine("  items:");

                foreach (var item in inspection.ItemsPreview)
                {
                    builder.AppendLine($"    - {item}");
                }

                if (inspection.HasMoreItems)
                {
                    builder.AppendLine("    - ...");
                }
            }
        }

        if (inspection.Members.Count > 0)
        {
            builder.AppendLine("  members:");

            foreach (var member in inspection.Members)
            {
                builder.AppendLine($"    - {FormatInspectionMember(member)}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private string FormatFileSystemEntry(FileSystemEntry entry, ObjectFormattingOptions options)
    {
        if (entry.PreferLongDisplay)
        {
            var size = entry.Length?.ToString() ?? "-";
            var timestamp = entry.Modified.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return $"{entry.GetModeDisplay(includeTypeIndicator: true)} {size,10} {timestamp} {entry.DisplayName}";
        }

        if (options.Style == ObjectRenderStyle.Detail)
        {
            return FormatObject(entry, typeof(FileSystemEntry), options, depth: 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        return entry.DisplayName;
    }

    private string FormatFileSystemInfo(FileSystemInfo fileSystemInfo, ObjectFormattingOptions options)
    {
        if (options.Style == ObjectRenderStyle.Detail)
        {
            var entry = FileSystemEntry.From(fileSystemInfo);
            return FormatObject(entry, typeof(FileSystemEntry), options, depth: 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        return fileSystemInfo.FullName;
    }

    private string FormatRecordFields(
        IReadOnlyList<KeyValuePair<string, object?>> fields,
        ObjectFormattingOptions options,
        int depth,
        HashSet<object> visited)
    {
        // Rendered in the record literal's own delimiters so displayed output
        // round-trips as source (TS-P2-25). A bare `{ ... }` opens a block now,
        // so the previous rendering produced text the parser rejects — visible
        // on every display of a record, in the REPL, diagnostics, and logs.
        if (depth >= options.MaxDepth)
        {
            return "{| ... |}";
        }

        var parts = fields
            .Take(options.MaxPropertyCount)
            .Select(field => $"{field.Key} = {FormatValue(field.Value, options, depth + 1, visited, isRoot: false)}")
            .ToList();

        if (fields.Count > options.MaxPropertyCount)
        {
            parts.Add("...");
        }

        return parts.Count == 0
            ? "{||}"
            : $"{{| {string.Join(", ", parts)} |}}";
    }

    private string FormatDictionary(
        IDictionary dictionary,
        ObjectFormattingOptions options,
        int depth,
        HashSet<object> visited)
    {
        var items = new List<string>();
        var hiddenCount = 0;
        var limit = options.MaxCollectionItemCount;

        foreach (DictionaryEntry entry in dictionary)
        {
            if (items.Count >= limit)
            {
                hiddenCount++;
                continue;
            }

            items.Add($"{FormatValue(entry.Key, options, depth + 1, visited, isRoot: false)} = {FormatValue(entry.Value, options, depth + 1, visited, isRoot: false)}");
        }

        if (hiddenCount > 0)
        {
            items.Add($"... +{hiddenCount} more");
        }

        return FormatContainer(GetTypeName(dictionary.GetType()), "{", "}", items, options);
    }

    private string FormatEnumerable(
        IEnumerable enumerable,
        Type runtimeType,
        ObjectFormattingOptions options,
        int depth,
        HashSet<object> visited,
        bool isRoot)
    {
        var items = new List<string>();
        var hiddenCount = 0;
        var limit = options.MaxCollectionItemCount;

        foreach (var item in enumerable)
        {
            if (items.Count >= limit)
            {
                hiddenCount++;
                continue;
            }

            items.Add(FormatValue(item, options, depth + 1, visited, isRoot: false));
        }

        if (hiddenCount > 0)
        {
            items.Add($"... +{hiddenCount} more");
        }

        // The element type is informative when a collection is the whole result
        // and noise when it is one field among many — and the header is not source,
        // so a nested collection rendered with it cannot be pasted back
        // (TS-P3-10). Strings already switch between unquoted and quoted on the
        // same root/nested distinction.
        var typeName = isRoot ? GetTypeName(runtimeType) : null;
        return FormatContainer(typeName, "[", "]", items, options);
    }

    private string FormatObject(
        object value,
        Type runtimeType,
        ObjectFormattingOptions options,
        int depth,
        HashSet<object> visited)
    {
        var properties = runtimeType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Take(options.MaxPropertyCount + 1)
            .ToArray();

        if (properties.Length == 0)
        {
            var text = value.ToString();
            return string.Equals(text, runtimeType.FullName, StringComparison.Ordinal) ? runtimeType.Name : text ?? runtimeType.Name;
        }

        var lines = new List<string>();
        var displayedCount = 0;

        foreach (var property in properties)
        {
            if (displayedCount >= options.MaxPropertyCount)
            {
                lines.Add("... = <more>");
                break;
            }

            var propertyValue = ObjectMemberAdapter.TryGetMember(runtimeType, property.Name, out _)
                ? ObjectMemberAdapter.SafeGetValue(value, property.Name)
                : SafeGetValue(property, value);
            lines.Add($"{property.Name} = {FormatValue(propertyValue, options, depth + 1, visited, isRoot: false)}");
            displayedCount++;
        }

        return FormatContainer(GetTypeName(runtimeType), "{", "}", lines, options);
    }

    internal static object? SafeGetValue(PropertyInfo property, object target)
    {
        try
        {
            return property.GetValue(target);
        }
        catch (Exception exception)
        {
            return $"<unavailable: {exception.GetType().Name}>";
        }
    }

    internal static string QuoteString(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        return $"\"{escaped}\"";
    }

    internal static string GetTypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var typeName = type.Name;
        var tickIndex = typeName.IndexOf('`');

        if (tickIndex >= 0)
        {
            typeName = typeName[..tickIndex];
        }

        var arguments = string.Join(", ", type.GetGenericArguments().Select(GetTypeName));
        return $"{typeName}<{arguments}>";
    }

    /// <param name="typeName">
    /// Prefix for the container, or <see langword="null"/> to render without one.
    /// </param>
    /// <remarks>
    /// Indents by exactly one level and takes no <c>depth</c>, because a
    /// container re-indents every line of every item it holds — so a nested
    /// container that also indented by its own depth was counted twice, and its
    /// items drifted a level further right at each level while its closing
    /// bracket drifted with them. Visible before this only under the CLR type
    /// header; source-like nested rendering (TS-P3-10) put it in plain sight.
    /// </remarks>
    private static string FormatContainer(
        string? typeName,
        string opening,
        string closing,
        IReadOnlyList<string> items,
        ObjectFormattingOptions options)
    {
        var prefix = typeName is null ? string.Empty : typeName + " ";

        if (items.Count == 0)
        {
            return $"{prefix}{opening}{closing}";
        }

        if (options.Style == ObjectRenderStyle.Compact)
        {
            return $"{prefix}{opening} {string.Join(", ", items)} {closing}";
        }

        var indent = new string(' ', options.IndentSize);
        var builder = new StringBuilder();
        builder.Append(prefix);
        builder.AppendLine(opening);

        foreach (var item in items)
        {
            var itemLines = item.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

            foreach (var itemLine in itemLines)
            {
                builder.Append(indent);
                builder.AppendLine(itemLine);
            }
        }

        builder.Append(closing);
        return builder.ToString();
    }
}
