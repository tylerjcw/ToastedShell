using System.Collections;
using System.Globalization;

namespace Tosh.Runtime;

public sealed record ConfigCollectionEditorItem(
    string Key,
    string Label,
    string Summary,
    string EditValue);

public static class ConfigCollectionEditorRegistry
{
    private static readonly IReadOnlyList<IConfigCollectionEditorHandler> Handlers =
    [
        new DisplayProfileCollectionEditorHandler(),
        new SimpleScalarCollectionEditorHandler(),
    ];

    public static bool SupportsEditing(string path, Type valueType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(valueType);

        return TryGetHandler(path, valueType, out _);
    }

    public static IReadOnlyList<ConfigCollectionEditorItem> GetItems(
        ToshRuntime runtime,
        ConfigBrowserNode node,
        object? value)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(node);

        if (TryGetHandler(node.Path, node.ValueType, out var handler) && handler is not null)
        {
            return handler.GetItems(runtime, node, value);
        }

        return Array.Empty<ConfigCollectionEditorItem>();
    }

    public static bool TryAddItem(
        ToshRuntime runtime,
        ConfigBrowserNode node,
        object? currentValue,
        string input,
        out object updatedValue,
        out string status,
        out string? selectedKey)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(node);

        if (TryGetHandler(node.Path, node.ValueType, out var handler) && handler is not null)
        {
            return handler.TryAddItem(runtime, node, currentValue, input, out updatedValue, out status, out selectedKey);
        }

        updatedValue = currentValue ?? Array.Empty<object?>();
        status = "This collection does not support adding items yet.";
        selectedKey = null;
        return false;
    }

    public static bool TryUpdateItem(
        ToshRuntime runtime,
        ConfigBrowserNode node,
        object? currentValue,
        string key,
        string input,
        out object updatedValue,
        out string status)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (TryGetHandler(node.Path, node.ValueType, out var handler) && handler is not null)
        {
            return handler.TryUpdateItem(runtime, node, currentValue, key, input, out updatedValue, out status);
        }

        updatedValue = currentValue ?? Array.Empty<object?>();
        status = "This collection does not support item edits yet.";
        return false;
    }

    public static bool TryRemoveItem(
        ToshRuntime runtime,
        ConfigBrowserNode node,
        object? currentValue,
        string key,
        out object updatedValue,
        out string status)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (TryGetHandler(node.Path, node.ValueType, out var handler) && handler is not null)
        {
            return handler.TryRemoveItem(runtime, node, currentValue, key, out updatedValue, out status);
        }

        updatedValue = currentValue ?? Array.Empty<object?>();
        status = "This collection does not support removing items yet.";
        return false;
    }

    public static bool TryApplyValue(
        ToshRuntime runtime,
        ConfigBrowserNode node,
        object? value,
        out string? errorMessage)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(node);

        if (TryGetHandler(node.Path, node.ValueType, out var handler) && handler is not null)
        {
            return handler.TryApplyValue(runtime, node, value, out errorMessage);
        }

        errorMessage = "This collection does not support apply yet.";
        return false;
    }

    public static IReadOnlyList<string> BuildManagedConfigLines(
        ToshRuntime runtime,
        ConfigBrowserNode node,
        object? value,
        Func<string, string> quoteString)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(quoteString);

        if (TryGetHandler(node.Path, node.ValueType, out var handler) && handler is not null)
        {
            return handler.BuildManagedConfigLines(runtime, node, value, quoteString);
        }

        return Array.Empty<string>();
    }

    private static bool TryGetHandler(string path, Type valueType, out IConfigCollectionEditorHandler? handler)
    {
        var effectiveType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        handler = Handlers.FirstOrDefault(candidate => candidate.Supports(path, effectiveType));
        return handler is not null;
    }

    private interface IConfigCollectionEditorHandler
    {
        bool Supports(string path, Type valueType);

        IReadOnlyList<ConfigCollectionEditorItem> GetItems(ToshRuntime runtime, ConfigBrowserNode node, object? value);

        bool TryAddItem(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? currentValue,
            string input,
            out object updatedValue,
            out string status,
            out string? selectedKey);

        bool TryUpdateItem(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? currentValue,
            string key,
            string input,
            out object updatedValue,
            out string status);

        bool TryRemoveItem(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? currentValue,
            string key,
            out object updatedValue,
            out string status);

        bool TryApplyValue(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? value,
            out string? errorMessage);

        IReadOnlyList<string> BuildManagedConfigLines(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? value,
            Func<string, string> quoteString);
    }

    private sealed class DisplayProfileCollectionEditorHandler : IConfigCollectionEditorHandler
    {
        public bool Supports(string path, Type valueType)
        {
            return string.Equals(path, "Display.Profiles.Types", StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<ConfigCollectionEditorItem> GetItems(ToshRuntime runtime, ConfigBrowserNode node, object? value)
        {
            return GetDisplayProfiles(value)
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(profile => new ConfigCollectionEditorItem(
                    Key: profile.Name,
                    Label: profile.Name,
                    Summary: profile.TableColumns.Count == 0
                        ? "<default order>"
                        : string.Join(", ", profile.TableColumns),
                    EditValue: string.Join(", ", profile.TableColumns)))
                .ToArray();
        }

        public bool TryAddItem(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? currentValue,
            string input,
            out object updatedValue,
            out string status,
            out string? selectedKey)
        {
            var (rawTypeName, rawColumns) = ParseProfileInput(input);

            if (string.IsNullOrWhiteSpace(rawTypeName))
            {
                updatedValue = GetDisplayProfiles(currentValue);
                status = "Enter a type name followed by columns, for example: System.String = Length, Chars";
                selectedKey = null;
                return false;
            }

            var normalizedTypeName = NormalizeTypeName(runtime, rawTypeName);
            var columns = SplitColumns(rawColumns).ToArray();

            if (columns.Length == 0)
            {
                updatedValue = GetDisplayProfiles(currentValue);
                status = "New display profile rows need at least one column name.";
                selectedKey = null;
                return false;
            }

            var profiles = GetDisplayProfiles(currentValue).ToList();
            var existingIndex = profiles.FindIndex(profile => string.Equals(profile.Name, normalizedTypeName, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
            {
                updatedValue = profiles;
                status = $"{normalizedTypeName} already exists. Edit it instead of adding a duplicate.";
                selectedKey = normalizedTypeName;
                return false;
            }

            profiles.Add(new ToshDisplayTypeProfileConfig(normalizedTypeName, columns));
            updatedValue = profiles
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            status = $"Staged display profile override for {normalizedTypeName}.";
            selectedKey = normalizedTypeName;
            return true;
        }

        public bool TryUpdateItem(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? currentValue,
            string key,
            string input,
            out object updatedValue,
            out string status)
        {
            var profiles = GetDisplayProfiles(currentValue).ToList();
            var index = profiles.FindIndex(profile => string.Equals(profile.Name, key, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                updatedValue = profiles;
                status = $"Could not find collection item {key}.";
                return false;
            }

            var columns = SplitColumns(input).ToArray();

            if (columns.Length == 0)
            {
                updatedValue = profiles;
                status = "Display profile overrides need at least one column name.";
                return false;
            }

            var normalizedTypeName = NormalizeTypeName(runtime, profiles[index].Name);
            profiles[index] = new ToshDisplayTypeProfileConfig(normalizedTypeName, columns);
            updatedValue = profiles
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            status = $"Staged columns for {normalizedTypeName}.";
            return true;
        }

        public bool TryRemoveItem(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? currentValue,
            string key,
            out object updatedValue,
            out string status)
        {
            var profiles = GetDisplayProfiles(currentValue).ToList();
            var removed = profiles.RemoveAll(profile => string.Equals(profile.Name, key, StringComparison.OrdinalIgnoreCase));
            updatedValue = profiles
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            status = removed == 0
                ? $"Could not find collection item {key}."
                : $"Removed display profile override for {key}.";
            return removed > 0;
        }

        public bool TryApplyValue(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? value,
            out string? errorMessage)
        {
            var preferences = runtime.DisplayPreferences.Profiles;
            preferences.Reset();

            foreach (var profile in GetDisplayProfiles(value))
            {
                preferences.GetOrCreate(profile.Name).SetTableColumns(profile.TableColumns);
            }

            errorMessage = null;
            return true;
        }

        public IReadOnlyList<string> BuildManagedConfigLines(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? value,
            Func<string, string> quoteString)
        {
            return GetDisplayProfiles(value)
                .Where(profile => profile.TableColumns.Count > 0)
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(profile =>
                {
                    var arguments = new List<string>(profile.TableColumns.Count + 2)
                    {
                        "view columns",
                        quoteString(profile.Name),
                    };

                    arguments.AddRange(profile.TableColumns.Select(quoteString));
                    return string.Join(' ', arguments);
                })
                .ToArray();
        }

        private static IReadOnlyList<ToshDisplayTypeProfileConfig> GetDisplayProfiles(object? value)
        {
            return value switch
            {
                IReadOnlyList<ToshDisplayTypeProfileConfig> typed => typed.ToArray(),
                IEnumerable<ToshDisplayTypeProfileConfig> enumerable => enumerable.ToArray(),
                _ => Array.Empty<ToshDisplayTypeProfileConfig>(),
            };
        }

        private static (string TypeName, string Columns) ParseProfileInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return (string.Empty, string.Empty);
            }

            var separatorIndex = input.IndexOf('=');

            if (separatorIndex < 0)
            {
                separatorIndex = input.IndexOf(':');
            }

            if (separatorIndex < 0)
            {
                return (input.Trim(), string.Empty);
            }

            return (
                input[..separatorIndex].Trim(),
                input[(separatorIndex + 1)..].Trim());
        }

        private static string NormalizeTypeName(ToshRuntime runtime, string typeName)
        {
            var trimmed = typeName.Trim();

            if (trimmed.Length == 0)
            {
                return trimmed;
            }

            if (BuiltInShellTypes.TryResolveStaticType(trimmed, runtime.TypeResolver, out var shellType) &&
                shellType is IShellTypeDescriptor descriptor)
            {
                return descriptor.ShellFullName;
            }

            var clrType = runtime.TypeResolver.Resolve(trimmed);
            return clrType is null ? trimmed : ReflectionMetadataUtilities.GetDisplayName(clrType);
        }

        private static IEnumerable<string> SplitColumns(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                yield break;
            }

            foreach (var part in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(part))
                {
                    yield return part;
                }
            }
        }
    }

    private sealed class SimpleScalarCollectionEditorHandler : IConfigCollectionEditorHandler
    {
        public bool Supports(string path, Type valueType)
        {
            return TryGetOrderedCollectionElementType(valueType, out _);
        }

        public IReadOnlyList<ConfigCollectionEditorItem> GetItems(ToshRuntime runtime, ConfigBrowserNode node, object? value)
        {
            return GetScalarItems(value)
                .Select((item, index) => new ConfigCollectionEditorItem(
                    Key: index.ToString(CultureInfo.InvariantCulture),
                    Label: $"[{index}]",
                    Summary: FormatScalarPreview(item),
                    EditValue: FormatScalarEditValue(item)))
                .ToArray();
        }

        public bool TryAddItem(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? currentValue,
            string input,
            out object updatedValue,
            out string status,
            out string? selectedKey)
        {
            if (!TryParseScalarInput(node.ValueType, input, out var parsedValue, out status))
            {
                updatedValue = currentValue ?? Array.Empty<object?>();
                selectedKey = null;
                return false;
            }

            var items = GetScalarItems(currentValue).ToList();
            items.Add(parsedValue);
            updatedValue = BuildScalarCollectionValue(node.ValueType, items);
            selectedKey = (items.Count - 1).ToString(CultureInfo.InvariantCulture);
            status = $"Staged collection item {selectedKey}.";
            return true;
        }

        public bool TryUpdateItem(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? currentValue,
            string key,
            string input,
            out object updatedValue,
            out string status)
        {
            var items = GetScalarItems(currentValue).ToList();

            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                index < 0 ||
                index >= items.Count)
            {
                updatedValue = currentValue ?? Array.Empty<object?>();
                status = $"Could not find collection item {key}.";
                return false;
            }

            if (!TryParseScalarInput(node.ValueType, input, out var parsedValue, out status))
            {
                updatedValue = currentValue ?? Array.Empty<object?>();
                return false;
            }

            items[index] = parsedValue;
            updatedValue = BuildScalarCollectionValue(node.ValueType, items);
            status = $"Staged collection item [{index}].";
            return true;
        }

        public bool TryRemoveItem(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? currentValue,
            string key,
            out object updatedValue,
            out string status)
        {
            var items = GetScalarItems(currentValue).ToList();

            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                index < 0 ||
                index >= items.Count)
            {
                updatedValue = currentValue ?? Array.Empty<object?>();
                status = $"Could not find collection item {key}.";
                return false;
            }

            items.RemoveAt(index);
            updatedValue = BuildScalarCollectionValue(node.ValueType, items);
            status = $"Removed collection item [{index}].";
            return true;
        }

        public bool TryApplyValue(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? value,
            out string? errorMessage)
        {
            try
            {
                runtime.ObjectAccessor.SetValue(runtime.Config, node.Path, value);
                errorMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public IReadOnlyList<string> BuildManagedConfigLines(
            ToshRuntime runtime,
            ConfigBrowserNode node,
            object? value,
            Func<string, string> quoteString)
        {
            if (!TryGetOrderedCollectionElementType(node.ValueType, out var elementType))
            {
                return Array.Empty<string>();
            }

            return
            [
                $"$tosh.Config.{node.Path} = {FormatScalarCollectionLiteral(value, elementType, quoteString)}"
            ];
        }

        private static IReadOnlyList<object?> GetScalarItems(object? value)
        {
            return value is IEnumerable enumerable and not string
                ? enumerable.Cast<object?>().ToArray()
                : Array.Empty<object?>();
        }

        private static string FormatScalarPreview(object? value)
        {
            return value switch
            {
                null => "null",
                string text => text,
                bool boolean => boolean ? "true" : "false",
                Enum @enum => @enum.ToString() ?? string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? value.ToString() ?? string.Empty,
                _ => value.ToString() ?? string.Empty,
            };
        }

        private static string FormatScalarEditValue(object? value)
        {
            return FormatScalarPreview(value);
        }

        private static string FormatScalarCollectionLiteral(object? value, Type elementType, Func<string, string> quoteString)
        {
            var items = GetScalarItems(value);
            var formatted = items.Select(item => FormatScalarLiteral(item, elementType, quoteString));
            return $"[{string.Join(", ", formatted)}]";
        }

        private static string FormatScalarLiteral(object? value, Type elementType, Func<string, string> quoteString)
        {
            return value switch
            {
                null => "null",
                string text => quoteString(text),
                bool boolean => boolean ? "true" : "false",
                Enum @enum => quoteString(@enum.ToString() ?? string.Empty),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? quoteString(value.ToString() ?? string.Empty),
                _ => quoteString(value.ToString() ?? string.Empty),
            };
        }

        private static bool TryParseScalarInput(Type collectionType, string input, out object? parsedValue, out string status)
        {
            if (!TryGetOrderedCollectionElementType(collectionType, out var elementType))
            {
                parsedValue = null;
                status = "This collection type is not supported by the simple scalar editor.";
                return false;
            }

            var trimmed = input.Trim();

            if (elementType == typeof(string))
            {
                parsedValue = trimmed;
                status = string.Empty;
                return true;
            }

            if (trimmed.Length == 0)
            {
                parsedValue = null;
                status = "Collection items cannot be empty for this element type.";
                return false;
            }

            try
            {
                if (elementType == typeof(bool))
                {
                    if (!bool.TryParse(trimmed, out var boolean))
                    {
                        parsedValue = null;
                        status = "Enter true or false.";
                        return false;
                    }

                    parsedValue = boolean;
                    status = string.Empty;
                    return true;
                }

                if (elementType.IsEnum)
                {
                    parsedValue = Enum.Parse(elementType, trimmed, ignoreCase: true);
                    status = string.Empty;
                    return true;
                }

                if (IsNumericType(elementType))
                {
                    parsedValue = Convert.ChangeType(trimmed, elementType, CultureInfo.InvariantCulture);
                    status = string.Empty;
                    return true;
                }
            }
            catch (Exception ex)
            {
                parsedValue = null;
                status = ex.Message;
                return false;
            }

            parsedValue = null;
            status = $"This collection element type ({elementType.Name}) is not supported yet.";
            return false;
        }

        private static object BuildScalarCollectionValue(Type collectionType, IReadOnlyList<object?> items)
        {
            if (!TryGetOrderedCollectionElementType(collectionType, out var elementType))
            {
                return items.ToArray();
            }

            var normalizedItems = items
                .Select(item =>
                {
                    return TypeConversion.TryConvert(item, elementType, out var converted)
                        ? converted
                        : item;
                })
                .ToArray();

            if (collectionType.IsArray)
            {
                var array = Array.CreateInstance(elementType, normalizedItems.Length);

                for (var index = 0; index < normalizedItems.Length; index++)
                {
                    array.SetValue(normalizedItems[index], index);
                }

                return array;
            }

            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (IList)Activator.CreateInstance(listType)!;

            foreach (var item in normalizedItems)
            {
                list.Add(item);
            }

            return list;
        }
    }

    private static bool TryGetOrderedCollectionElementType(Type valueType, out Type elementType)
    {
        if (valueType.IsArray)
        {
            elementType = valueType.GetElementType() ?? typeof(object);
            return IsSupportedScalarElementType(elementType);
        }

        foreach (var candidate in valueType.GetInterfaces().Append(valueType))
        {
            if (!candidate.IsGenericType)
            {
                continue;
            }

            var genericDefinition = candidate.GetGenericTypeDefinition();

            if (genericDefinition != typeof(IReadOnlyList<>) &&
                genericDefinition != typeof(IList<>) &&
                genericDefinition != typeof(List<>))
            {
                continue;
            }

            elementType = candidate.GetGenericArguments()[0];
            return IsSupportedScalarElementType(elementType);
        }

        elementType = typeof(object);
        return false;
    }

    private static bool IsSupportedScalarElementType(Type elementType)
    {
        var effectiveType = Nullable.GetUnderlyingType(elementType) ?? elementType;
        return effectiveType == typeof(string) ||
               effectiveType == typeof(bool) ||
               effectiveType.IsEnum ||
               IsNumericType(effectiveType);
    }

    private static bool IsNumericType(Type type)
    {
        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong) ||
               type == typeof(float) ||
               type == typeof(double) ||
               type == typeof(decimal);
    }
}
