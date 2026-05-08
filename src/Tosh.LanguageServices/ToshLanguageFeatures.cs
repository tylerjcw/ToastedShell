using System.Reflection;
using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.LanguageServices;

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
        ["const"] = "Declare an immutable constant. Like `var` but the binding cannot be reassigned.",
        ["alloc"] = "Allocate a native buffer into a variable by byte count or interop type size.",
        ["class"] = "Define a ToSh class with properties, constructors, and methods.",
        ["interface"] = "Define a structural interface with required method signatures. Classes conform via `fulfills`/`implements`. Example: `interface IShape { func area() }`.",
        ["union"] = "Define a discriminated union (sum type) with named variants. Example: `union Result { Ok(value); Error(msg) }`.",
        ["module"] = "Define a named ToSh module object with its own lexical scope.",
        ["enum"] = "Define a named enum with symbolic members and optional underlying numeric storage.",
        ["record"] = "Define a named data shape with positional construction and structural equality.",
        ["rune"] = "Define a computed value type — a named, parameterized computation that caches its result. Sealed by default; use `leaky` to allow subclassing.",
        ["event"] = "Define a named event on a class with optional payload fields. Example: `event DataReceived { data }`.",
        ["required"] = "Modifier on `event` definitions — callers must handle the event (it cannot be silently ignored).",
        ["prop"] = "Declare a class property, computed member, or accessor-backed property.",
        ["shy"] = "Keep a declaration in the current lexical scope, or hide a class member from public inspection and external access.",
        ["static"] = "Mark a class member as belonging to the class rather than an instance.",
        ["shared"] = "Mark a class member as belonging to the class rather than an instance (alias for static).",
        ["sealed"] = "Prevent a class from being inherited.",
        ["hollow"] = "Mark a class or method as abstract, requiring subclass implementation.",
        ["fixed"] = "Mark a class property as read-only after initialization.",
        ["vital"] = "Mark a class property as required during construction.",
        ["guarded"] = "Restrict member access to the defining class and its subclasses (protected).",
        ["overrule"] = "Override an inherited method from a parent class.",
        ["hermit"] = "Mark a class as static-only; all members are auto-promoted to shared.",
        ["strict"] = "Make all properties in a class read-only (immutable).",
        ["lazy"] = "Defer property initialization until first access.",
        ["fading"] = "Mark a member as deprecated; emits a warning on use.",
        ["local"] = "Restrict member visibility to the defining assembly (internal).",
        ["raw"] = "Mark a method for unsafe/native interop.",
        ["partial"] = "Allow a class definition to be split across multiple declarations.",
        ["proud"] = "Explicitly mark a member as public.",
        ["public"] = "Explicitly mark a member as public (no-op, members are public by default).",
        ["fluid"] = "Mark a struct as mutable, allowing field reassignment after construction.",
        ["leaky"] = "Modifier on `rune` definitions — allows the rune to be subclassed (non-sealed).",
        ["struct"] = "Define a value-type with positional fields, structural equality, and copy-on-assign semantics.",
        ["trait"] = "Define a trait with required and default method/property signatures that classes can adopt via 'uses'.",
        ["fulfills"] = "Declare that a class conforms to one or more interfaces.",
        ["implements"] = "Declare interface conformance — alias for `fulfills`. Example: `class Circle implements IShape { ... }`.",
        ["extends"] = "Specify a base class for inheritance. Example: `class Dog extends Animal { ... }`.",
        ["uses"] = "Declare that a class adopts one or more traits.",
        ["handles"] = "Attach a method as an event handler. Example: `func onData handles DataReceived { ... }`. Combine with `when`, `priority`, and `once`.",
        ["when"] = "Guard predicate in a `handles` clause or `match` arm. Example: `func f handles E when { $E.value > 0 } { ... }`.",
        ["priority"] = "Numeric execution order for a `handles` clause. Lower numbers run first. Example: `func f handles E priority 10 { ... }`.",
        ["once"] = "Mark a `handles` clause to run the handler only once, then auto-deregister. Example: `func f handles E once { ... }`.",
        ["global"] = "Publish a declaration to the session-wide scope.",
        ["export"] = "Publish a module declaration or export an environment value.",
        ["using"] = "Import CLR namespaces or type aliases in the current lexical scope.",
        ["require"] = "Load a `.tosh` module, `.dll`, `.csproj`, or native shared library once per session and import it lexically.",
        ["native"] = "In `require native`, load a native shared library for binding and invocation.",
        ["bind"] = "Attach native exports to a required native module as callable ToSh functions.",
        ["from"] = "Select a source path in selective `require` forms.",
        ["as"] = "Type-cast operator or import alias keyword. As an infix operator, casts a value to the named type (e.g. `5 as float`). In `using` / `require` / `bind`, renames an imported symbol.",
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
        ["yield"] = "Emit a value from a generator function. Each `yield` produces one pipeline item.",
        ["defer"] = "Execute a block when the current scope exits. Multiple defers run in reverse declaration order (like Go's defer). Example: `defer { close $file }`.",
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
        ["not-in"] = "Negated membership operator. Returns `true` if the value is not found in the collection. Example: `4 not-in [1,2,3]`.",
        ["is"] = "Infix type-check operator. Returns `true` if the value matches the named type. Example: `5 is int`. Use `is not` or `is-not` as the negated form.",
        ["is-not"] = "Negated type-check operator. Returns `true` if the value does not match the named type. Example: `5 is-not string`. The two-word form `is not` is equivalent.",
        ["is-in"] = "Membership form of `is`. Written as `is in` (two words): `3 is in [1,2,3]` → `true`. The normalized form `is-in` is produced internally by the parser.",
        ["is-not-in"] = "Negated membership form of `is not`. Written as `is not in` (three words): `4 is not in [1,2,3]` → `true`. The normalized form `is-not-in` is produced internally by the parser.",
        ["contains"] = "Substring/membership operator. Returns `true` if a string contains the substring, or if a collection contains the value. Example: `\"hello\" contains \"ell\"`.",
        ["starts-with"] = "String prefix operator. Returns `true` if the string starts with the given prefix. Example: `\"hello\" starts-with \"he\"`.",
        ["ends-with"] = "String suffix operator. Returns `true` if the string ends with the given suffix. Example: `\"hello\" ends-with \"lo\"`.",
        ["where"] = "Filter clause in a list comprehension or query expression. Example: `[for x in $items where { $x > 5 }]`.",
        ["let"] = "Bind a variable in a comprehension expression. Example: `[for x in $items let y = x * 2 pick y]`.",
        ["pick"] = "Projection clause in a comprehension — selects/transforms the output value. Example: `[for x in $items pick x * 2]`. Alias for `get`.",
        ["get"] = "Projection clause in a comprehension (alias for `pick`). Also used as a property accessor keyword inside class `prop` bodies.",
        ["quote"] = "Capture a code block as a first-class value without evaluating it. Example: `var block = quote { ls | sort }`.",
        ["new"] = "Construct a CLR object or ToSh named type instance.",
        ["nameof"] = "Return the name of a variable or member path.",
        ["name-of"] = "Command-style alias for `nameof`.",
    };

    private readonly ToshRuntime _runtime;
    private IReadOnlyDictionary<string, CommandMetadata>? _metadataCache;

    public ToshLanguageFeatures()
    {
        _runtime = ToshRuntime.CreateDefault();
    }

    private IReadOnlyDictionary<string, CommandMetadata> GetMetadataLookup()
    {
        return _metadataCache ??= CommandMetadataExporter.BuildMetadata(_runtime.Commands)
            .ToDictionary(entry => entry.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CommandMetadata> GetAllCommandMetadata()
    {
        return CommandMetadataExporter.BuildMetadata(_runtime.Commands);
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

            if (GetMetadataLookup().TryGetValue(commandCallSite.Command.Name, out var metadataEntry) &&
                metadataEntry.Arguments.Count > 0)
            {
                return CreateBuiltInCommandSignatureHelp(metadataEntry, commandCallSite.ActiveParameter);
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

    public IReadOnlyList<LspLocation> GetReferences(string text, string sourceName, LspPosition position, bool includeDeclaration)
    {
        return DeclarationIndex.Create(sourceName, text).FindReferences(position, includeDeclaration);
    }

    public LspPrepareRenameResult? PrepareRename(string text, string sourceName, LspPosition position)
    {
        return DeclarationIndex.Create(sourceName, text).PrepareRename(position);
    }

    public LspWorkspaceEdit? Rename(string text, string sourceName, LspPosition position, string newName)
    {
        return DeclarationIndex.Create(sourceName, text).BuildRenameEdits(position, newName);
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
        else if (GetTypeLikeDeclarationHoverDescription(index, offset, normalizedWord) is { } typeDescription)
        {
            description = typeDescription;
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
            if (topic.Kind == HelpSubjectKind.BuiltIn && GetMetadataLookup().TryGetValue(normalizedWord, out var metadataEntry))
            {
                description = FormatCommandHoverMarkdown(metadataEntry);
            }
            else
            {
                description = topic.Description;
            }
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

    public static readonly IReadOnlyList<string> SemanticTokenTypes =
    [
        "comment",    // 0
        "keyword",    // 1
        "string",     // 2
        "number",     // 3
        "variable",   // 4
        "function",   // 5
        "type",       // 6
        "operator",   // 7
    ];

    public static readonly IReadOnlyList<string> SemanticTokenModifiers =
    [
        "declaration",     // bit 0
        "defaultLibrary",  // bit 1
        "documentation",   // bit 2
    ];

    public LspSemanticTokens GetSemanticTokens(string text, string sourceName)
    {
        var map = new TextCoordinateMap(text);
        var rawTokens = new List<(int Line, int Start, int Length, int Type, int Modifiers)>();

        // 1. Scan for comments (lexer skips them)
        var lines = text.Split('\n');
        for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx];
            var inString = false;
            var stringChar = '\0';
            for (var ci = 0; ci < line.Length; ci++)
            {
                var ch = line[ci];
                if (inString)
                {
                    if (ch == '\\') { ci++; continue; }
                    if (ch == stringChar) inString = false;
                    continue;
                }
                if (ch is '"' or '\'') { inString = true; stringChar = ch; continue; }
                if (ch == '#')
                {
                    var isDocComment = ci + 1 < line.Length && line[ci + 1] == '#';
                    var modifiers = isDocComment ? 0x04 : 0; // documentation modifier for ##
                    rawTokens.Add((lineIdx, ci, line.Length - ci, 0, modifiers)); // comment
                    break;
                }
            }
        }

        // 2. Lex the source to get tokens
        var lexer = new ToshLexer(text);
        IReadOnlyList<SyntaxToken> tokens;
        try { tokens = lexer.Lex(); }
        catch { return new LspSemanticTokens([]); }

        var index = DeclarationIndex.Create(sourceName, text);
        var builtinNames = new HashSet<string>(
            _runtime.Commands.All.Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            if (token.Kind == SyntaxTokenKind.EndOfFile) continue;

            var pos = map.ToPosition(token.Position);

            switch (token.Kind)
            {
                case SyntaxTokenKind.String or SyntaxTokenKind.InterpolatedString:
                    rawTokens.Add((pos.Line, pos.Character, token.Text.Length, 2, 0));
                    break;

                case SyntaxTokenKind.Number or SyntaxTokenKind.UnitLiteral:
                    rawTokens.Add((pos.Line, pos.Character, token.Text.Length, 3, 0));
                    break;

                case SyntaxTokenKind.Boolean or SyntaxTokenKind.Null:
                    rawTokens.Add((pos.Line, pos.Character, token.Text.Length, 1, 0x02)); // keyword + defaultLibrary
                    break;

                case SyntaxTokenKind.Bareword:
                    ClassifyBareword(token, pos, rawTokens, builtinNames, index);
                    break;

                case SyntaxTokenKind.Pipe
                    or SyntaxTokenKind.DoublePipe or SyntaxTokenKind.DoubleAmpersand
                    or SyntaxTokenKind.Ampersand
                    or SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanGreaterThan
                    or SyntaxTokenKind.LessThan or SyntaxTokenKind.LessThanLessThanLessThan
                    or SyntaxTokenKind.GreaterThanEqual or SyntaxTokenKind.LessThanEqual
                    or SyntaxTokenKind.BangEqual or SyntaxTokenKind.BangTilde
                    or SyntaxTokenKind.Bang
                    or SyntaxTokenKind.QuestionQuestion or SyntaxTokenKind.QuestionDot
                    or SyntaxTokenKind.DotDot:
                    rawTokens.Add((pos.Line, pos.Character, token.Text.Length, 7, 0)); // operator
                    break;
            }
        }

        // 3. Encode as delta-encoded LSP data
        rawTokens.Sort((a, b) =>
        {
            var lineCmp = a.Line.CompareTo(b.Line);
            return lineCmp != 0 ? lineCmp : a.Start.CompareTo(b.Start);
        });

        var data = new List<int>(rawTokens.Count * 5);
        var prevLine = 0;
        var prevStart = 0;
        foreach (var (line, start, length, type, modifiers) in rawTokens)
        {
            var deltaLine = line - prevLine;
            var deltaStart = deltaLine == 0 ? start - prevStart : start;
            data.Add(deltaLine);
            data.Add(deltaStart);
            data.Add(length);
            data.Add(type);
            data.Add(modifiers);
            prevLine = line;
            prevStart = start;
        }

        return new LspSemanticTokens(data);
    }

    private void ClassifyBareword(
        SyntaxToken token,
        LspPosition pos,
        List<(int Line, int Start, int Length, int Type, int Modifiers)> rawTokens,
        HashSet<string> builtinNames,
        DeclarationIndex index)
    {
        var word = token.Text;

        // Variable reference ($name)
        if (word.StartsWith('$'))
        {
            rawTokens.Add((pos.Line, pos.Character, word.Length, 4, 0)); // variable
            return;
        }

        // Language keywords
        if (Keywords.ContainsKey(word))
        {
            rawTokens.Add((pos.Line, pos.Character, word.Length, 1, 0)); // keyword
            return;
        }

        // Built-in commands
        if (builtinNames.Contains(word))
        {
            rawTokens.Add((pos.Line, pos.Character, word.Length, 5, 0x02)); // function + defaultLibrary
            return;
        }

        // User-defined function names
        var offset = token.Position;
        var visibleFunctions = index.GetVisibleFunctions(offset);
        if (visibleFunctions.Contains(word))
        {
            rawTokens.Add((pos.Line, pos.Character, word.Length, 5, 0)); // function
            return;
        }

        // User-defined type names
        var visibleTypes = index.GetVisibleTypeLikeSymbols(offset);
        if (visibleTypes.Contains(word))
        {
            rawTokens.Add((pos.Line, pos.Character, word.Length, 6, 0)); // type
            return;
        }
    }

    private static string FormatCommandHoverMarkdown(CommandMetadata entry)
    {
        var sb = new System.Text.StringBuilder();

        // Badges: experimental, deprecated, version
        var badges = new List<string>();
        if (entry.IsExperimental) badges.Add("⚗️ Experimental");
        if (entry.DeprecatedVersion is not null) badges.Add($"⚠️ Deprecated since {entry.DeprecatedVersion}");
        if (entry.RemovedVersion is not null) badges.Add($"❌ Removed in {entry.RemovedVersion}");
        if (badges.Count > 0)
        {
            sb.AppendLine(string.Join(" · ", badges));
            sb.AppendLine();
        }

        sb.AppendLine(entry.Description);
        sb.AppendLine();

        if (entry.LongDescription is not null)
        {
            sb.AppendLine(entry.LongDescription);
            sb.AppendLine();
        }

        if (entry.Aliases.Count > 0)
        {
            sb.AppendLine($"*Aliases:* {string.Join(", ", entry.Aliases.Select(a => $"`{a}`"))}");
            sb.AppendLine();
        }

        // Category and version info
        var infoLine = new List<string>();
        infoLine.Add($"Category: {entry.Category}");
        if (entry.SinceVersion is not null) infoLine.Add($"Since: {entry.SinceVersion}");
        sb.AppendLine($"*{string.Join(" · ", infoLine)}*");
        sb.AppendLine();

        sb.AppendLine("```tosh");
        sb.AppendLine(entry.Usage);
        sb.AppendLine("```");

        if (entry.Arguments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Arguments**");
            foreach (var arg in entry.Arguments)
            {
                var req = arg.Required ? "" : " *(optional)*";
                var typePart = arg.TypeName is not null ? $" `{arg.TypeName}`" : "";
                sb.AppendLine($"- `{arg.Name}`{typePart} — {arg.Description}{req}");
            }
        }

        if (entry.Options.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Options**");
            foreach (var opt in entry.Options)
            {
                sb.AppendLine($"- `{opt.Syntax}` — {opt.Description}");
            }
        }

        if (entry.PipelineInput is { } pi)
        {
            var accepts = new List<string>();
            if (pi.AcceptsScalar) accepts.Add("scalar");
            if (pi.AcceptsRecord) accepts.Add("record");
            if (pi.AcceptsList) accepts.Add("list");
            if (pi.AcceptsTable) accepts.Add("table");
            if (accepts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"**Pipeline input:** {string.Join(", ", accepts)}");
                if (pi.Description is not null)
                    sb.AppendLine($"  {pi.Description}");
            }
        }

        if (entry.Output is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"**Output:** {entry.Output}");
        }

        if (entry.Examples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Examples**");
            sb.AppendLine("```tosh");
            foreach (var ex in entry.Examples)
            {
                var comment = ex.Title is not null ? $"  # {ex.Title}" : "";
                sb.AppendLine($"{ex.Code}{comment}");
            }
            sb.AppendLine("```");
        }

        if (entry.CanonicalExamples.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Canonical Examples**");
            foreach (var ce in entry.CanonicalExamples)
            {
                if (ce.Description is not null)
                    sb.AppendLine($"*{ce.Description}*");
                sb.AppendLine("```tosh");
                sb.AppendLine($"> {ce.Input}");
                sb.AppendLine(ce.Output);
                sb.AppendLine("```");
            }
        }

        if (entry.Notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var note in entry.Notes)
            {
                sb.AppendLine($"> {note}");
            }
        }

        if (entry.ErrorConditions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Error Conditions**");
            foreach (var err in entry.ErrorConditions)
            {
                sb.AppendLine($"- {err}");
            }
        }

        if (entry.Permissions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Permissions:** {string.Join(", ", entry.Permissions)}");
        }

        if (entry.Tags.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"*Tags:* {string.Join(", ", entry.Tags.Select(t => $"`{t}`"))}");
        }

        if (entry.SeeAlso.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"*See also:* {string.Join(", ", entry.SeeAlso.Select(s => $"`{s}`"))}");
        }

        return sb.ToString().TrimEnd();
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

    private static LspSignatureHelp CreateBuiltInCommandSignatureHelp(CommandMetadata entry, int activeParameter)
    {
        var parameterLabels = entry.Arguments
            .Select(arg =>
            {
                var typePart = arg.TypeName is not null ? $": {arg.TypeName}" : "";
                var optPart = arg.Required ? "" : "?";
                return $"{arg.Name}{optPart}{typePart}";
            })
            .ToArray();

        var labelBuilder = new System.Text.StringBuilder(entry.Name);
        foreach (var paramLabel in parameterLabels)
        {
            labelBuilder.Append($" <{paramLabel}>");
        }

        foreach (var option in entry.Options)
        {
            var primaryFlag = option.Syntax.Split(',', StringSplitOptions.TrimEntries)[0];
            labelBuilder.Append($" [{primaryFlag}]");
        }

        var signatureLabel = labelBuilder.ToString();

        var parameters = entry.Arguments
            .Select(arg => new LspParameterInformation(
                Label: arg.TypeName is not null
                    ? $"{arg.Name}{(arg.Required ? "" : "?")}: {arg.TypeName}"
                    : $"{arg.Name}{(arg.Required ? "" : "?")}",
                Documentation: arg.Description))
            .ToArray();

        var signature = new LspSignatureInformation(
            signatureLabel,
            Documentation: entry.Description,
            Parameters: parameters);

        var boundedActive = entry.Arguments.Count == 0
            ? 0
            : Math.Min(activeParameter, entry.Arguments.Count - 1);

        return new LspSignatureHelp([signature], ActiveSignature: 0, ActiveParameter: boundedActive);
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

        var doc = overloads.Select(o => o.DocComment).FirstOrDefault(d => d is not null);
        var parts = new List<string> { label };

        AppendDeprecatedBanner(parts, doc);
        AppendSummary(parts, doc);

        parts.Add($"```tosh\n{signatures}{overflow}\n```");

        if (doc is not null)
        {
            // Use the first overload as the canonical source of parameter
            // names and types when rendering @param/@returns docs.
            var canonical = overloads[0];
            AppendDocCommentSections(parts, doc, canonical.Parameters, canonical.ReturnTypeName);
        }

        return string.Join("\n\n", parts);
    }

    private static string? GetTypeLikeDeclarationHoverDescription(DeclarationIndex index, int offset, string token)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            token.StartsWith("$", StringComparison.Ordinal) ||
            token.Contains('.', StringComparison.Ordinal))
        {
            return null;
        }

        var kindLabel = index.GetDeclarationKindLabel(offset, token);
        if (kindLabel is null)
        {
            return null;
        }

        var parts = new List<string> { kindLabel };

        var doc = index.GetDeclarationDocComment(offset, token);
        AppendDeprecatedBanner(parts, doc);
        AppendSummary(parts, doc);

        if (doc is not null)
        {
            AppendDocCommentSections(parts, doc, parameters: null, returnTypeName: null);
        }

        return string.Join("\n\n", parts);
    }

    private static void AppendDeprecatedBanner(List<string> parts, DocComment? doc)
    {
        if (doc?.IsDeprecated != true)
        {
            return;
        }

        parts.Add(doc.Deprecated is { Length: > 0 } depMsg
            ? $"⚠️ **Deprecated** — {depMsg}"
            : "⚠️ **Deprecated**");
    }

    private static void AppendSummary(List<string> parts, DocComment? doc)
    {
        if (doc?.Description is { Length: > 0 } desc)
        {
            parts.Add(desc);
        }
    }

    /// <summary>
    /// Appends the non-summary doc-comment sections (remarks, type
    /// parameters, parameters, returns, value, exceptions, examples,
    /// see-also, since) to <paramref name="parts"/> in a consistent
    /// Markdown layout. Each entry in <paramref name="parts"/> ends up
    /// being joined with a blank line, so every section is its own
    /// paragraph in the rendered hover.
    /// </summary>
    private static void AppendDocCommentSections(
        List<string> parts,
        DocComment doc,
        IReadOnlyList<FunctionParameterSyntax>? parameters,
        string? returnTypeName)
    {
        if (doc.Remarks is { Length: > 0 } remarks)
        {
            parts.Add($"**Remarks**\n\n{remarks}");
        }

        if (doc.TypeParameters is { Count: > 0 } typeParams)
        {
            var lines = new List<string> { "**Type parameters**" };
            foreach (var (name, description) in typeParams)
            {
                lines.Add(description.Length > 0
                    ? $"- `{name}` — {description}"
                    : $"- `{name}`");
            }
            parts.Add(string.Join("\n", lines));
        }

        if (doc.Parameters is { Count: > 0 } && parameters is not null)
        {
            var paramLines = new List<string>();
            foreach (var param in parameters)
            {
                if (!doc.Parameters.TryGetValue(param.Name, out var paramDesc) || paramDesc.Length == 0)
                {
                    continue;
                }

                var typeAnnotation = param.TypeName is not null ? $" `{param.TypeName}`" : string.Empty;
                paramLines.Add($"- `{param.Name}`{typeAnnotation} — {paramDesc}");
            }

            if (paramLines.Count > 0)
            {
                parts.Add("**Parameters**\n" + string.Join("\n", paramLines));
            }
        }
        else if (doc.Parameters is { Count: > 0 } looseParams && parameters is null)
        {
            // No syntactic parameter list available (e.g. record/class
            // hover); fall back to the names captured in the doc-comment.
            var paramLines = new List<string> { "**Parameters**" };
            foreach (var (name, description) in looseParams)
            {
                paramLines.Add(description.Length > 0
                    ? $"- `{name}` — {description}"
                    : $"- `{name}`");
            }
            parts.Add(string.Join("\n", paramLines));
        }

        if (doc.Returns is { Length: > 0 } ret)
        {
            var returnType = returnTypeName is { Length: > 0 } rt ? $" `{rt}`" : string.Empty;
            parts.Add($"**Returns**{returnType} — {ret}");
        }

        if (doc.Value is { Length: > 0 } val)
        {
            parts.Add($"**Value** — {val}");
        }

        if (doc.Throws is { Count: > 0 } throws)
        {
            var throwLines = new List<string> { "**Throws**" };
            foreach (var t in throws)
            {
                throwLines.Add(t.Length > 0 ? $"- {t}" : "- _(unspecified)_");
            }
            parts.Add(string.Join("\n", throwLines));
        }

        if (doc.Examples is { Count: > 0 } examples)
        {
            var heading = examples.Count == 1 ? "**Example**" : "**Examples**";
            var blocks = new List<string> { heading };
            foreach (var example in examples)
            {
                blocks.Add($"```tosh\n{example}\n```");
            }
            parts.Add(string.Join("\n\n", blocks));
        }

        if (doc.SeeAlso is { Count: > 0 } seeAlso)
        {
            parts.Add($"**See also:** {string.Join(", ", seeAlso.Select(s => $"`{s}`"))}");
        }

        if (doc.Since is { Length: > 0 } since)
        {
            parts.Add($"_Since {since}_");
        }
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
            DocumentSemanticModel.ShellReferenceSymbol.Property property =>
                FormatShellPropertyReferenceHover(property),
            DocumentSemanticModel.ShellReferenceSymbol.Method method =>
                FormatShellMethodReferenceHover(method),
            _ => null,
        };
    }

    private static string FormatShellPropertyReferenceHover(DocumentSemanticModel.ShellReferenceSymbol.Property property)
    {
        var parts = new List<string> { "Property" };
        var doc = property.Symbol.Doc;

        AppendDeprecatedBanner(parts, doc);
        AppendSummary(parts, doc);

        parts.Add($"```tosh\n{FormatShellPropertySignature(property.Symbol)}\n```");

        if (doc is not null)
        {
            AppendDocCommentSections(parts, doc, parameters: null, returnTypeName: property.Symbol.TypeName);
        }

        return string.Join("\n\n", parts);
    }

    private static string FormatShellMethodReferenceHover(DocumentSemanticModel.ShellReferenceSymbol.Method method)
    {
        var label = method.Overloads.Count > 1 ? "Methods" : "Method";
        var signatures = string.Join("\n", method.Overloads.Take(6).Select(FormatShellMethodSignature));
        var overflow = method.Overloads.Count > 6
            ? $"\n... {method.Overloads.Count - 6} more overload(s)"
            : string.Empty;
        var doc = method.Overloads.Select(o => o.Doc).FirstOrDefault(d => d is not null);

        var parts = new List<string> { label };

        AppendDeprecatedBanner(parts, doc);
        AppendSummary(parts, doc);

        parts.Add($"```tosh\n{signatures}{overflow}\n```");

        if (doc is not null)
        {
            var canonical = method.Overloads[0];
            AppendDocCommentSections(parts, doc, canonical.Parameters, canonical.ReturnTypeName);
        }

        return string.Join("\n\n", parts);
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
            .Select(function =>
            {
                var doc = function.DocComment;
                return new LspSignatureInformation(
                    FormatTopLevelFunctionSignature(function),
                    Documentation: doc?.Description is { Length: > 0 } desc
                        ? desc
                        : function.IsCommandWrapper ? "Command-wrapper function" : null,
                    Parameters: function.Parameters
                        .Select(parameter => new LspParameterInformation(
                            FormatShellParameter(parameter),
                            Documentation: doc?.Parameters.TryGetValue(parameter.Name, out var paramDesc) == true && paramDesc.Length > 0
                                ? paramDesc
                                : null))
                        .ToArray());
            })
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
            case TupleLiteralArgumentSyntax tuple:
                foreach (var item in tuple.Items)
                {
                    CollectCommandCallSites(item, text, offset, matches);
                }
                break;
            case SetLiteralArgumentSyntax set:
                foreach (var item in set.Items)
                {
                    CollectCommandCallSites(item, text, offset, matches);
                }
                break;
            case ComparisonPatternSyntax comparisonPattern:
                CollectCommandCallSites(comparisonPattern.Operand, text, offset, matches);
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
                if (range.End is not null)
                {
                    CollectCommandCallSites(range.End, text, offset, matches);
                }
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

            case TupleLiteralArgumentSyntax tuple:
                foreach (var item in tuple.Items)
                {
                    CollectCallSites(item, text, offset, matches);
                }
                break;

            case SetLiteralArgumentSyntax set:
                foreach (var item in set.Items)
                {
                    CollectCallSites(item, text, offset, matches);
                }
                break;

            case ComparisonPatternSyntax comparisonPattern:
                CollectCallSites(comparisonPattern.Operand, text, offset, matches);
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
                if (range.End is not null)
                {
                    CollectCallSites(range.End, text, offset, matches);
                }
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
