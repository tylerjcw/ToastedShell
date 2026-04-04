using System.Reflection;
using Tosh.Core;
using Tosh.Language.Parsing;

namespace Tosh.Lsp;

public sealed class ToshLanguageFeatures
{
    private static readonly IReadOnlyDictionary<string, string> SpecialVariables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["$tosh"] = "The live ToSh runtime namespace.",
        ["$env"] = "Read environment-variable values directly by name.",
        ["_"] = "The current pipeline item in predicates and block-driven pipeline commands.",
    };

    private static readonly IReadOnlyDictionary<string, string> Keywords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["func"] = "Define a function with an optional CLR return type.",
        ["var"] = "Declare a variable. Reference it later as `$name`.",
        ["alloc"] = "Allocate a native buffer into a variable by byte count or interop type size.",
        ["class"] = "Define a ToSh class with properties, constructors, and methods.",
        ["module"] = "Define a named ToSh module object with its own lexical scope.",
        ["enum"] = "Define a named enum with symbolic members and optional underlying numeric storage.",
        ["record"] = "Define a named data shape with positional construction and structural equality.",
        ["prop"] = "Declare a class property, computed member, or accessor-backed property.",
        ["shy"] = "Keep a declaration in the current lexical scope, or hide a class member from public inspection and external access.",
        ["static"] = "Mark a class member as belonging to the class rather than an instance.",
        ["global"] = "Publish a declaration to the session-wide scope.",
        ["export"] = "Publish a module declaration or export an environment value.",
        ["using"] = "Import CLR namespaces or type aliases in the current lexical scope.",
        ["require"] = "Load a `.tosh` module, `.dll`, `.csproj`, or native shared library once per session and import it lexically.",
        ["native"] = "In `require native`, load a native shared library for binding and invocation.",
        ["bind"] = "Attach native exports to a required native module as callable ToSh functions.",
        ["from"] = "Select a source path in selective `require` forms.",
        ["as"] = "Rename a CLR alias or required export.",
        ["out"] = "Mark a native binding parameter as output-only and return the updated value after the call.",
        ["ref"] = "Mark a native binding parameter as by-reference so the call can read and update it.",
        ["callconv"] = "Override the native calling convention for a bound export.",
        ["if"] = "Run a block conditionally.",
        ["else"] = "Fallback branch for `if`.",
        ["for"] = "Iterate an enumerable source.",
        ["in"] = "Membership operator and `for` loop keyword.",
        ["while"] = "Repeat while a condition is true.",
        ["until"] = "Repeat until a condition is true.",
        ["return"] = "Return early from a function or script.",
        ["throw"] = "Raise an error value.",
        ["try"] = "Begin a `try` / `catch` / `finally` block.",
        ["catch"] = "Handle a thrown value or failure.",
        ["finally"] = "Always run cleanup code.",
        ["switch"] = "Match a value against `case` clauses.",
        ["match"] = "Expression-style branching with ordered `=>` arms.",
        ["case"] = "A `switch` branch.",
        ["default"] = "Fallback `switch` branch or `match` arm.",
        ["break"] = "Exit the current loop.",
        ["continue"] = "Continue to the next loop iteration.",
        ["and"] = "Logical conjunction.",
        ["or"] = "Logical disjunction.",
        ["not"] = "Logical negation.",
        ["not-in"] = "Negated membership operator.",
        ["new"] = "Construct a CLR object or ToSh named type instance.",
        ["nameof"] = "Return the name of a variable or member path.",
        ["name-of"] = "Command-style alias for `nameof`.",
    };

    private readonly ToshRuntime _runtime;

    public ToshLanguageFeatures()
    {
        _runtime = ToshRuntime.CreateDefault();
    }

    public IReadOnlyList<LspDiagnostic> GetDiagnostics(string text, string sourceName)
    {
        var parseResult = ToshParser.Parse(text, sourceName);
        var map = new TextCoordinateMap(parseResult.SourceText);

        return parseResult.Diagnostics
            .Select(diagnostic => new LspDiagnostic(
                map.ToRange(diagnostic.Span.Start, diagnostic.Span.End),
                Severity: 1,
                Code: diagnostic.Code,
                Source: "tosh",
                Message: diagnostic.Help is { Length: > 0 }
                    ? $"{diagnostic.Title}\n{diagnostic.Help}"
                    : diagnostic.Title))
            .ToArray();
    }

    public IReadOnlyList<LspCompletionItem> GetCompletionItems(string text, LspPosition position, string sourceName = "<completion>")
    {
        var index = DeclarationIndex.Create(sourceName, text);
        var semantics = DocumentSemanticModel.Create(sourceName, text);
        var offset = new TextCoordinateMap(text).ToOffset(position);
        var variableContext = offset > 0 && text[offset - 1] == '$';
        var items = new Dictionary<string, LspCompletionItem>(StringComparer.OrdinalIgnoreCase);
        semantics.CreateTypeResolver(offset);
        var clrCatalog = ClrCompletionCatalog.Shared;

        if (TryGetUsingCompletionContext(text, offset, out var usingPathPrefix))
        {
            AddItems(items, GetNamespaceOrTypeCompletionItems(clrCatalog, usingPathPrefix));
            return items.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        if (TryGetQualifiedCompletionContext(text, offset, out var qualifiedTarget, out var qualifiedPartial))
        {
            var shellTargetClass = semantics.ResolveShellTargetClass(offset, qualifiedTarget);

            if (shellTargetClass is not null)
            {
                AddItems(items, GetShellMemberCompletions(
                    shellTargetClass,
                    staticOnly: !qualifiedTarget.StartsWith("$", StringComparison.Ordinal),
                    includeHidden: qualifiedTarget.StartsWith("$this", StringComparison.Ordinal),
                    partial: qualifiedPartial));
            }
            else
            {
                var targetType = semantics.ResolveReferenceType(offset, qualifiedTarget);

                if (targetType is not null)
                {
                    AddItems(items, clrCatalog.GetMemberCompletions(
                        targetType,
                        staticOnly: !qualifiedTarget.StartsWith('$') && !string.Equals(qualifiedTarget, "_", StringComparison.Ordinal),
                        partial: qualifiedPartial));
                }

                var expandedTarget = ExpandAliasPath(qualifiedTarget, semantics.GetVisibleAliases(offset));

                if (clrCatalog.NamespaceExists(expandedTarget))
                {
                    AddItems(items, clrCatalog.GetNamespaceAndTypeCompletions(expandedTarget, qualifiedPartial));
                }
            }

            if (items.Count > 0)
            {
                return items.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        if (variableContext)
        {
            foreach (var variable in index.GetVisibleVariables(offset))
            {
                items["$" + variable] = new LspCompletionItem(
                    "$" + variable,
                    Kind: 6,
                    Detail: "Variable",
                    Documentation: "Variable declared in the current document.");
            }

            foreach (var (name, description) in SpecialVariables.Where(entry => entry.Key.StartsWith('$')))
            {
                items[name] = new LspCompletionItem(name, Kind: 6, Detail: "Special variable", Documentation: description);
            }

            return items.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        foreach (var (keyword, description) in Keywords)
        {
            items[keyword] = new LspCompletionItem(keyword, Kind: 14, Detail: "Keyword", Documentation: description);
        }

        foreach (var (name, description) in SpecialVariables)
        {
            items[name] = new LspCompletionItem(name, Kind: 6, Detail: "Special variable", Documentation: description);
        }

        foreach (var command in _runtime.Commands.All)
        {
            items[command.Name] = new LspCompletionItem(
                command.Name,
                Kind: 3,
                Detail: "Built-in command",
                Documentation: command.Description);
        }

        foreach (var symbol in index.GetVisibleFunctions(offset))
        {
            var overloads = index.GetVisibleFunctionOverloads(offset, symbol);
            items[symbol] = new LspCompletionItem(
                symbol,
                Kind: 18,
                Detail: overloads.Count == 1 ? "Function" : $"Function ({overloads.Count} overloads)",
                Documentation: string.Join("\n", overloads.Take(6).Select(FormatTopLevelFunctionSignature)));
        }

        foreach (var symbol in index.GetVisibleTypeLikeSymbols(offset))
        {
            items[symbol] = new LspCompletionItem(
                symbol,
                Kind: 7,
                Detail: "Type declared in current document",
                Documentation: "ToSh type declared in the current document.");
        }

        foreach (var symbol in index.GetVisibleModules(offset))
        {
            items[symbol] = new LspCompletionItem(
                symbol,
                Kind: 9,
                Detail: "Module declared in current document",
                Documentation: "ToSh module declared in the current document.");
        }

        var rootPrefix = GetSimpleCompletionPrefix(text, offset);

        if (!string.IsNullOrWhiteSpace(rootPrefix))
        {
            AddItems(items, clrCatalog.GetNamespaceAndTypeCompletions(string.Empty, rootPrefix));
            AddItems(items, clrCatalog.GetBuiltInAliasCompletions(rootPrefix));
            AddItems(items, clrCatalog.GetAliasCompletions(
                semantics.GetVisibleAliases(offset),
                rootPrefix,
                targetPath => semantics.CreateTypeResolver(offset).Resolve(targetPath)));
            AddItems(items, clrCatalog.GetImportedTypeCompletions(semantics.GetVisibleImports(offset), rootPrefix));
        }

        return items.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public LspSignatureHelp? GetSignatureHelp(string text, string sourceName, LspPosition position)
    {
        var parseResult = ToshParser.Parse(text, sourceName);
        var offset = new TextCoordinateMap(parseResult.SourceText).ToOffset(position);
        var semantics = DocumentSemanticModel.Create(sourceName, parseResult.SourceText);
        var declarations = DeclarationIndex.Create(sourceName, parseResult.SourceText);

        if (FindCommandCallSite(parseResult.Statement, parseResult.SourceText, offset) is { } commandCallSite)
        {
            var overloads = declarations.GetVisibleFunctionOverloads(offset, commandCallSite.Command.Name);
            if (overloads.Count > 0)
            {
                return CreateTopLevelFunctionSignatureHelp(overloads, commandCallSite.ActiveParameter);
            }
        }

        if (FindCallSite(parseResult.Statement, parseResult.SourceText, offset) is not { } callSite)
        {
            return null;
        }

        return callSite.Argument switch
        {
            NewObjectArgumentSyntax newObject => CreateShellConstructorSignatureHelp(
                    semantics.ResolveVisibleShellClass(offset, newObject.TypeName),
                    callSite.ActiveParameter)
                ?? CreateConstructorSignatureHelp(
                    semantics.CreateTypeResolver(offset).Resolve(newObject.TypeName),
                    callSite.ActiveParameter),
            StaticMethodCallArgumentSyntax staticCall => CreateShellStaticCallSignatureHelp(
                    semantics,
                    staticCall.Path,
                    offset,
                    callSite.ActiveParameter)
                ?? CreateStaticCallSignatureHelp(
                    semantics,
                    staticCall.Path,
                    offset,
                    callSite.ActiveParameter),
            MethodCallArgumentSyntax methodCall => CreateShellInstanceMethodSignatureHelp(
                    TryGetSourceSlice(text, methodCall.Target.Span) is { } targetReference
                        ? semantics.ResolveShellTargetClass(offset, targetReference)
                        : null,
                    methodCall.MethodName,
                    callSite.ActiveParameter)
                ?? CreateInstanceMethodSignatureHelp(
                    semantics.ResolveArgumentType(methodCall.Target, offset),
                    methodCall.MethodName,
                    callSite.ActiveParameter),
            _ => null,
        };
    }

    public IReadOnlyList<LspLocation> GetDefinitions(string text, string sourceName, LspPosition position)
    {
        return DeclarationIndex.Create(sourceName, text).FindDefinitions(position);
    }

    public LspHover? GetHover(string text, string sourceName, LspPosition position)
    {
        var index = DeclarationIndex.Create(sourceName, text);
        var map = new TextCoordinateMap(text);
        var offset = map.ToOffset(position);
        var token = FindWordAt(text, offset);

        if (string.IsNullOrWhiteSpace(token.Word))
        {
            return null;
        }

        string? description = null;
        var normalizedWord = token.Word;
        var semantics = DocumentSemanticModel.Create(sourceName, text);

        if (SpecialVariables.TryGetValue(normalizedWord, out var special))
        {
            description = special;
        }
        else if (Keywords.TryGetValue(normalizedWord, out var keyword))
        {
            description = keyword;
        }
        else if (GetShellHoverDescription(semantics, offset, normalizedWord) is { } shellDescription)
        {
            description = shellDescription;
        }
        else if (GetTopLevelFunctionHoverDescription(index, offset, normalizedWord) is { } functionDescription)
        {
            description = functionDescription;
        }
        else if (HelpCatalog.ResolveTopic(_runtime, normalizedWord) is { } topic)
        {
            description = topic.Description;
        }

        if (description is null)
        {
            description = GetClrHoverDescription(semantics, offset, normalizedWord);
        }

        if (description is null)
        {
            return null;
        }

        return new LspHover(
            new LspMarkupContent("markdown", $"**{normalizedWord}**\n\n{description}"),
            map.ToRange(token.Start, token.End));
    }

    public IReadOnlyList<LspDocumentSymbol> GetDocumentSymbols(string text, string sourceName)
    {
        return DeclarationIndex.Create(sourceName, text)
            .GetSymbols()
            .Select(symbol => new LspDocumentSymbol(
                symbol.Name,
                symbol.Detail,
                symbol.SymbolKind,
                symbol.Range,
                symbol.SelectionRange,
                Array.Empty<LspDocumentSymbol>()))
            .ToArray();
    }

    public IReadOnlyList<LspSymbolInformation> GetSymbolInformations(string text, string sourceName)
    {
        return DeclarationIndex.Create(sourceName, text)
            .GetSymbols()
            .Select(symbol => new LspSymbolInformation(
                symbol.Name,
                symbol.SymbolKind,
                new LspLocation(sourceName, symbol.Range),
                symbol.Detail))
            .ToArray();
    }

    private static (string Word, int Start, int End) FindWordAt(string text, int offset)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (string.Empty, 0, 0);
        }

        var index = Math.Clamp(offset, 0, Math.Max(0, text.Length - 1));

        bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch is '$' or '_' or '-' or '.';

        if (!IsWordChar(text[index]) && index > 0 && IsWordChar(text[index - 1]))
        {
            index--;
        }

        if (!IsWordChar(text[index]))
        {
            return (string.Empty, index, index);
        }

        var start = index;
        while (start > 0 && IsWordChar(text[start - 1]))
        {
            start--;
        }

        var end = index + 1;
        while (end < text.Length && IsWordChar(text[end]))
        {
            end++;
        }

        return (text[start..end], start, end);
    }

    private static void AddItems(IDictionary<string, LspCompletionItem> target, IEnumerable<LspCompletionItem> items)
    {
        foreach (var item in items)
        {
            target[item.Label] = item;
        }
    }

    private static IReadOnlyList<LspCompletionItem> GetNamespaceOrTypeCompletionItems(ClrCompletionCatalog clrCatalog, string rawPathPrefix)
    {
        SplitQualifier(rawPathPrefix, out var qualifier, out var partial);
        return clrCatalog.GetNamespaceAndTypeCompletions(qualifier, partial);
    }

    private static string GetSimpleCompletionPrefix(string text, int offset)
    {
        var token = FindWordAt(text, offset);

        if (string.IsNullOrWhiteSpace(token.Word) ||
            token.Word.StartsWith('$') ||
            token.Word.Contains('.'))
        {
            return string.Empty;
        }

        return token.Word;
    }

    private static string? TryGetSourceSlice(string text, TextSpan span)
    {
        if (span.Start < 0 || span.End < span.Start || span.End > text.Length)
        {
            return null;
        }

        return text[span.Start..span.End];
    }

    private static bool TryGetQualifiedCompletionContext(string text, int offset, out string target, out string partial)
    {
        target = string.Empty;
        partial = string.Empty;

        if (offset <= 0)
        {
            return false;
        }

        var start = offset;

        while (start > 0 && IsCompletionPathChar(text[start - 1]))
        {
            start--;
        }

        if (start == offset)
        {
            return false;
        }

        var candidate = text[start..offset];
        var separatorIndex = candidate.LastIndexOf('.');

        if (separatorIndex <= 0)
        {
            return false;
        }

        target = candidate[..separatorIndex];
        partial = candidate[(separatorIndex + 1)..];
        return true;
    }

    private static bool TryGetUsingCompletionContext(string text, int offset, out string pathPrefix)
    {
        pathPrefix = string.Empty;
        var boundedOffset = Math.Clamp(offset, 0, text.Length);
        var lineStart = text.LastIndexOf('\n', Math.Max(0, boundedOffset - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var linePrefix = text[lineStart..boundedOffset];

        if (!linePrefix.TrimStart().StartsWith("using ", StringComparison.Ordinal))
        {
            return false;
        }

        var afterKeyword = linePrefix.TrimStart()[6..];

        if (afterKeyword.Contains(' ') ||
            afterKeyword.Contains('\t') ||
            afterKeyword.Contains('='))
        {
            return false;
        }

        pathPrefix = afterKeyword;
        return true;
    }

    private static void SplitQualifier(string rawPathPrefix, out string qualifier, out string partial)
    {
        var separatorIndex = rawPathPrefix.LastIndexOf('.');

        if (separatorIndex < 0)
        {
            qualifier = string.Empty;
            partial = rawPathPrefix;
            return;
        }

        qualifier = rawPathPrefix[..separatorIndex];
        partial = rawPathPrefix[(separatorIndex + 1)..];
    }

    private static string ExpandAliasPath(string path, IReadOnlyList<KeyValuePair<string, string>> aliases)
    {
        foreach (var (alias, target) in aliases)
        {
            if (string.Equals(path, alias, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }

            if (path.StartsWith(alias + ".", StringComparison.OrdinalIgnoreCase))
            {
                return target + path[alias.Length..];
            }
        }

        return path;
    }

    private static IReadOnlyList<LspCompletionItem> GetShellMemberCompletions(
        DocumentSemanticModel.ShellClassSymbol shellClass,
        bool staticOnly,
        bool includeHidden,
        string partial)
    {
        var items = new Dictionary<string, LspCompletionItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in shellClass.Properties
                     .Where(property => property.IsStatic == staticOnly)
                     .Where(property => includeHidden || !property.IsHidden))
        {
            if (!MatchesPrefix(property.Name, partial))
            {
                continue;
            }

            items[property.Name] = new LspCompletionItem(
                property.Name,
                Kind: 10,
                Detail: property.IsComputed ? "Computed property" : "Property",
                Documentation: FormatShellPropertySignature(property));
        }

        foreach (var methodGroup in shellClass.Methods
                     .Where(method => method.IsStatic == staticOnly)
                     .Where(method => includeHidden || !method.IsHidden)
                     .GroupBy(method => method.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!MatchesPrefix(methodGroup.Key, partial))
            {
                continue;
            }

            var overloads = methodGroup
                .OrderBy(method => method.Parameters.Count)
                .ToArray();

            items[methodGroup.Key] = new LspCompletionItem(
                methodGroup.Key,
                Kind: 2,
                Detail: overloads.Length == 1 ? "Method" : $"Method ({overloads.Length} overloads)",
                Documentation: string.Join("\n", overloads.Take(3).Select(FormatShellMethodSignature)));
        }

        return items.Values
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? GetShellHoverDescription(DocumentSemanticModel semantics, int offset, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (token.StartsWith("$", StringComparison.Ordinal))
        {
            var trimmed = token[1..];

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            if (!trimmed.Contains('.', StringComparison.Ordinal))
            {
                return semantics.ResolveVisibleVariableShellClass(offset, trimmed) is { } shellClass
                    ? $"Variable\n\n```tosh\n{shellClass.Name} ${trimmed}\n```"
                    : null;
            }

            return DescribeShellReference(semantics.ResolveShellReference(offset, token));
        }

        return DescribeShellReference(semantics.ResolveShellReference(offset, token));
    }

    private static string? GetTopLevelFunctionHoverDescription(DeclarationIndex index, int offset, string token)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            token.StartsWith("$", StringComparison.Ordinal) ||
            token.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var overloads = index.GetVisibleFunctionOverloads(offset, token);
        if (overloads.Count == 0)
        {
            return null;
        }

        var label = overloads.Count == 1 ? "Function" : "Functions";
        var signatures = string.Join(
            "\n",
            overloads.Take(6).Select(FormatTopLevelFunctionSignature));
        var overflow = overloads.Count > 6 ? $"\n... {overloads.Count - 6} more overload(s)" : string.Empty;
        return $"{label}\n\n```tosh\n{signatures}{overflow}\n```";
    }

    private static string? GetClrHoverDescription(DocumentSemanticModel semantics, int offset, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        if (token.StartsWith("$", StringComparison.Ordinal))
        {
            return DescribeVariableOrInstancePath(semantics, offset, token);
        }

        if (string.Equals(token, "_", StringComparison.Ordinal) ||
            token.StartsWith("_.", StringComparison.Ordinal))
        {
            return null;
        }

        return DescribeTypeOrStaticPath(semantics, offset, token);
    }

    private static string? DescribeVariableOrInstancePath(DocumentSemanticModel semantics, int offset, string token)
    {
        var trimmed = token[1..];

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var segments = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return null;
        }

        var rootReference = "$" + segments[0];
        var currentType = semantics.ResolveReferenceType(offset, rootReference);

        if (currentType is null)
        {
            return null;
        }

        if (segments.Length == 1)
        {
            return $"Variable\n\n```tosh\n{ClrMetadataFormatting.FormatTypeDisplayName(currentType)} ${segments[0]}\n```";
        }

        for (var index = 1; index < segments.Length; index++)
        {
            var isLast = index == segments.Length - 1;
            var description = DescribeMember(currentType, segments[index], staticOnly: false, out var nextType);

            if (description is null)
            {
                return null;
            }

            if (isLast)
            {
                return description;
            }

            if (nextType is null)
            {
                return null;
            }

            currentType = nextType;
        }

        return null;
    }

    private static string? DescribeTypeOrStaticPath(DocumentSemanticModel semantics, int offset, string token)
    {
        var resolver = semantics.CreateTypeResolver(offset);
        var directType = resolver.Resolve(token);

        if (directType is not null)
        {
            return FormatTypeDescription(directType);
        }

        var segments = token.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = resolver.Resolve(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            var currentType = type;

            for (var index = prefixLength; index < segments.Length; index++)
            {
                var isLast = index == segments.Length - 1;
                var description = DescribeMember(currentType, segments[index], staticOnly: index == prefixLength, out var nextType);

                if (description is null)
                {
                    return null;
                }

                if (isLast)
                {
                    return description;
                }

                if (nextType is null)
                {
                    return null;
                }

                currentType = nextType;
            }
        }

        return null;
    }

    private static string? DescribeMember(Type declaringType, string memberName, bool staticOnly, out Type? nextType)
    {
        nextType = null;

        if (staticOnly)
        {
            var nestedType = declaringType.GetNestedType(memberName, BindingFlags.Public);

            if (nestedType is not null)
            {
                nextType = nestedType;
                return FormatTypeDescription(nestedType);
            }
        }

        var bindingFlags = BindingFlags.Public | (staticOnly ? BindingFlags.Static : BindingFlags.Instance);
        var property = declaringType.GetProperty(memberName, bindingFlags);

        if (property is not null && property.GetIndexParameters().Length == 0)
        {
            nextType = property.PropertyType;
            return $"Property\n\n```tosh\n{ClrMetadataFormatting.FormatTypeDisplayName(property.PropertyType)} {property.Name}\n```";
        }

        var field = declaringType.GetField(memberName, bindingFlags);

        if (field is not null && !field.IsSpecialName)
        {
            nextType = field.FieldType;
            return $"Field\n\n```tosh\n{ClrMetadataFormatting.FormatTypeDisplayName(field.FieldType)} {field.Name}\n```";
        }

        var methods = declaringType.GetMethods(bindingFlags)
            .Where(method => !method.IsSpecialName && string.Equals(method.Name, memberName, StringComparison.Ordinal))
            .OrderBy(method => method.GetParameters().Length)
            .ToArray();

        if (methods.Length == 0)
        {
            return null;
        }

        nextType = methods[0].ReturnType == typeof(void) ? declaringType : methods[0].ReturnType;
        var visibleMethods = methods.Take(6).Select(ClrMetadataFormatting.FormatMethodSignature).ToArray();
        var overflowSuffix = methods.Length > visibleMethods.Length
            ? $"\n... {methods.Length - visibleMethods.Length} more overload(s)"
            : string.Empty;

        return $"Method{(methods.Length > 1 ? "s" : string.Empty)}\n\n```tosh\n{string.Join("\n", visibleMethods)}{overflowSuffix}\n```";
    }

    private static string FormatTypeDescription(Type type)
    {
        var kind = type.IsEnum
            ? "Enum"
            : type.IsInterface
                ? "Interface"
                : type.IsValueType
                    ? "Struct"
                    : type.IsClass
                        ? "Class"
                        : "Type";

        return $"{kind}\n\n```tosh\n{ClrMetadataFormatting.FormatTypeDisplayName(type)}\n```";
    }

    private static string? DescribeShellReference(DocumentSemanticModel.ShellReferenceSymbol? reference)
    {
        return reference switch
        {
            DocumentSemanticModel.ShellReferenceSymbol.Class shellClass => $"Class\n\n```tosh\n{shellClass.Symbol.Name}\n```",
            DocumentSemanticModel.ShellReferenceSymbol.Property property => $"Property\n\n```tosh\n{FormatShellPropertySignature(property.Symbol)}\n```",
            DocumentSemanticModel.ShellReferenceSymbol.Method method => $"Method{(method.Overloads.Count > 1 ? "s" : string.Empty)}\n\n```tosh\n{string.Join("\n", method.Overloads.Take(6).Select(FormatShellMethodSignature))}{(method.Overloads.Count > 6 ? $"\n... {method.Overloads.Count - 6} more overload(s)" : string.Empty)}\n```",
            _ => null,
        };
    }

    private static string FormatShellPropertySignature(DocumentSemanticModel.ShellClassPropertySymbol property)
    {
        var typeName = NormalizeShellTypeName(property.TypeName);
        return $"{typeName} {property.Name}";
    }

    private static string FormatShellMethodSignature(DocumentSemanticModel.ShellClassMethodSymbol method)
    {
        var modifier = method.IsStatic ? "static " : string.Empty;
        return $"{modifier}{NormalizeShellTypeName(method.ReturnTypeName)} {method.Name}({FormatShellParameters(method.Parameters)})";
    }

    private static string FormatTopLevelFunctionSignature(DeclarationIndex.IndexedFunctionDeclaration function)
    {
        var returnType = string.IsNullOrWhiteSpace(function.ReturnTypeName)
            ? string.Empty
            : $" -> {function.ReturnTypeName}";
        return $"func {function.Name}({FormatShellParameters(function.Parameters)}){returnType}";
    }

    private static string FormatShellConstructorSignature(string className, DocumentSemanticModel.ShellClassConstructorSymbol constructor)
    {
        return $"{className}({FormatShellParameters(constructor.Parameters)})";
    }

    private static string FormatShellParameters(IReadOnlyList<FunctionParameterSyntax> parameters)
    {
        return string.Join(
            ", ",
            parameters.Select(parameter =>
            {
                var suffix = parameter.IsOptional ? "?" : string.Empty;
                var rest = parameter.IsRest ? "..." : string.Empty;
                return string.IsNullOrWhiteSpace(parameter.TypeName)
                    ? $"{parameter.Name}{suffix}{rest}"
                    : $"{parameter.Name}{suffix}{rest}: {parameter.TypeName}";
            }));
    }

    private static string NormalizeShellTypeName(string? typeName)
    {
        return string.IsNullOrWhiteSpace(typeName) ? "object" : typeName;
    }

    private static bool MatchesPrefix(string text, string prefix)
    {
        return string.IsNullOrEmpty(prefix) ||
               text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompletionPathChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch is '$' or '_' or '.';
    }

    private static LspSignatureHelp? CreateShellConstructorSignatureHelp(DocumentSemanticModel.ShellClassSymbol? shellClass, int activeParameter)
    {
        if (shellClass is null)
        {
            return null;
        }

        var constructors = shellClass.Constructors
            .OrderBy(constructor => constructor.Parameters.Count)
            .ToArray();

        if (constructors.Length == 0)
        {
            return null;
        }

        var signatures = constructors
            .Select(constructor => new LspSignatureInformation(
                FormatShellConstructorSignature(shellClass.Name, constructor),
                Documentation: null,
                Parameters: constructor.Parameters
                    .Select(parameter => new LspParameterInformation(FormatShellParameter(parameter)))
                    .ToArray()))
            .ToArray();

        return CreateShellSignatureHelp(constructors.Select(constructor => constructor.Parameters.Count).ToArray(), signatures, activeParameter);
    }

    private static LspSignatureHelp? CreateConstructorSignatureHelp(Type? type, int activeParameter)
    {
        if (type is null)
        {
            return null;
        }

        return CreateSignatureHelp(
            type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Cast<MethodBase>()
                .ToArray(),
            activeParameter,
            constructor => ClrMetadataFormatting.FormatConstructorSignature((ConstructorInfo)constructor));
    }

    private static LspSignatureHelp? CreateStaticCallSignatureHelp(
        DocumentSemanticModel semantics,
        string path,
        int offset,
        int activeParameter)
    {
        var resolver = semantics.CreateTypeResolver(offset);
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var prefixLength = segments.Length - 1; prefixLength >= 1; prefixLength--)
        {
            var type = resolver.Resolve(string.Join('.', segments.Take(prefixLength)));

            if (type is null)
            {
                continue;
            }

            return CreateMethodSignatureHelp(type, segments[^1], activeParameter, staticOnly: true);
        }

        return null;
    }

    private static LspSignatureHelp? CreateShellStaticCallSignatureHelp(
        DocumentSemanticModel semantics,
        string path,
        int offset,
        int activeParameter)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 2 &&
            semantics.ResolveVisibleShellClass(offset, segments[0]) is { } shellClass)
        {
            var methods = shellClass.Methods
                .Where(method => method.IsStatic && string.Equals(method.Name, segments[1], StringComparison.OrdinalIgnoreCase))
                .OrderBy(method => method.Parameters.Count)
                .ToArray();

            return CreateShellMethodSignatureHelp(methods, activeParameter);
        }

        return null;
    }

    private static LspSignatureHelp? CreateShellInstanceMethodSignatureHelp(
        DocumentSemanticModel.ShellClassSymbol? shellClass,
        string methodName,
        int activeParameter)
    {
        if (shellClass is null)
        {
            return null;
        }

        var methods = shellClass.Methods
            .Where(method => !method.IsStatic && string.Equals(method.Name, methodName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(method => method.Parameters.Count)
            .ToArray();

        return CreateShellMethodSignatureHelp(methods, activeParameter);
    }

    private static LspSignatureHelp? CreateInstanceMethodSignatureHelp(Type? targetType, string methodName, int activeParameter)
    {
        return targetType is null
            ? null
            : CreateMethodSignatureHelp(targetType, methodName, activeParameter, staticOnly: false);
    }

    private static LspSignatureHelp? CreateMethodSignatureHelp(Type type, string methodName, int activeParameter, bool staticOnly)
    {
        var bindingFlags = BindingFlags.Public | (staticOnly ? BindingFlags.Static : BindingFlags.Instance);
        var methods = type.GetMethods(bindingFlags)
            .Where(method => !method.IsSpecialName && string.Equals(method.Name, methodName, StringComparison.Ordinal))
            .OrderBy(method => method.GetParameters().Length)
            .Cast<MethodBase>()
            .ToArray();

        return CreateSignatureHelp(methods, activeParameter, method => ClrMetadataFormatting.FormatMethodSignature((MethodInfo)method));
    }

    private static LspSignatureHelp? CreateShellMethodSignatureHelp(
        IReadOnlyList<DocumentSemanticModel.ShellClassMethodSymbol> methods,
        int activeParameter)
    {
        if (methods.Count == 0)
        {
            return null;
        }

        var signatures = methods
            .Select(method => new LspSignatureInformation(
                FormatShellMethodSignature(method),
                Documentation: null,
                Parameters: method.Parameters
                    .Select(parameter => new LspParameterInformation(FormatShellParameter(parameter)))
                    .ToArray()))
            .ToArray();

        return CreateShellSignatureHelp(methods.Select(method => method.Parameters.Count).ToArray(), signatures, activeParameter);
    }

    private static LspSignatureHelp CreateTopLevelFunctionSignatureHelp(
        IReadOnlyList<DeclarationIndex.IndexedFunctionDeclaration> functions,
        int activeParameter)
    {
        var ordered = functions
            .OrderBy(function => function.Parameters.Count)
            .ThenBy(function => FormatTopLevelFunctionSignature(function), StringComparer.Ordinal)
            .ToArray();

        var signatures = ordered
            .Select(function => new LspSignatureInformation(
                FormatTopLevelFunctionSignature(function),
                Documentation: function.IsCommandWrapper ? "Command-wrapper function" : null,
                Parameters: function.Parameters
                    .Select(parameter => new LspParameterInformation(FormatShellParameter(parameter)))
                    .ToArray()))
            .ToArray();

        return CreateShellSignatureHelp(ordered.Select(function => function.Parameters.Count).ToArray(), signatures, activeParameter);
    }

    private static LspSignatureHelp? CreateSignatureHelp(
        IReadOnlyList<MethodBase> methods,
        int activeParameter,
        Func<MethodBase, string> formatLabel)
    {
        if (methods.Count == 0)
        {
            return null;
        }

        var signatures = methods
            .Select(method => new LspSignatureInformation(
                formatLabel(method),
                Documentation: null,
                Parameters: method.GetParameters()
                    .Select(parameter => new LspParameterInformation(
                        ClrMetadataFormatting.FormatParameter(parameter)))
                    .ToArray()))
            .ToArray();

        var activeSignature = methods
            .Select((method, index) => new
            {
                index,
                parameterCount = method.GetParameters().Length,
                preferred = method.GetParameters().Length > activeParameter ? 0 : 1,
                delta = Math.Abs(method.GetParameters().Length - Math.Max(1, activeParameter + 1)),
            })
            .OrderBy(candidate => candidate.preferred)
            .ThenBy(candidate => candidate.delta)
            .ThenBy(candidate => candidate.parameterCount)
            .Select(candidate => candidate.index)
            .First();

        var activeMethodParameterCount = methods[activeSignature].GetParameters().Length;
        var boundedActiveParameter = activeMethodParameterCount == 0
            ? 0
            : Math.Min(activeParameter, activeMethodParameterCount - 1);

        return new LspSignatureHelp(signatures, activeSignature, boundedActiveParameter);
    }

    private static LspSignatureHelp CreateShellSignatureHelp(
        IReadOnlyList<int> parameterCounts,
        IReadOnlyList<LspSignatureInformation> signatures,
        int activeParameter)
    {
        var activeSignature = parameterCounts
            .Select((parameterCount, index) => new
            {
                index,
                parameterCount,
                preferred = parameterCount > activeParameter ? 0 : 1,
                delta = Math.Abs(parameterCount - Math.Max(1, activeParameter + 1)),
            })
            .OrderBy(candidate => candidate.preferred)
            .ThenBy(candidate => candidate.delta)
            .ThenBy(candidate => candidate.parameterCount)
            .Select(candidate => candidate.index)
            .First();

        var activeMethodParameterCount = parameterCounts[activeSignature];
        var boundedActiveParameter = activeMethodParameterCount == 0
            ? 0
            : Math.Min(activeParameter, activeMethodParameterCount - 1);

        return new LspSignatureHelp(signatures, activeSignature, boundedActiveParameter);
    }

    private static string FormatShellParameter(FunctionParameterSyntax parameter)
    {
        var suffix = parameter.IsOptional ? "?" : string.Empty;
        var rest = parameter.IsRest ? "..." : string.Empty;
        return string.IsNullOrWhiteSpace(parameter.TypeName)
            ? $"{parameter.Name}{suffix}{rest}"
            : $"{parameter.Name}{suffix}{rest}: {parameter.TypeName}";
    }

    private static CallSite? FindCallSite(StatementSyntax statement, string text, int offset)
    {
        var candidates = new List<CallSite>();
        CollectCallSites(statement, text, offset, candidates);
        return candidates
            .OrderBy(candidate => candidate.Argument.Span.Length)
            .FirstOrDefault();
    }

    private static CommandCallSite? FindCommandCallSite(StatementSyntax statement, string text, int offset)
    {
        var candidates = new List<CommandCallSite>();
        CollectCommandCallSites(statement, text, offset, candidates);
        return candidates
            .OrderBy(candidate => candidate.Command.Span.Length)
            .FirstOrDefault();
    }

    private static void CollectCallSites(StatementSyntax statement, string text, int offset, ICollection<CallSite> matches)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case PipelineStatementSyntax pipelineStatement:
                CollectCallSites(pipelineStatement.Pipeline, text, offset, matches);
                break;

            case VariableDeclarationStatementSyntax variable when variable.Value is not null:
                CollectCallSites(variable.Value, text, offset, matches);
                break;

            case VariableAssignmentStatementSyntax assignment:
                CollectCallSites(assignment.Value, text, offset, matches);
                break;

            case MemberAssignmentStatementSyntax assignment:
                CollectCallSites(assignment.Target, text, offset, matches);
                CollectCallSites(assignment.Value, text, offset, matches);
                break;

            case ReturnStatementSyntax @return when @return.Value is not null:
                CollectCallSites(@return.Value, text, offset, matches);
                break;

            case ThrowStatementSyntax @throw when @throw.Value is not null:
                CollectCallSites(@throw.Value, text, offset, matches);
                break;

            case FunctionDefinitionStatementSyntax function:
                foreach (var child in function.Body.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case IfStatementSyntax @if:
                CollectCallSites(@if.Condition, text, offset, matches);
                foreach (var child in @if.ThenBlock.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }

                if (@if.ElseBlock is not null)
                {
                    foreach (var child in @if.ElseBlock.Statements)
                    {
                        CollectCallSites(child, text, offset, matches);
                    }
                }
                break;

            case ForStatementSyntax @for:
                CollectCallSites(@for.Source, text, offset, matches);
                foreach (var child in @for.Body.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case WhileStatementSyntax @while:
                CollectCallSites(@while.Condition, text, offset, matches);
                foreach (var child in @while.Body.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case UntilStatementSyntax until:
                CollectCallSites(until.Condition, text, offset, matches);
                foreach (var child in until.Body.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case TryStatementSyntax @try:
                foreach (var child in @try.TryBlock.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }

                if (@try.CatchClause is not null)
                {
                    foreach (var child in @try.CatchClause.Body.Statements)
                    {
                        CollectCallSites(child, text, offset, matches);
                    }
                }

                if (@try.FinallyBlock is not null)
                {
                    foreach (var child in @try.FinallyBlock.Statements)
                    {
                        CollectCallSites(child, text, offset, matches);
                    }
                }
                break;

            case DeferStatementSyntax @defer:
                foreach (var child in @defer.Body.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case SwitchStatementSyntax @switch:
                CollectCallSites(@switch.Value, text, offset, matches);
                foreach (var @case in @switch.Cases)
                {
                    CollectCallSites(@case.MatchExpression, text, offset, matches);
                    foreach (var child in @case.Body.Statements)
                    {
                        CollectCallSites(child, text, offset, matches);
                    }
                }

                if (@switch.DefaultBlock is not null)
                {
                    foreach (var child in @switch.DefaultBlock.Statements)
                    {
                        CollectCallSites(child, text, offset, matches);
                    }
                }
                break;
        }
    }

    private static void CollectCommandCallSites(StatementSyntax statement, string text, int offset, ICollection<CommandCallSite> matches)
    {
        switch (statement)
        {
            case ScriptStatementSyntax script:
                foreach (var child in script.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case PipelineStatementSyntax pipelineStatement:
                CollectCommandCallSites(pipelineStatement.Pipeline, text, offset, matches);
                break;
            case VariableDeclarationStatementSyntax variable when variable.Value is not null:
                CollectCommandCallSites(variable.Value, text, offset, matches);
                break;
            case VariableAssignmentStatementSyntax assignment:
                CollectCommandCallSites(assignment.Value, text, offset, matches);
                break;
            case MemberAssignmentStatementSyntax assignment:
                CollectCommandCallSites(assignment.Target, text, offset, matches);
                CollectCommandCallSites(assignment.Value, text, offset, matches);
                break;
            case ReturnStatementSyntax @return when @return.Value is not null:
                CollectCommandCallSites(@return.Value, text, offset, matches);
                break;
            case ThrowStatementSyntax @throw when @throw.Value is not null:
                CollectCommandCallSites(@throw.Value, text, offset, matches);
                break;
            case FunctionDefinitionStatementSyntax function:
                foreach (var child in function.Body.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case IfStatementSyntax @if:
                CollectCommandCallSites(@if.Condition, text, offset, matches);
                foreach (var child in @if.ThenBlock.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                if (@if.ElseBlock is not null)
                {
                    foreach (var child in @if.ElseBlock.Statements)
                    {
                        CollectCommandCallSites(child, text, offset, matches);
                    }
                }
                break;
            case ForStatementSyntax @for:
                CollectCommandCallSites(@for.Source, text, offset, matches);
                foreach (var child in @for.Body.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case WhileStatementSyntax @while:
                CollectCommandCallSites(@while.Condition, text, offset, matches);
                foreach (var child in @while.Body.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case UntilStatementSyntax until:
                CollectCommandCallSites(until.Condition, text, offset, matches);
                foreach (var child in until.Body.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case TryStatementSyntax @try:
                foreach (var child in @try.TryBlock.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                if (@try.CatchClause is not null)
                {
                    foreach (var child in @try.CatchClause.Body.Statements)
                    {
                        CollectCommandCallSites(child, text, offset, matches);
                    }
                }
                if (@try.FinallyBlock is not null)
                {
                    foreach (var child in @try.FinallyBlock.Statements)
                    {
                        CollectCommandCallSites(child, text, offset, matches);
                    }
                }
                break;

            case DeferStatementSyntax @defer:
                foreach (var child in @defer.Body.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case SwitchStatementSyntax @switch:
                CollectCommandCallSites(@switch.Value, text, offset, matches);
                foreach (var @case in @switch.Cases)
                {
                    CollectCommandCallSites(@case.MatchExpression, text, offset, matches);
                    foreach (var child in @case.Body.Statements)
                    {
                        CollectCommandCallSites(child, text, offset, matches);
                    }
                }
                if (@switch.DefaultBlock is not null)
                {
                    foreach (var child in @switch.DefaultBlock.Statements)
                    {
                        CollectCommandCallSites(child, text, offset, matches);
                    }
                }
                break;
        }
    }

    private static void CollectCommandCallSites(PipelineSyntax pipeline, string text, int offset, ICollection<CommandCallSite> matches)
    {
        foreach (var stage in pipeline.Stages)
        {
            switch (stage)
            {
                case CommandSyntax command:
                    if (command.NameSpan.Start <= offset && offset <= command.Span.End)
                    {
                        matches.Add(new CommandCallSite(command, GetActiveCommandParameter(command, offset)));
                    }

                    foreach (var argument in command.Arguments)
                    {
                        CollectCommandCallSites(argument, text, offset, matches);
                    }
                    break;
                case ExpressionPipelineStageSyntax expression:
                    CollectCommandCallSites(expression.Expression, text, offset, matches);
                    break;
            }
        }

        if (pipeline.Redirections is null)
        {
            return;
        }

        foreach (var redirection in pipeline.Redirections)
        {
            CollectCommandCallSites(redirection.Target, text, offset, matches);
        }
    }

    private static void CollectCommandCallSites(ArgumentSyntax argument, string text, int offset, ICollection<CommandCallSite> matches)
    {
        if (argument.Span.Start > offset || offset > argument.Span.End)
        {
            return;
        }

        switch (argument)
        {
            case SplatArgumentSyntax splat:
                CollectCommandCallSites(splat.Value, text, offset, matches);
                break;
            case NewObjectArgumentSyntax newObject:
                foreach (var child in newObject.Arguments)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case StaticMethodCallArgumentSyntax staticMethodCall:
                foreach (var child in staticMethodCall.Arguments)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case MethodCallArgumentSyntax methodCall:
                CollectCommandCallSites(methodCall.Target, text, offset, matches);
                foreach (var child in methodCall.Arguments)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case MemberAccessArgumentSyntax member:
                CollectCommandCallSites(member.Target, text, offset, matches);
                break;
            case ArrayLiteralArgumentSyntax list:
                foreach (var item in list.Items)
                {
                    CollectCommandCallSites(item, text, offset, matches);
                }
                break;
            case RecordLiteralArgumentSyntax record:
                foreach (var entry in record.Fields)
                {
                    switch (entry)
                    {
                        case RecordFieldSyntax field:
                            CollectCommandCallSites(field.Value, text, offset, matches);
                            break;
                        case ComputedRecordFieldSyntax computed:
                            CollectCommandCallSites(computed.NameExpression, text, offset, matches);
                            CollectCommandCallSites(computed.Value, text, offset, matches);
                            break;
                        case SpreadRecordEntrySyntax spread:
                            CollectCommandCallSites(spread.Value, text, offset, matches);
                            break;
                    }
                }
                break;
            case BlockArgumentSyntax blockArgument:
                foreach (var child in blockArgument.Block.Statements)
                {
                    CollectCommandCallSites(child, text, offset, matches);
                }
                break;
            case SubexpressionArgumentSyntax subexpression:
                CollectCommandCallSites(subexpression.Pipeline, text, offset, matches);
                break;
            case OperatorArgumentSyntax operation:
                CollectCommandCallSites(operation.Left, text, offset, matches);
                CollectCommandCallSites(operation.Right, text, offset, matches);
                break;
            case MatchArgumentSyntax match:
                CollectCommandCallSites(match.Value, text, offset, matches);
                foreach (var arm in match.Arms)
                {
                    if (arm.Pattern is not null)
                    {
                        CollectCommandCallSites(arm.Pattern, text, offset, matches);
                    }
                    if (arm.Guard is not null)
                    {
                        CollectCommandCallSites(arm.Guard, text, offset, matches);
                    }
                    switch (arm.Body)
                    {
                        case MatchArmPipelineBodySyntax pipelineBody:
                            CollectCommandCallSites(pipelineBody.Pipeline, text, offset, matches);
                            break;
                        case MatchArmBlockBodySyntax blockBody:
                            foreach (var child in blockBody.Block.Statements)
                            {
                                CollectCommandCallSites(child, text, offset, matches);
                            }
                            break;
                    }
                }
                break;
            case UnaryOperatorArgumentSyntax unary:
                CollectCommandCallSites(unary.Operand, text, offset, matches);
                break;
            case RangeArgumentSyntax range:
                CollectCommandCallSites(range.Start, text, offset, matches);
                if (range.Step is not null)
                {
                    CollectCommandCallSites(range.Step, text, offset, matches);
                }
                CollectCommandCallSites(range.End, text, offset, matches);
                break;
        }
    }

    private static int GetActiveCommandParameter(CommandSyntax command, int offset)
    {
        if (command.Arguments.Count == 0)
        {
            return 0;
        }

        for (var index = 0; index < command.Arguments.Count; index++)
        {
            if (offset <= command.Arguments[index].Span.End)
            {
                return index;
            }
        }

        return Math.Max(0, command.Arguments.Count - 1);
    }

    private static void CollectCallSites(PipelineSyntax pipeline, string text, int offset, ICollection<CallSite> matches)
    {
        foreach (var stage in pipeline.Stages)
        {
            switch (stage)
            {
                case CommandSyntax command:
                    foreach (var argument in command.Arguments)
                    {
                        CollectCallSites(argument, text, offset, matches);
                    }
                    break;

                case ExpressionPipelineStageSyntax expression:
                    CollectCallSites(expression.Expression, text, offset, matches);
                    break;
            }
        }

        if (pipeline.Redirections is null)
        {
            return;
        }

        foreach (var redirection in pipeline.Redirections)
        {
            CollectCallSites(redirection.Target, text, offset, matches);
        }
    }

    private static void CollectCallSites(ArgumentSyntax argument, string text, int offset, ICollection<CallSite> matches)
    {
        if (argument.Span.Start > offset || offset > argument.Span.End)
        {
            return;
        }

        switch (argument)
        {
            case SplatArgumentSyntax splat:
                CollectCallSites(splat.Value, text, offset, matches);
                break;

            case NewObjectArgumentSyntax newObject:
                TryAddCallSite(newObject, FindOpenParenIndex(text, newObject.Span), text, offset, matches);
                foreach (var child in newObject.Arguments)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case StaticMethodCallArgumentSyntax staticMethodCall:
                TryAddCallSite(staticMethodCall, FindOpenParenIndex(text, staticMethodCall.Span), text, offset, matches);
                foreach (var child in staticMethodCall.Arguments)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case MethodCallArgumentSyntax methodCall:
                TryAddCallSite(methodCall, FindMethodCallOpenParenIndex(text, methodCall), text, offset, matches);
                CollectCallSites(methodCall.Target, text, offset, matches);
                foreach (var child in methodCall.Arguments)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case MemberAccessArgumentSyntax member:
                CollectCallSites(member.Target, text, offset, matches);
                break;

            case ArrayLiteralArgumentSyntax list:
                foreach (var item in list.Items)
                {
                    CollectCallSites(item, text, offset, matches);
                }
                break;

            case RecordLiteralArgumentSyntax record:
                foreach (var entry in record.Fields)
                {
                    if (entry is RecordFieldSyntax field)
                    {
                        CollectCallSites(field.Value, text, offset, matches);
                    }
                    else if (entry is ComputedRecordFieldSyntax computed)
                    {
                        CollectCallSites(computed.NameExpression, text, offset, matches);
                        CollectCallSites(computed.Value, text, offset, matches);
                    }
                    else if (entry is SpreadRecordEntrySyntax spread)
                    {
                        CollectCallSites(spread.Value, text, offset, matches);
                    }
                }
                break;

            case BlockArgumentSyntax blockArgument:
                foreach (var child in blockArgument.Block.Statements)
                {
                    CollectCallSites(child, text, offset, matches);
                }
                break;

            case SubexpressionArgumentSyntax subexpression:
                CollectCallSites(subexpression.Pipeline, text, offset, matches);
                break;

            case OperatorArgumentSyntax operation:
                CollectCallSites(operation.Left, text, offset, matches);
                CollectCallSites(operation.Right, text, offset, matches);
                break;

            case MatchArgumentSyntax match:
                CollectCallSites(match.Value, text, offset, matches);
                foreach (var arm in match.Arms)
                {
                    if (arm.Pattern is not null)
                    {
                        CollectCallSites(arm.Pattern, text, offset, matches);
                    }

                    if (arm.Guard is not null)
                    {
                        CollectCallSites(arm.Guard, text, offset, matches);
                    }

                    switch (arm.Body)
                    {
                        case MatchArmPipelineBodySyntax pipelineBody:
                            CollectCallSites(pipelineBody.Pipeline, text, offset, matches);
                            break;
                        case MatchArmBlockBodySyntax blockBody:
                            foreach (var child in blockBody.Block.Statements)
                            {
                                CollectCallSites(child, text, offset, matches);
                            }
                            break;
                    }
                }
                break;

            case UnaryOperatorArgumentSyntax unary:
                CollectCallSites(unary.Operand, text, offset, matches);
                break;

            case RangeArgumentSyntax range:
                CollectCallSites(range.Start, text, offset, matches);
                if (range.Step is not null)
                {
                    CollectCallSites(range.Step, text, offset, matches);
                }
                CollectCallSites(range.End, text, offset, matches);
                break;
        }
    }

    private static void TryAddCallSite(ArgumentSyntax argument, int openParenIndex, string text, int offset, ICollection<CallSite> matches)
    {
        if (openParenIndex < 0 || offset < openParenIndex || offset > argument.Span.End)
        {
            return;
        }

        matches.Add(new CallSite(argument, openParenIndex, GetActiveParameter(text, openParenIndex, offset)));
    }

    private static int FindOpenParenIndex(string text, TextSpan span)
    {
        var start = Math.Clamp(span.Start, 0, text.Length);
        var length = Math.Clamp(span.End - start, 0, text.Length - start);
        return text.IndexOf('(', start, length);
    }

    private static int FindMethodCallOpenParenIndex(string text, MethodCallArgumentSyntax methodCall)
    {
        var searchStart = Math.Clamp(methodCall.Target.Span.End, 0, text.Length);
        var searchEnd = Math.Clamp(methodCall.Span.End, searchStart, text.Length);
        var segment = text[searchStart..searchEnd];
        var methodIndex = segment.LastIndexOf(methodCall.MethodName, StringComparison.Ordinal);

        while (methodIndex >= 0)
        {
            var openParenIndex = searchStart + methodIndex + methodCall.MethodName.Length;
            var probe = openParenIndex;

            while (probe < text.Length && char.IsWhiteSpace(text[probe]))
            {
                probe++;
            }

            if (probe < searchEnd && text[probe] == '(')
            {
                return probe;
            }

            methodIndex = segment.LastIndexOf(methodCall.MethodName, methodIndex - 1, StringComparison.Ordinal);
        }

        return -1;
    }

    private static int GetActiveParameter(string text, int openParenIndex, int offset)
    {
        var activeParameter = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inString = false;
        var escapeNext = false;

        for (var index = openParenIndex + 1; index < Math.Min(offset, text.Length); index++)
        {
            var ch = text[index];

            if (inString)
            {
                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escapeNext = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                    {
                        parenDepth--;
                    }
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                    {
                        bracketDepth--;
                    }
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                    }
                    break;
                case ',' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    activeParameter++;
                    break;
            }
        }

        return activeParameter;
    }

    private sealed record CallSite(ArgumentSyntax Argument, int OpenParenIndex, int ActiveParameter);

    private sealed record CommandCallSite(CommandSyntax Command, int ActiveParameter);
}
