using System.Reflection;
using Tosh.Runtime;
using Tosh.Runtime.Units;
using Tosh.Language.Parsing;

namespace Tosh.LanguageServices;

public sealed partial class ToshLanguageFeatures
{
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

        // `TOAST-0091`. Inside `new T {| … |}` the names that belong are the type's own settable
        // members. Checked before the qualified context: a field name is a bare identifier, so
        // nothing below would recognise it, and the caller fell through to the global list.
        if (TryGetTypedLiteralContext(text, offset, out var literalType, out var literalPartial))
        {
            AddItems(items, GetInitializableMemberCompletions(index, offset, literalType, literalPartial));

            // The type says what may be set, so this is the answer whether or not it is empty.
            // Falling through would offer every command and keyword in a position where only a
            // field name can go.
            if (index.GetInitializableMembers(offset, literalType).Count > 0)
            {
                return items.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        if (TryGetQualifiedCompletionContext(
                text, offset, out var qualifiedTarget, out var qualifiedPartial, out var viaPathOperator))
        {
            // `TOAST-0090`. Reached by either operator: `Type.Member` keeps working, so it keeps
            // completing, and `Type::Member` is the spelling this is really for.
            AddItems(items, GetDeclaredTypeMemberCompletions(index, offset, qualifiedTarget, qualifiedPartial));

            // A path reaches *inside* a type. A value's instance members are not in there, and
            // `$value::Member` is not a thing to write, so the value-shaped sources are skipped
            // rather than offered and rejected later.
            var shellTargetClass = viaPathOperator
                ? null
                : semantics.ResolveShellTargetClass(offset, qualifiedTarget);

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
                        staticOnly: viaPathOperator ||
                            (!qualifiedTarget.StartsWith('$') && !string.Equals(qualifiedTarget, "_", StringComparison.Ordinal)),
                        partial: qualifiedPartial));
                }

                var expandedTarget = ExpandAliasPath(qualifiedTarget, semantics.GetVisibleAliases(offset));

