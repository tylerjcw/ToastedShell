using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using Tosh.Core;

namespace Tosh.Cli;

internal sealed class ReplCompletionEngine
{
    private static readonly IReadOnlySet<string> PathFirstCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cd",
        "ls",
        "cat",
        "mkdir",
        "touch",
        "rm",
        "cp",
        "mv",
        "find",
        "findmnt",
        "du",
        "df",
        "lsblk",
        "head",
        "tail",
        "grep",
        "open",
        "hash",
        "dirname",
        "basename",
        "readlink",
        "realpath",
        "archive",
        "extract",
        "tree",
        "trash",
        "source",
    };

    private static readonly IReadOnlyDictionary<string, string> SpecialVariables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["$tosh"] = "The live ToSh runtime namespace.",
        ["$env"] = "Read environment-variable values directly by name.",
        ["_"] = "The current pipeline item in predicates and block-driven pipeline commands.",
    };

    private static readonly IReadOnlyDictionary<string, string> Keywords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["func"] = "Keyword",
        ["var"] = "Keyword",
        ["class"] = "Keyword",
        ["module"] = "Keyword",
        ["enum"] = "Keyword",
        ["record"] = "Keyword",
        ["prop"] = "Keyword",
        ["shy"] = "Keyword",
        ["static"] = "Keyword",
        ["global"] = "Keyword",
        ["export"] = "Keyword",
        ["using"] = "Keyword",
        ["require"] = "Keyword",
        ["from"] = "Keyword",
        ["as"] = "Keyword",
        ["if"] = "Keyword",
        ["else"] = "Keyword",
        ["for"] = "Keyword",
        ["in"] = "Keyword",
        ["while"] = "Keyword",
        ["until"] = "Keyword",
        ["return"] = "Keyword",
        ["throw"] = "Keyword",
        ["try"] = "Keyword",
        ["catch"] = "Keyword",
        ["finally"] = "Keyword",
        ["switch"] = "Keyword",
        ["match"] = "Keyword",
        ["case"] = "Keyword",
        ["default"] = "Keyword",
        ["break"] = "Keyword",
        ["continue"] = "Keyword",
        ["and"] = "Operator",
        ["or"] = "Operator",
        ["not"] = "Operator",
        ["is"] = "Operator",
        ["is-not"] = "Operator",
        ["not-in"] = "Operator",
        ["new"] = "Language form",
        ["nameof"] = "Language form",
        ["name-of"] = "Language form",
    };

    private readonly ToshRuntime _runtime;

    public ReplCompletionEngine(ToshRuntime runtime)
    {
        _runtime = runtime;
    }

    public ReplCompletionResult? GetCompletions(string text, int cursorIndex)
    {
        ArgumentNullException.ThrowIfNull(text);

        cursorIndex = Math.Clamp(cursorIndex, 0, text.Length);
        var tokenStart = FindCompletionTokenStart(text, cursorIndex);
        var tokenText = text[tokenStart..cursorIndex];
        var tokenPrefix = tokenText.TrimStart();
        var replacementStart = cursorIndex - tokenPrefix.Length;
        var replacementLength = cursorIndex - replacementStart;

        if (TryGetGenericTypeArgumentContext(tokenText, tokenStart, cursorIndex, out var genericContext))
        {
            var genericSuggestions = GetTypeSuggestions(genericContext.Partial);
            return genericSuggestions.Count == 0
                ? null
                : new ReplCompletionResult(genericContext.ReplacementStart, genericContext.ReplacementLength, genericSuggestions);
        }

        if (TryGetVariableOrMemberSuggestions(tokenPrefix, replacementStart, replacementLength, out var variableResult))
        {
            return variableResult;
        }

        if (TryGetPathSuggestions(text, tokenPrefix, replacementStart, replacementLength, out var pathResult))
        {
            return pathResult;
        }

        if (TryGetQualifiedSuggestions(tokenPrefix, replacementStart, replacementLength, out var qualifiedResult))
        {
            return qualifiedResult;
        }

        var linePrefix = text[..cursorIndex];
        var trimmedLinePrefix = linePrefix.TrimStart();
        var typeOnlyContext = trimmedLinePrefix.StartsWith("using ", StringComparison.Ordinal) ||
                              trimmedLinePrefix.StartsWith("new ", StringComparison.Ordinal) ||
                              trimmedLinePrefix.Contains(" cast ", StringComparison.Ordinal);
        var suggestions = typeOnlyContext
            ? GetTypeSuggestions(tokenPrefix)
            : GetRootSuggestions(
                tokenPrefix,
                includeKeywords: true,
                includeVariables: false,
                includeClrRoot: true,
                includeExternalCommands: IsCommandPosition(text, replacementStart));

        return suggestions.Count == 0
            ? null
            : new ReplCompletionResult(replacementStart, replacementLength, suggestions);
    }

    internal static string GetTokenAtCursor(string text, int cursorIndex)
    {
        ArgumentNullException.ThrowIfNull(text);

        cursorIndex = Math.Clamp(cursorIndex, 0, text.Length);
        var tokenStart = FindCompletionTokenStart(text, cursorIndex);
        var tokenEnd = FindCompletionTokenEnd(text, cursorIndex);

        return tokenEnd <= tokenStart
            ? string.Empty
            : text[tokenStart..tokenEnd];
    }

    internal static (int Start, int Length, string Token) GetTokenSpanAtCursor(string text, int cursorIndex)
    {
        ArgumentNullException.ThrowIfNull(text);

        cursorIndex = Math.Clamp(cursorIndex, 0, text.Length);
        var tokenStart = FindCompletionTokenStart(text, cursorIndex);
        var tokenEnd = FindCompletionTokenEnd(text, cursorIndex);

        return tokenEnd <= tokenStart
            ? (cursorIndex, 0, string.Empty)
            : (tokenStart, tokenEnd - tokenStart, text[tokenStart..tokenEnd]);
    }

    internal static (int Start, int Length, string Token) GetInspectTargetSpanAtCursor(string text, int cursorIndex)
    {
        ArgumentNullException.ThrowIfNull(text);

        cursorIndex = Math.Clamp(cursorIndex, 0, text.Length);

        return TryGetQuotedSpanAtCursor(text, cursorIndex, out var quotedSpan)
            ? quotedSpan
            : GetTokenSpanAtCursor(text, cursorIndex);
    }

    internal static string? GetInlineHelpQuery(string text, int cursorIndex)
    {
        var token = GetTokenSpanAtCursor(text, cursorIndex).Token.Trim();

        if (token.Length == 0)
        {
            return null;
        }

        if (token.StartsWith('$'))
        {
            token = token[1..];
        }

        if (token.StartsWith("env.", StringComparison.OrdinalIgnoreCase))
        {
            token = token[4..];
        }
        else
        {
            var dotIndex = token.LastIndexOf('.');

            if (dotIndex >= 0 && dotIndex + 1 < token.Length)
            {
                token = token[(dotIndex + 1)..];
            }
        }

        return token.Length == 0 ? null : token;
    }

    internal bool TryResolveInspectableReference(string token, out object? value)
    {
        ArgumentNullException.ThrowIfNull(token);

        var trimmed = token.Trim();

        if (trimmed.Length == 0)
        {
            value = null;
            return false;
        }

        if (TryParseInspectableLiteral(trimmed, out value))
        {
            return true;
        }

        value = trimmed.StartsWith("$", StringComparison.Ordinal)
            ? ResolveValueReference(trimmed)
            : ResolveBareReference(trimmed);

        if (value is not null)
        {
            return true;
        }

        if (!trimmed.StartsWith("$", StringComparison.Ordinal))
        {
            value = trimmed;
            return true;
        }

        return false;
    }

    internal static string? BuildInspectableSourceExpression(string token, object? value)
    {
        ArgumentNullException.ThrowIfNull(token);

        var trimmed = token.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        if (IsQuotedInspectableLiteral(trimmed) ||
            trimmed.StartsWith("$", StringComparison.Ordinal) ||
            string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) ||
            bool.TryParse(trimmed, out _) ||
            BigInteger.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
            decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            value is Type)
        {
            return trimmed;
        }

        return value is string stringValue
            ? QuoteInspectableString(stringValue)
            : trimmed;
    }

    private static bool TryParseInspectableLiteral(string text, out object? value)
    {
        if (TryParseQuotedInspectableLiteral(text, out value))
        {
            return true;
        }

        if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
        {
            value = null;
            return true;
        }

        if (bool.TryParse(text, out var boolean))
        {
            value = boolean;
            return true;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            value = intValue;
            return true;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            value = longValue;
            return true;
        }

        if (BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bigInteger))
        {
            value = bigInteger;
            return true;
        }

        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
        {
            value = decimalValue;
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            value = doubleValue;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryParseQuotedInspectableLiteral(string text, out object? value)
    {
        if (!IsQuotedInspectableLiteral(text))
        {
            value = null;
            return false;
        }

        value = UnescapeInspectableString(text[1..^1]);
        return true;
    }

    private static bool IsQuotedInspectableLiteral(string text)
    {
        return text.Length >= 2 &&
               ((text[0] == '"' && text[^1] == '"') ||
                (text[0] == '\'' && text[^1] == '\''));
    }

    private static string QuoteInspectableString(string value)
    {
        return "\"" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
    }

    private static string UnescapeInspectableString(string value)
    {
        var builder = new StringBuilder(value.Length);

        for (var index = 0; index < value.Length; index += 1)
        {
            var character = value[index];

            if (character == '\\' && index + 1 < value.Length)
            {
                index += 1;
                builder.Append(value[index] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    '\'' => '\'',
                    _ => value[index],
                });
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private bool TryGetVariableOrMemberSuggestions(string tokenPrefix, int replacementStart, int replacementLength, out ReplCompletionResult? result)
    {
        if (tokenPrefix.Length == 0)
        {
            result = null;
            return false;
        }

        if (tokenPrefix.StartsWith("$", StringComparison.Ordinal))
        {
            var dotIndex = tokenPrefix.LastIndexOf('.');

            if (dotIndex < 0)
            {
                var variablePartial = tokenPrefix;
                var variableSuggestions = GetVariableSuggestions(variablePartial);
                result = variableSuggestions.Count == 0
                    ? null
                    : new ReplCompletionResult(replacementStart, replacementLength, variableSuggestions);
                return true;
            }

            var reference = tokenPrefix[..dotIndex];
            var partial = tokenPrefix[(dotIndex + 1)..];
            var target = ResolveValueReference(reference);

            if (target is null)
            {
                result = null;
                return true;
            }

            var suggestions = GetMemberSuggestions(target, partial, staticOnly: false, includeHidden: reference.StartsWith("$this", StringComparison.Ordinal));
            result = suggestions.Count == 0
                ? null
                : new ReplCompletionResult(replacementStart + dotIndex + 1, partial.Length, suggestions);
            return true;
        }

        result = null;
        return false;
    }

    private bool TryGetQualifiedSuggestions(string tokenPrefix, int replacementStart, int replacementLength, out ReplCompletionResult? result)
    {
        var dotIndex = tokenPrefix.LastIndexOf('.');

        if (dotIndex < 0)
        {
            result = null;
            return false;
        }

        var reference = tokenPrefix[..dotIndex];
        var partial = tokenPrefix[(dotIndex + 1)..];

        if (reference.Length == 0)
        {
            result = null;
            return false;
        }

        var target = ResolveBareReference(reference);

        if (target is not null)
        {
            var suggestions = GetMemberSuggestions(target, partial, staticOnly: true);
            result = suggestions.Count == 0
                ? null
                : new ReplCompletionResult(replacementStart + dotIndex + 1, partial.Length, suggestions);
            return true;
        }

        var expandedPath = ExpandAliasPath(reference);
        var namespaceSuggestions = ReplClrCompletionCatalog.Shared.GetNamespaceAndTypeSuggestions(expandedPath, partial);

        result = namespaceSuggestions.Count == 0
            ? null
            : new ReplCompletionResult(replacementStart + dotIndex + 1, partial.Length, namespaceSuggestions);
        return true;
    }

    private IReadOnlyList<ReplCompletionSuggestion> GetVariableSuggestions(string partial)
    {
        var suggestions = new Dictionary<string, ReplCompletionSuggestion>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in _runtime.Variables.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            var label = "$" + name;

            if (!MatchesPrefix(label, partial))
            {
                continue;
            }

            suggestions[label] = new ReplCompletionSuggestion(label, "Variable", Priority: 10);
        }

        foreach (var (name, description) in SpecialVariables)
        {
            if (!name.StartsWith('$') || !MatchesPrefix(name, partial))
            {
                continue;
            }

            suggestions[name] = new ReplCompletionSuggestion(name, description, Priority: 5);
        }

        return OrderSuggestions(suggestions.Values);
    }

    private IReadOnlyList<ReplCompletionSuggestion> GetRootSuggestions(
        string partial,
        bool includeKeywords,
        bool includeVariables,
        bool includeClrRoot = true,
        bool includeExternalCommands = false)
    {
        var suggestions = new Dictionary<string, ReplCompletionSuggestion>(StringComparer.OrdinalIgnoreCase);

        foreach (var command in _runtime.Commands.All)
        {
            if (!MatchesPrefix(command.Name, partial))
            {
                continue;
            }

            suggestions[command.Name] = new ReplCompletionSuggestion(command.Name, "Command", Priority: 10);
        }

        if (includeExternalCommands)
        {
            foreach (var name in ExternalCommandResolver.FindExecutableNamesByPrefix(_runtime.CurrentDirectory, partial))
            {
                suggestions[name] = new ReplCompletionSuggestion(name, "External command", Priority: 20);
            }
        }

        foreach (var name in _runtime.Classes.Keys)
        {
            if (!MatchesPrefix(name, partial))
            {
                continue;
            }

            suggestions[name] = new ReplCompletionSuggestion(name, "Type", Priority: 30);
        }

        foreach (var name in _runtime.Modules.Keys)
        {
            if (!MatchesPrefix(name, partial))
            {
                continue;
            }

            suggestions[name] = new ReplCompletionSuggestion(name, "Module", Priority: 25);
        }

        if (includeVariables)
        {
            foreach (var name in _runtime.Variables.Keys)
            {
                var label = "$" + name;
                if (!MatchesPrefix(label, partial))
                {
                    continue;
                }

                suggestions[label] = new ReplCompletionSuggestion(label, "Variable", Priority: 10);
            }
        }

        foreach (var (name, description) in SpecialVariables)
        {
            if (!MatchesPrefix(name, partial))
            {
                continue;
            }

            suggestions[name] = new ReplCompletionSuggestion(name, description, Priority: 15);
        }

        if (includeKeywords)
        {
            foreach (var (name, detail) in Keywords)
            {
                if (!MatchesPrefix(name, partial))
                {
                    continue;
                }

                suggestions[name] = new ReplCompletionSuggestion(name, detail, Priority: 40);
            }
        }

        if (includeClrRoot)
        {
            foreach (var (alias, _) in DotNetTypeResolver.BuiltInAliases)
            {
                if (!MatchesPrefix(alias, partial))
                {
                    continue;
                }

                suggestions[alias] = new ReplCompletionSuggestion(alias, "Type alias", Priority: 30);
            }

            if (_runtime.TypeResolver is DotNetTypeResolver resolver)
            {
                foreach (var (alias, target) in resolver.GetAliases())
                {
                    if (!MatchesPrefix(alias, partial))
                    {
                        continue;
                    }

                    suggestions[alias] = new ReplCompletionSuggestion(alias, target, Priority: 28);
                }

                foreach (var importPath in resolver.GetImports())
                {
                    foreach (var item in ReplClrCompletionCatalog.Shared.GetImportedTypeSuggestions(importPath, partial))
                    {
                        suggestions[item.Label] = item;
                    }
                }
            }

            foreach (var item in ReplClrCompletionCatalog.Shared.GetNamespaceAndTypeSuggestions(string.Empty, partial))
            {
                suggestions[item.Label] = item;
            }
        }

        return OrderSuggestions(suggestions.Values);
    }

    private IReadOnlyList<ReplCompletionSuggestion> GetTypeSuggestions(string partial)
    {
        var suggestions = new Dictionary<string, ReplCompletionSuggestion>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in _runtime.Classes.Keys)
        {
            if (!MatchesPrefix(name, partial))
            {
                continue;
            }

            suggestions[name] = new ReplCompletionSuggestion(name, "Type", Priority: 10);
        }

        foreach (var (alias, _) in DotNetTypeResolver.BuiltInAliases)
        {
            if (!MatchesPrefix(alias, partial))
            {
                continue;
            }

            suggestions[alias] = new ReplCompletionSuggestion(alias, "Type alias", Priority: 5);
        }

        if (_runtime.TypeResolver is DotNetTypeResolver resolver)
        {
            foreach (var (alias, target) in resolver.GetAliases())
            {
                if (!MatchesPrefix(alias, partial))
                {
                    continue;
                }

                suggestions[alias] = new ReplCompletionSuggestion(alias, target, Priority: 4);
            }

            foreach (var importPath in resolver.GetImports())
            {
                foreach (var item in ReplClrCompletionCatalog.Shared.GetImportedTypeSuggestions(importPath, partial))
                {
                    suggestions[item.Label] = item;
                }
            }
        }

        foreach (var item in ReplClrCompletionCatalog.Shared.GetNamespaceAndTypeSuggestions(string.Empty, partial))
        {
            suggestions[item.Label] = item;
        }

        return OrderSuggestions(suggestions.Values);
    }

    private IReadOnlyList<ReplCompletionSuggestion> GetMemberSuggestions(object target, string partial, bool staticOnly, bool includeHidden = false)
    {
        var suggestions = new Dictionary<string, ReplCompletionSuggestion>(StringComparer.OrdinalIgnoreCase);

        switch (target)
        {
            case IShellTypedObject typedObject:
                AddShellTypeSuggestions(suggestions, typedObject.ShellTypeDescriptor, partial, staticOnly: false, includeHidden);
                break;

            case IShellTypeDescriptor shellType:
                AddShellTypeSuggestions(suggestions, shellType, partial, staticOnly, includeHidden);
                break;

            case IShellRecordObject shellRecord:
                foreach (var member in shellRecord.GetMembers(includeHidden))
                {
                    if (!MatchesPrefix(member.Key, partial))
                    {
                        continue;
                    }

                    suggestions[member.Key] = new ReplCompletionSuggestion(member.Key, "Member", Priority: 10);
                }

                if (target is IShellInvocableObject)
                {
                    AddClrMemberSuggestions(suggestions, target.GetType(), partial, staticOnly: false);
                }
                break;

            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                AddDictionarySuggestions(suggestions, readOnlyDictionary.Keys, partial);
                break;

            case IDictionary<string, object?> dictionary:
                AddDictionarySuggestions(suggestions, dictionary.Keys, partial);
                break;

            case IDictionary nonGenericDictionary:
                AddDictionarySuggestions(
                    suggestions,
                    nonGenericDictionary.Keys.Cast<object?>().Select(key => key?.ToString() ?? string.Empty),
                    partial);
                break;

            case Type staticType:
                AddClrMemberSuggestions(suggestions, staticType, partial, staticOnly: true);
                break;

            default:
                if (BuiltInShellTypes.TryDescribeRuntimeValue(target, out var descriptor))
                {
                    AddShellTypeSuggestions(suggestions, descriptor, partial, staticOnly: false, includeHidden);
                }
                else
                {
                    AddClrMemberSuggestions(suggestions, target.GetType(), partial, staticOnly: false);
                }

                break;
        }

        return OrderSuggestions(suggestions.Values);
    }

    private static void AddDictionarySuggestions(IDictionary<string, ReplCompletionSuggestion> suggestions, IEnumerable<string> keys, string partial)
    {
        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!MatchesPrefix(key, partial))
            {
                continue;
            }

            suggestions[key] = new ReplCompletionSuggestion(key, "Field", Priority: 10);
        }
    }

    private static void AddShellTypeSuggestions(
        IDictionary<string, ReplCompletionSuggestion> suggestions,
        IShellTypeDescriptor shellType,
        string partial,
        bool staticOnly,
        bool includeHidden)
    {
        foreach (var member in shellType.GetShellMembers(includeHidden))
        {
            if (member.IsStatic != staticOnly || !MatchesPrefix(member.Name, partial))
            {
                continue;
            }

            suggestions[member.Name] = new ReplCompletionSuggestion(member.Name, member.Kind, Priority: member.IsStatic ? 25 : 10);
        }

        foreach (var method in shellType.GetShellMethods(includeHidden))
        {
            if (method.IsStatic != staticOnly || !MatchesPrefix(method.Name, partial))
            {
                continue;
            }

            suggestions[method.Name] = new ReplCompletionSuggestion(method.Name, method.Signature, Priority: method.IsStatic ? 25 : 12);
        }
    }

    private static void AddClrMemberSuggestions(
        IDictionary<string, ReplCompletionSuggestion> suggestions,
        Type targetType,
        string partial,
        bool staticOnly)
    {
        foreach (var item in ReplClrCompletionCatalog.Shared.GetMemberSuggestions(targetType, staticOnly, partial))
        {
            suggestions[item.Label] = item;
        }
    }

    private object? ResolveValueReference(string reference)
    {
        if (!reference.StartsWith("$", StringComparison.Ordinal))
        {
            return null;
        }

        var trimmed = reference[1..];
        var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return null;
        }

        object? current;
        if (string.Equals(segments[0], "env", StringComparison.Ordinal))
        {
            current = new ShellEnvironmentNamespace();
        }
        else if (!_runtime.Variables.TryGetValue(segments[0], out current))
        {
            return null;
        }

        foreach (var segment in segments.Skip(1))
        {
            if (!TryGetMemberValue(current, segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private object? ResolveBareReference(string reference)
    {
        var segments = reference.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return null;
        }

        if (!TryResolveBareRoot(segments[0], out var current))
        {
            return null;
        }

        foreach (var segment in segments.Skip(1))
        {
            if (!TryGetMemberValue(current, segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private bool TryResolveBareRoot(string root, out object? value)
    {
        if (_runtime.Modules.TryGetValue(root, out value))
        {
            return true;
        }

        if (_runtime.Classes.TryGetValue(root, out value))
        {
            return true;
        }

        if (_runtime.TypeResolver.Resolve(root) is Type clrType)
        {
            value = clrType;
            return true;
        }

        if (_runtime.TypeResolver is DotNetTypeResolver resolver)
        {
            foreach (var (alias, target) in resolver.GetAliases())
            {
                if (!string.Equals(alias, root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_runtime.TypeResolver.Resolve(target) is Type aliasType)
                {
                    value = aliasType;
                    return true;
                }

                value = target;
                return true;
            }
        }

        value = null;
        return false;
    }

    private bool TryGetMemberValue(object? target, string memberName, out object? value)
    {
        switch (target)
        {
            case null:
                value = null;
                return false;

            case IShellRecordObject shellRecord when shellRecord.TryGetMember(memberName, out value):
                return true;

            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                return TryGetDictionaryValue(readOnlyDictionary, memberName, out value);

            case IDictionary<string, object?> dictionary:
                return TryGetDictionaryValue(dictionary, memberName, out value);

            case IDictionary nonGenericDictionary:
                return TryGetDictionaryValue(nonGenericDictionary, memberName, out value);

            case Type staticType:
                return TryGetStaticMemberValue(staticType, memberName, out value);

            default:
                return TryGetInstanceMemberValue(target, memberName, out value);
        }
    }

    private static bool TryGetDictionaryValue(IEnumerable<KeyValuePair<string, object?>> dictionary, string memberName, out object? value)
    {
        foreach (var entry in dictionary)
        {
            if (string.Equals(entry.Key, memberName, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryGetDictionaryValue(IDictionary dictionary, string memberName, out object? value)
    {
        foreach (DictionaryEntry entry in dictionary)
        {
            if (string.Equals(entry.Key?.ToString(), memberName, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryGetStaticMemberValue(Type type, string memberName, out object? value)
    {
        var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);

        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            value = property.GetValue(null);
            return true;
        }

        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);

        if (field is not null)
        {
            value = field.GetValue(null);
            return true;
        }

        var nestedType = type.GetNestedTypes(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(candidate => string.Equals(GetTypeLabel(candidate), memberName, StringComparison.OrdinalIgnoreCase));

        if (nestedType is not null)
        {
            value = nestedType;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetInstanceMemberValue(object target, string memberName, out object? value)
    {
        if (target is ShellTextLine textLine)
        {
            if (string.Equals(memberName, nameof(ShellTextLine.Text), StringComparison.OrdinalIgnoreCase))
            {
                value = textLine.Text;
                return true;
            }

            target = textLine.Text;
        }

        var property = target.GetType().GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            value = property.GetValue(target);
            return true;
        }

        var field = target.GetType().GetField(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        if (field is not null)
        {
            value = field.GetValue(target);
            return true;
        }

        value = null;
        return false;
    }

    private string ExpandAliasPath(string reference)
    {
        if (_runtime.TypeResolver is not DotNetTypeResolver resolver)
        {
            return reference;
        }

        foreach (var (alias, target) in resolver.GetAliases())
        {
            if (string.Equals(reference, alias, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }

            if (reference.StartsWith(alias + ".", StringComparison.OrdinalIgnoreCase))
            {
                return target + reference[alias.Length..];
            }
        }

        return reference;
    }

    private static bool TryGetGenericTypeArgumentContext(string tokenText, int tokenStart, int cursorIndex, out GenericTypeArgumentContext context)
    {
        var openAngleIndex = tokenText.LastIndexOf('<');

        if (openAngleIndex < 0)
        {
            context = default;
            return false;
        }

        var closeAngleIndex = tokenText.LastIndexOf('>');

        if (closeAngleIndex > openAngleIndex)
        {
            context = default;
            return false;
        }

        var separatorIndex = tokenText.LastIndexOf(',');
        var valueStart = Math.Max(openAngleIndex, separatorIndex) + 1;
        var partial = tokenText[valueStart..].TrimStart();
        var trimmedLeading = tokenText[valueStart..].Length - partial.Length;
        var replacementStart = tokenStart + valueStart + trimmedLeading;

        context = new GenericTypeArgumentContext(replacementStart, cursorIndex - replacementStart, partial);
        return true;
    }

    private static int FindCompletionTokenStart(string text, int cursorIndex)
    {
        var index = cursorIndex;

        while (index > 0 && IsCompletionTokenCharacter(text[index - 1]))
        {
            index--;
        }

        return index;
    }

    private static int FindCompletionTokenEnd(string text, int cursorIndex)
    {
        var index = cursorIndex;

        while (index < text.Length && IsCompletionTokenCharacter(text[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsCompletionTokenCharacter(char character)
    {
        return !char.IsWhiteSpace(character) &&
               character is not ('|' or '#' or '(' or ')' or '{' or '}' or '[' or ']' or ';' or '"' or '\'' or '>' or '<' or '&' or '!' or '=' or ':');
    }

    private static bool TryGetQuotedSpanAtCursor(string text, int cursorIndex, out (int Start, int Length, string Token) span)
    {
        foreach (var quote in new[] { '"', '\'' })
        {
            if (TryGetQuotedSpanAtCursor(text, cursorIndex, quote, out span))
            {
                return true;
            }
        }

        span = default;
        return false;
    }

    private static bool TryGetQuotedSpanAtCursor(string text, int cursorIndex, char quote, out (int Start, int Length, string Token) span)
    {
        var searchIndex = cursorIndex;

        if (searchIndex > 0 && text[searchIndex - 1] == quote && !IsEscaped(text, searchIndex - 1))
        {
            searchIndex -= 1;
        }

        var openIndex = -1;
        for (var index = searchIndex - 1; index >= 0; index -= 1)
        {
            if (text[index] == quote && !IsEscaped(text, index))
            {
                openIndex = index;
                break;
            }
        }

        if (openIndex < 0)
        {
            span = default;
            return false;
        }

        var closeIndex = -1;
        for (var index = openIndex + 1; index < text.Length; index += 1)
        {
            if (text[index] == quote && !IsEscaped(text, index))
            {
                closeIndex = index;
                break;
            }
        }

        if (closeIndex < 0 || cursorIndex < openIndex || cursorIndex > closeIndex + 1)
        {
            span = default;
            return false;
        }

        span = (openIndex, closeIndex - openIndex + 1, text[openIndex..(closeIndex + 1)]);
        return true;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;

        for (var current = index - 1; current >= 0 && text[current] == '\\'; current -= 1)
        {
            slashCount += 1;
        }

        return slashCount % 2 == 1;
    }

    private static bool MatchesPrefix(string text, string prefix)
    {
        return string.IsNullOrEmpty(prefix) ||
               text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetPathSuggestions(string text, string tokenPrefix, int replacementStart, int replacementLength, out ReplCompletionResult? result)
    {
        if (!ShouldTreatAsPathContext(text, replacementStart, tokenPrefix))
        {
            result = null;
            return false;
        }

        var suggestions = GetPathSuggestions(tokenPrefix, IsQuotedTokenContext(text, replacementStart));
        result = suggestions.Count == 0
            ? null
            : new ReplCompletionResult(replacementStart, replacementLength, suggestions);
        return true;
    }

    private IReadOnlyList<ReplCompletionSuggestion> GetPathSuggestions(string tokenPrefix, bool isQuotedContext)
    {
        var suggestions = new List<ReplCompletionSuggestion>();

        if (tokenPrefix.StartsWith('~') && PathUtilities.DirectoryAliases is not null)
        {
            var separatorIndex = tokenPrefix.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], 1);

            if (separatorIndex < 0)
            {
                var aliasPrefix = tokenPrefix[1..];

                foreach (var (alias, aliasPath) in PathUtilities.DirectoryAliases.Aliases)
                {
                    if (alias.StartsWith(aliasPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var label = $"~{alias}{Path.DirectorySeparatorChar}";
                        suggestions.Add(new ReplCompletionSuggestion(
                            label,
                            aliasPath,
                            Priority: 0,
                            InsertText: BuildPathInsertText(label, isQuotedContext)));
                    }
                }

                if (suggestions.Count > 0)
                {
                    return OrderSuggestions(suggestions);
                }
            }
        }

        var directoryPart = string.Empty;
        var namePrefix = tokenPrefix;

        if (!string.IsNullOrEmpty(tokenPrefix))
        {
            var separatorIndex = tokenPrefix.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

            if (separatorIndex >= 0)
            {
                directoryPart = tokenPrefix[..(separatorIndex + 1)];
                namePrefix = tokenPrefix[(separatorIndex + 1)..];
            }
        }

        var searchBase = string.IsNullOrEmpty(directoryPart)
            ? _runtime.CurrentDirectory
            : PathUtilities.ResolvePath(_runtime.CurrentDirectory, directoryPart);

        if (!Directory.Exists(searchBase))
        {
            return Array.Empty<ReplCompletionSuggestion>();
        }

        IEnumerable<string> entries;

        try
        {
            entries = Directory.EnumerateFileSystemEntries(searchBase);
        }
        catch
        {
            return Array.Empty<ReplCompletionSuggestion>();
        }

        foreach (var entryPath in entries)
        {
            var name = Path.GetFileName(entryPath);

            if (string.IsNullOrEmpty(name) || !MatchesPrefix(name, namePrefix))
            {
                continue;
            }

            if (!namePrefix.StartsWith(".", StringComparison.Ordinal) && name.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            var isDirectory = Directory.Exists(entryPath);
            var suffix = isDirectory ? Path.DirectorySeparatorChar.ToString() : string.Empty;
            var label = directoryPart + name + suffix;
            suggestions.Add(new ReplCompletionSuggestion(
                label,
                isDirectory ? "Directory" : "File",
                Priority: isDirectory ? 0 : 10,
                InsertText: BuildPathInsertText(label, isQuotedContext)));
        }

        return OrderSuggestions(suggestions);
    }

    private bool ShouldTreatAsPathContext(string text, int replacementStart, string tokenPrefix)
    {
        if (tokenPrefix.StartsWith("$", StringComparison.Ordinal))
        {
            return false;
        }

        if (LooksLikePath(tokenPrefix))
        {
            return true;
        }

        var segmentPrefix = GetCurrentSegmentPrefix(text, replacementStart);
        var tokens = SplitSegmentTokens(segmentPrefix);

        if (tokens.Count == 0)
        {
            return false;
        }

        var commandName = tokens[0];

        if (string.Equals(commandName, "require", StringComparison.OrdinalIgnoreCase))
        {
            return tokens.Count == 1 || string.Equals(tokens[^1], "from", StringComparison.OrdinalIgnoreCase);
        }

        return PathFirstCommands.Contains(commandName);
    }

    private static bool IsCommandPosition(string text, int replacementStart)
    {
        var segmentPrefix = GetCurrentSegmentPrefix(text, replacementStart);
        return SplitSegmentTokens(segmentPrefix).Count == 0;
    }

    private static string GetCurrentSegmentPrefix(string text, int replacementStart)
    {
        var segmentStart = replacementStart;

        while (segmentStart > 0)
        {
            var character = text[segmentStart - 1];

            if (character is '|' or ';' or '&' or '\n' or '\r' or '{' or '}')
            {
                break;
            }

            segmentStart--;
        }

        return text[segmentStart..replacementStart].TrimStart();
    }

    private static IReadOnlyList<string> SplitSegmentTokens(string segmentPrefix)
    {
        return segmentPrefix
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool LooksLikePath(string tokenPrefix)
    {
        if (tokenPrefix.StartsWith("./", StringComparison.Ordinal) ||
            tokenPrefix.StartsWith(".\\", StringComparison.Ordinal) ||
            tokenPrefix.StartsWith("../", StringComparison.Ordinal) ||
            tokenPrefix.StartsWith("..\\", StringComparison.Ordinal) ||
            tokenPrefix.StartsWith("~/", StringComparison.Ordinal) ||
            tokenPrefix.StartsWith("~\\", StringComparison.Ordinal) ||
            tokenPrefix.StartsWith("/", StringComparison.Ordinal) ||
            tokenPrefix.StartsWith("\\", StringComparison.Ordinal) ||
            tokenPrefix.Contains(Path.DirectorySeparatorChar) ||
            tokenPrefix.Contains(Path.AltDirectorySeparatorChar))
        {
            return true;
        }

        if (tokenPrefix.StartsWith('~') && tokenPrefix.Length > 1 && PathUtilities.DirectoryAliases is not null)
        {
            var aliasPrefix = tokenPrefix[1..];
            return PathUtilities.DirectoryAliases.Aliases.Keys
                .Any(alias => alias.StartsWith(aliasPrefix, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool IsQuotedTokenContext(string text, int replacementStart)
    {
        return replacementStart > 0 && text[replacementStart - 1] == '"';
    }

    private static string BuildPathInsertText(string label, bool textWasQuoted)
    {
        if (textWasQuoted || !label.Any(char.IsWhiteSpace))
        {
            return label;
        }

        return "\"" + label
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static IReadOnlyList<ReplCompletionSuggestion> OrderSuggestions(IEnumerable<ReplCompletionSuggestion> suggestions)
    {
        return suggestions
            .DistinctBy(suggestion => suggestion.Label, StringComparer.OrdinalIgnoreCase)
            .OrderBy(suggestion => suggestion.Priority)
            .ThenBy(suggestion => suggestion.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetTypeLabel(Type type)
    {
        var tickIndex = type.Name.IndexOf('`');
        return tickIndex >= 0 ? type.Name[..tickIndex] : type.Name;
    }

    private readonly record struct GenericTypeArgumentContext(int ReplacementStart, int ReplacementLength, string Partial);
}

public sealed record ReplCompletionSuggestion(string Label, string Detail, int Priority = 100, string? InsertText = null)
{
    public string GetInsertText() => InsertText ?? Label;
}

public sealed record ReplCompletionResult(
    int ReplacementStart,
    int ReplacementLength,
    IReadOnlyList<ReplCompletionSuggestion> Suggestions);