                if (clrCatalog.NamespaceExists(expandedTarget))
                {
                    AddItems(items, clrCatalog.GetNamespaceAndTypeCompletions(expandedTarget, qualifiedPartial));
                }
            }

            // `::` says a path is being written, so this is the answer whether or not it is empty.
            // Falling through would offer every command and keyword after `Unknown::`, which is
            // the shape of the bug this item fixed: a plausible-looking list with the one name
            // that belongs there missing from it.
            if (items.Count > 0 || viaPathOperator)
            {
                return items.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        if (TryGetCommandFlagCompletionContext(text, offset, out var flagPrefix, out var commandName))
        {
            if (GetMetadataLookup().TryGetValue(commandName, out var metadataEntry))
            {
                foreach (var option in metadataEntry.Options)
                {
                    var flags = option.Syntax.Split(',', StringSplitOptions.TrimEntries);
                    foreach (var flag in flags)
                    {
                        if (flag.StartsWith(flagPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            items[flag] = new LspCompletionItem(
                                flag,
                                Kind: 20,
                                Detail: $"Option ({commandName})",
                                Documentation: option.Description);
                        }
                    }
                }
            }

            if (items.Count > 0)
            {
                return items.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        if (TryGetOptionValueCompletionContext(text, offset, out var valuePrefix, out var flagForValue, out var cmdForValue))
        {
            if (GetMetadataLookup().TryGetValue(cmdForValue, out var valueMeta))
            {
                foreach (var option in valueMeta.Options)
                {
                    var flags = option.Syntax.Split(',', StringSplitOptions.TrimEntries);
                    foreach (var flag in flags)
                    {
                        var flagName = flag.Split(' ', 2)[0];
                        if (!string.Equals(flagName, flagForValue, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var choices = ParseOptionValueChoices(option.Syntax);
                        if (choices is null)
                        {
                            break;
                        }

                        foreach (var choice in choices)
                        {
                            if (choice.StartsWith(valuePrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                items[choice] = new LspCompletionItem(
                                    choice,
                                    Kind: 12,
                                    Detail: $"Value for {flagName}",
                                    Documentation: option.Description);
                            }
                        }

                        break;
                    }
                }

                if (items.Count > 0)
                {
                    return items.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }
        }

        if (TryGetPathCompletionContext(text, offset, out var pathPrefix))
        {
            var pathItems = GetPathCompletionItems(pathPrefix);
            if (pathItems.Count > 0)
            {
                return pathItems;
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

        var metadataLookup = GetMetadataLookup();
        foreach (var command in _runtime.Commands.All)
        {
            var detail = "Built-in command";
            var documentation = command.Description;
            IReadOnlyList<int>? completionTags = null;

            if (metadataLookup.TryGetValue(command.Name, out var meta))
            {
                detail = $"Built-in ({meta.Category})";
                documentation = meta.LongDescription ?? meta.Description;
                if (meta.DeprecatedVersion is not null)
                    completionTags = [1]; // CompletionItemTag.Deprecated
            }

            items[command.Name] = new LspCompletionItem(
                command.Name,
                Kind: 3,
                Detail: detail,
                Documentation: documentation,
                Tags: completionTags);
        }

        foreach (var symbol in index.GetVisibleFunctions(offset))
        {
            var overloads = index.GetVisibleFunctionOverloads(offset, symbol);
            var doc = overloads.Select(o => o.DocComment).FirstOrDefault(d => d is not null);
            var docLine = doc?.Description is { Length: > 0 } desc ? $"\n{desc}" : string.Empty;
            var deprecatedTags = doc?.IsDeprecated == true ? (IReadOnlyList<int>)[1] : null;
            items[symbol] = new LspCompletionItem(
                symbol,
                Kind: 18,
                Detail: overloads.Count == 1 ? "Function" : $"Function ({overloads.Count} overloads)",
                Documentation: string.Join("\n", overloads.Take(6).Select(FormatTopLevelFunctionSignature)) + docLine,
                Tags: deprecatedTags);
        }

        foreach (var symbol in index.GetVisibleTypeLikeSymbols(offset))
        {
            var typeDoc = index.GetDeclarationDocComment(offset, symbol);
            var typeDocumentation = typeDoc?.Description is { Length: > 0 } td ? td : "ToSh type declared in the current document.";
            var typeDeprecatedTags = typeDoc?.IsDeprecated == true ? (IReadOnlyList<int>)[1] : null;
            items[symbol] = new LspCompletionItem(
                symbol,
                Kind: 7,
                Detail: "Type declared in current document",
                Documentation: typeDocumentation,
                Tags: typeDeprecatedTags);
        }

        foreach (var symbol in index.GetVisibleModules(offset))
        {
            var modDoc = index.GetDeclarationDocComment(offset, symbol);
            var modDocumentation = modDoc?.Description is { Length: > 0 } md ? md : "ToSh module declared in the current document.";
            var modDeprecatedTags = modDoc?.IsDeprecated == true ? (IReadOnlyList<int>)[1] : null;
            items[symbol] = new LspCompletionItem(
                symbol,
                Kind: 9,
                Detail: "Module declared in current document",
                Documentation: modDocumentation,
                Tags: modDeprecatedTags);
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

    private static bool TryGetCommandFlagCompletionContext(string text, int offset, out string flagPrefix, out string commandName)
    {
        flagPrefix = string.Empty;
        commandName = string.Empty;

        // Walk back from offset to find the current token being typed
        var tokenEnd = offset;
        var tokenStart = offset;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1]))
        {
            tokenStart--;
        }

        if (tokenStart >= tokenEnd)
        {
            return false;
        }

        var currentToken = text[tokenStart..tokenEnd];
        if (!currentToken.StartsWith('-'))
        {
            return false;
        }

        flagPrefix = currentToken;

        // Walk backwards from tokenStart to find the command name (first non-whitespace word on this logical line)
        var searchPos = tokenStart;
        while (searchPos > 0 && char.IsWhiteSpace(text[searchPos - 1]) && text[searchPos - 1] != '\n')
        {
            searchPos--;
        }

        // Find words before to identify the command (first word in the pipeline stage)
        var lineStart = text.LastIndexOf('\n', Math.Max(0, searchPos - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        // Skip leading whitespace
        while (lineStart < searchPos && char.IsWhiteSpace(text[lineStart]))
        {
            lineStart++;
        }

        // Also check after a pipe character for pipeline stages
        var pipePos = text.LastIndexOf('|', Math.Max(0, searchPos - 1));
        if (pipePos >= lineStart)
        {
            lineStart = pipePos + 1;
            while (lineStart < searchPos && char.IsWhiteSpace(text[lineStart]))
            {
                lineStart++;
            }
        }

        // Read command name
        var cmdEnd = lineStart;
        while (cmdEnd < searchPos && !char.IsWhiteSpace(text[cmdEnd]))
        {
            cmdEnd++;
        }

        if (cmdEnd <= lineStart)
        {
            return false;
        }

        commandName = text[lineStart..cmdEnd];
        return !commandName.StartsWith('-') && !commandName.StartsWith('$');
    }

    private static bool TryGetOptionValueCompletionContext(string text, int offset, out string valuePrefix, out string flagName, out string commandName)
    {
        valuePrefix = string.Empty;
        flagName = string.Empty;
        commandName = string.Empty;

        // Walk back to find the current token
        var tokenEnd = offset;
        var tokenStart = offset;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1]))
        {
            tokenStart--;
        }

        var currentToken = tokenStart < tokenEnd ? text[tokenStart..tokenEnd] : string.Empty;

        // If current token starts with '-', it's a flag itself, not a value
        if (currentToken.StartsWith('-'))
        {
            return false;
        }

        valuePrefix = currentToken;

        // Find the previous token (should be a flag)
        var prevEnd = tokenStart;
        while (prevEnd > 0 && char.IsWhiteSpace(text[prevEnd - 1]) && text[prevEnd - 1] != '\n')
        {
            prevEnd--;
        }

        var prevStart = prevEnd;
        while (prevStart > 0 && !char.IsWhiteSpace(text[prevStart - 1]))
        {
            prevStart--;
        }

        if (prevStart >= prevEnd)
        {
            return false;
        }

        var previousToken = text[prevStart..prevEnd];
        if (!previousToken.StartsWith('-'))
        {
            return false;
        }

        flagName = previousToken;

        // Find the command name (first word in the pipeline stage)
        var lineStart = text.LastIndexOf('\n', Math.Max(0, prevStart - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        while (lineStart < prevStart && char.IsWhiteSpace(text[lineStart]))
        {
            lineStart++;
        }

        var pipePos = text.LastIndexOf('|', Math.Max(0, prevStart - 1));
        if (pipePos >= lineStart)
        {
            lineStart = pipePos + 1;
            while (lineStart < prevStart && char.IsWhiteSpace(text[lineStart]))
            {
                lineStart++;
            }
        }

        var cmdEnd = lineStart;
        while (cmdEnd < prevStart && !char.IsWhiteSpace(text[cmdEnd]))
        {
            cmdEnd++;
        }

        if (cmdEnd <= lineStart)
        {
            return false;
        }

        commandName = text[lineStart..cmdEnd];
        return !commandName.StartsWith('-') && !commandName.StartsWith('$');
    }

    private static IReadOnlyList<string>? ParseOptionValueChoices(string syntax)
    {
        var openAngle = syntax.IndexOf('<');
        if (openAngle < 0)
        {
            return null;
        }

        var closeAngle = syntax.IndexOf('>', openAngle + 1);
        if (closeAngle < 0)
        {
            return null;
        }

        var inner = syntax.AsSpan()[(openAngle + 1)..closeAngle];
        if (!inner.Contains('|'))
        {
            return null;
        }

        return inner.ToString().Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static readonly IReadOnlySet<string> PathFirstCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "ls", "cat", "mkdir", "touch", "rm", "cp", "mv", "find", "du", "df",
        "head", "tail", "grep", "open", "dirname", "basename", "readlink", "realpath",
        "archive", "extract", "tree", "trash", "source",
    };

    private static bool TryGetPathCompletionContext(string text, int offset, out string pathPrefix)
    {
        pathPrefix = string.Empty;

        var tokenEnd = offset;
        var tokenStart = offset;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1]))
        {
            tokenStart--;
        }

        var currentToken = tokenStart < tokenEnd ? text[tokenStart..tokenEnd] : string.Empty;

        if (currentToken.StartsWith('-') || currentToken.StartsWith('$'))
        {
            return false;
        }

        if (LooksLikePathToken(currentToken))
        {
            pathPrefix = currentToken;
            return true;
        }

        // Check if we're after a path-first command
        var lineStart = text.LastIndexOf('\n', Math.Max(0, tokenStart - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        var pipePos = text.LastIndexOf('|', Math.Max(0, tokenStart - 1));
        if (pipePos >= lineStart)
        {
            lineStart = pipePos + 1;
        }

        while (lineStart < tokenStart && char.IsWhiteSpace(text[lineStart]))
        {
            lineStart++;
        }

        var cmdEnd = lineStart;
        while (cmdEnd < tokenStart && !char.IsWhiteSpace(text[cmdEnd]))
        {
            cmdEnd++;
        }

        if (cmdEnd <= lineStart)
        {
            return false;
        }

        var cmdName = text[lineStart..cmdEnd];
        if (PathFirstCommands.Contains(cmdName))
        {
            pathPrefix = currentToken;
            return true;
        }

        return false;
    }

    private static bool LooksLikePathToken(string token)
    {
        return token.StartsWith("./", StringComparison.Ordinal) ||
               token.StartsWith(".\\", StringComparison.Ordinal) ||
               token.StartsWith("../", StringComparison.Ordinal) ||
               token.StartsWith("..\\", StringComparison.Ordinal) ||
               token.StartsWith("~/", StringComparison.Ordinal) ||
               token.StartsWith("~\\", StringComparison.Ordinal) ||
               token.StartsWith("/", StringComparison.Ordinal) ||
               token.StartsWith("\\", StringComparison.Ordinal) ||
               token.Contains(Path.DirectorySeparatorChar) ||
               token.Contains(Path.AltDirectorySeparatorChar);
    }

    private IReadOnlyList<LspCompletionItem> GetPathCompletionItems(string tokenPrefix)
    {
        var searchBase = _runtime.CurrentDirectory;
        var namePrefix = tokenPrefix;

        if (!string.IsNullOrEmpty(tokenPrefix))
        {
            var separatorIndex = tokenPrefix.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);

            if (separatorIndex >= 0)
            {
                var directoryPart = tokenPrefix[..(separatorIndex + 1)];
                namePrefix = tokenPrefix[(separatorIndex + 1)..];
                searchBase = PathUtilities.ResolvePath(_runtime.CurrentDirectory, directoryPart);
            }
        }

        if (!Directory.Exists(searchBase))
        {
            return [];
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(searchBase);
        }
        catch
        {
            return [];
        }

        var items = new List<LspCompletionItem>();

        foreach (var entryPath in entries)
        {
            var name = Path.GetFileName(entryPath);

            if (string.IsNullOrEmpty(name) || name.StartsWith('.') && !namePrefix.StartsWith('.'))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(namePrefix) && !name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isDirectory = Directory.Exists(entryPath);
            items.Add(new LspCompletionItem(
                name,
                Kind: isDirectory ? 19 : 17,
                Detail: isDirectory ? "Directory" : "File"));
        }

        return items.OrderBy(item => item.Kind).ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase).ToArray();
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

    private static bool TryGetQualifiedCompletionContext(
        string text,
        int offset,
        out string target,
        out string partial,
        out bool viaPathOperator)
    {
        target = string.Empty;
        partial = string.Empty;
        viaPathOperator = false;

        if (offset <= 0)
        {
            return false;
        }

        var start = offset;

        while (start > 0 && IsCompletionPathChar(text[start - 1]))
        {
            start--;
        }

        // `TOAST-0090`. `::` separates too. It is not folded into `IsCompletionPathChar`: a bare
        // ':' also ends a type annotation and a ternary, so `var a:Level.` would scan back through
        // it and complete against `a:Level`. Matching the pair explicitly keeps the scan honest.
        if (start >= 2 && text[start - 1] == ':' && text[start - 2] == ':')
        {
            var typeStart = start - 2;

            while (typeStart > 0 && IsCompletionPathChar(text[typeStart - 1]))
            {
                typeStart--;
            }

            if (typeStart == start - 2)
            {
                return false;
            }

            target = text[typeStart..(start - 2)];
            partial = text[start..offset];
            viaPathOperator = true;
            return true;
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

    /// <summary>
    /// Whether the cursor sits in a field-name position of a typed record literal, and if so the
    /// type it is constructing — <c>TOAST-0091</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found by scanning forward from the start of the document rather than backward from the
    /// cursor. Backward is cheaper and wrong: a <c>"{|"</c> inside a string, or a <c>#</c>
    /// comment, would open a literal that is not there. Forward, the scanner knows which of
    /// those it is inside, and documents are small enough that the cost does not matter.
    /// </para>
    /// <para>
    /// The type is read from what precedes the opener: an identifier, optionally preceded by a
    /// parenthesised constructor call, preceded by <c>new</c>. An untyped <c>{| … |}</c> has no
    /// <c>new</c> before it and is left alone, which is what keeps ordinary record literals free
    /// of type-shaped completions.
    /// </para>
    /// </remarks>
    private static bool TryGetTypedLiteralContext(
        string text,
        int offset,
        out string typeName,
        out string partial)
    {
        typeName = string.Empty;
        partial = string.Empty;

        var open = FindEnclosingRecordLiteral(text, offset);
        if (open < 0) { return false; }

        if (!TryReadTypedLiteralHead(text, open, out typeName)) { return false; }

        // Only a field-name position. After an `=` the author is writing a value, and the type's
        // member names are not what belongs there.
        for (var index = offset - 1; index > open + 1; index--)
        {
            var ch = text[index];
            if (ch is ',' or '\n') { break; }
            if (ch == '=' && (index == 0 || text[index - 1] is not ('=' or '!' or '<' or '>'))) { return false; }
        }

        var start = offset;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
        {
            start--;
        }

        partial = text[start..offset];
        return true;
    }

    /// <summary>
    /// Index of the <c>{</c> of the innermost <c>{|</c> the cursor is inside, or -1.
    /// </summary>
    private static int FindEnclosingRecordLiteral(string text, int offset)
    {
        var openings = new Stack<int>();
        var limit = Math.Clamp(offset, 0, text.Length);

        for (var index = 0; index < limit; index++)
        {
            var ch = text[index];

            if (ch == '#')
            {
                while (index < limit && text[index] != '\n') { index++; }
                continue;
            }

            if (ch is '"' or '\'')
            {
                index = SkipQuotedSpan(text, index, ch, limit);
                continue;
            }

            if (ch == '{' && index + 1 < limit && text[index + 1] == '|')
            {
                openings.Push(index);
                index++;
                continue;
            }

            if (ch == '|' && index + 1 < limit && text[index + 1] == '}')
            {
                if (openings.Count > 0) { openings.Pop(); }
                index++;
            }
        }

        return openings.Count > 0 ? openings.Peek() : -1;
    }

    private static int SkipQuotedSpan(string text, int start, char quote, int limit)
    {
        for (var index = start + 1; index < limit; index++)
        {
            if (text[index] == '\\') { index++; continue; }
            if (text[index] == quote) { return index; }
        }

        return limit;
    }

    /// <summary>
    /// Reads <c>new T</c> or <c>new T(args)</c> immediately before a literal's <c>{|</c>.
    /// </summary>
    private static bool TryReadTypedLiteralHead(string text, int open, out string typeName)
    {
        typeName = string.Empty;
        var cursor = open - 1;

        while (cursor >= 0 && char.IsWhiteSpace(text[cursor])) { cursor--; }

        // `new T(args) {| … |}` is accepted too, so a constructor call is skipped as a unit.
        if (cursor >= 0 && text[cursor] == ')')
        {
            var depth = 0;

            while (cursor >= 0)
            {
                if (text[cursor] == ')') { depth++; }
                else if (text[cursor] == '(')
                {
                    depth--;
                    if (depth == 0) { cursor--; break; }
                }

                cursor--;
            }

            if (depth != 0) { return false; }

            while (cursor >= 0 && char.IsWhiteSpace(text[cursor])) { cursor--; }
        }

        var nameEnd = cursor + 1;

        while (cursor >= 0 && (char.IsLetterOrDigit(text[cursor]) || text[cursor] is '_' or '.'))
        {
            cursor--;
        }

        var nameStart = cursor + 1;
        if (nameStart >= nameEnd) { return false; }

        while (cursor >= 0 && char.IsWhiteSpace(text[cursor])) { cursor--; }

        // Without `new` this is an untyped record literal, which has no type to complete against.
        const string New = "new";
        if (cursor - New.Length + 1 < 0) { return false; }
        if (text.AsSpan(cursor - New.Length + 1, New.Length) is not "new") { return false; }

        var before = cursor - New.Length;
        if (before >= 0 && (char.IsLetterOrDigit(text[before]) || text[before] is '_' or '$'))
        {
            return false;
        }

        typeName = text[nameStart..nameEnd];
        return true;
    }

    /// <summary>
    /// An enum's members and a union's variants, which is what a path into a declared type
    /// reaches — <c>TOAST-0090</c>. Neither the shell-class resolver nor the CLR catalog answers
    /// for these, so before this they completed to nothing and the caller fell through to the
    /// global list.
    /// </summary>
    /// <summary>
    /// A class's properties and a record's fields, which is what a typed literal sets —
    /// <c>TOAST-0091</c>.
    /// </summary>
    private static IReadOnlyList<LspCompletionItem> GetInitializableMemberCompletions(
        DeclarationIndex index,
        int offset,
        string typeName,
        string partial)
    {
        var members = index.GetInitializableMembers(offset, typeName);

        if (members.Count == 0)
        {
            return Array.Empty<LspCompletionItem>();
        }

        var items = new List<LspCompletionItem>(members.Count);

        foreach (var member in members)
        {
            if (!MatchesPrefix(member.Name, partial))
            {
                continue;
            }

            items.Add(new LspCompletionItem(
                member.Name,
                Kind: member.KindLabel == "Property" ? 10 : 5, // Property / Field
                Detail: $"{member.KindLabel} of {member.DeclaringType}",
                Documentation: member.DocComment?.Summary,
                InsertText: member.Name + " = "));
        }

        return items;
    }

    private static IReadOnlyList<LspCompletionItem> GetDeclaredTypeMemberCompletions(
        DeclarationIndex index,
        int offset,
        string target,
        string partial)
    {
        var members = index.GetTypeMembers(offset, target);

        if (members.Count == 0)
        {
            return Array.Empty<LspCompletionItem>();
        }

        var items = new List<LspCompletionItem>(members.Count);

        foreach (var member in members)
        {
            if (!MatchesPrefix(member.Name, partial))
            {
                continue;
            }

            items.Add(new LspCompletionItem(
                member.Name,
                Kind: 20, // EnumMember
                Detail: $"{member.KindLabel} of {member.DeclaringType}",
                Documentation: member.DocComment?.Summary));
        }

        return items;
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

    private static bool MatchesPrefix(string text, string prefix)
    {
        return string.IsNullOrEmpty(prefix) ||
               text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompletionPathChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch is '$' or '_' or '.';
    }


}
