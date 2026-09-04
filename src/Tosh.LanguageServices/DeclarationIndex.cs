using System.Text.RegularExpressions;
using Tosh.Runtime;
using Tosh.Language.Parsing;

namespace Tosh.LanguageServices;

public sealed class DeclarationIndex
{
    private readonly string _sourceName;
    private readonly string _text;
    private readonly TextCoordinateMap _map;
    private readonly IReadOnlyList<IndexedDeclaration> _declarations;
    private readonly IReadOnlyList<IndexedFunctionDeclaration> _functions;

    private DeclarationIndex(
        string sourceName,
        string text,
        TextCoordinateMap map,
        IReadOnlyList<IndexedDeclaration> declarations,
        IReadOnlyList<IndexedFunctionDeclaration> functions)
    {
        _sourceName = sourceName;
        _text = text;
        _map = map;
        _declarations = declarations;
        _functions = functions;
    }

    public static DeclarationIndex Create(string sourceName, string text)
    {
        var parseResult = ToshParser.Parse(text, sourceName);
        var map = new TextCoordinateMap(parseResult.SourceText);
        var collector = new Collector(parseResult.SourceText, map, ToLocalPath(sourceName));
        collector.Collect(parseResult.Statement);
        return new DeclarationIndex(
            sourceName,
            parseResult.SourceText,
            map,
            collector.BuildDeclarations(),
            collector.BuildFunctions());
    }

    /// <summary>
    /// Turns whatever names the document into a filesystem path, or <see langword="null"/> when it
    /// does not name a file.
    /// </summary>
    /// <remarks>
    /// The language server identifies documents by URI — <c>file:///home/…/main.tosh</c> — while
    /// the CLI and tests pass plain paths. Relative <c>require</c> targets resolve against the
    /// document's directory, so the URI form has to be unwrapped first: `Path.GetDirectoryName`
    /// on a `file://` string yields `file:/home/…`, which resolves to nothing and silently
    /// disabled require-following in the editor while every unit test passed (<c>TS-P3-12</c>).
    /// </remarks>
    private static string? ToLocalPath(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            return null;
        }

        if (!sourceName.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return sourceName;
        }

        return Uri.TryCreate(sourceName, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath
            : null;
    }

    public IReadOnlyList<string> GetVisibleVariables(int offset)
    {
        return GetVisibleDeclarations(offset, declaration => declaration.Kind is DeclarationKind.Variable or DeclarationKind.Parameter or DeclarationKind.LoopVariable or DeclarationKind.CatchVariable)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetVisibleFunctions(int offset)
    {
        return GetVisibleDeclarations(offset, declaration => declaration.Kind == DeclarationKind.Function)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<IndexedFunctionDeclaration> GetVisibleFunctionOverloads(int offset, string name)
    {
        return _functions
            .Where(function =>
                string.Equals(function.Name, name, StringComparison.Ordinal) &&
                function.ScopeStart <= offset &&
                offset <= function.ScopeEnd &&
                function.SelectionStart <= offset)
            .OrderByDescending(function => function.ScopeDepth)
            .ThenByDescending(function => function.SelectionStart)
            .ToArray();
    }

    public IReadOnlyList<string> GetVisibleClasses(int offset)
    {
        return GetVisibleDeclarations(offset, declaration => declaration.Kind == DeclarationKind.Class)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> GetVisibleTypeLikeSymbols(int offset)
    {
        return GetVisibleDeclarations(
                offset,
                declaration => declaration.Kind is DeclarationKind.Class or DeclarationKind.Record or DeclarationKind.Enum or DeclarationKind.Union)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The names reached <i>inside</i> a declared type — an enum's members, a union's variants —
    /// for a type named at <paramref name="offset"/>. This is what the <c>::</c> path operator
    /// completes to; <c>TOAST-0090</c>.
    /// </summary>
    /// <remarks>
    /// Members are looked up by their scope rather than through
    /// <see cref="GetVisibleDeclarations"/>: a member's scope is its declaring type's span, which
    /// almost never contains the cursor. The type itself must be visible from the cursor, so the
    /// visibility rule still applies — it applies to the type, which is what was written.
    /// </remarks>
    public IReadOnlyList<IndexedTypeMember> GetTypeMembers(int offset, string typeName)
    {
        var declaringType = GetVisibleDeclarations(
                offset,
                declaration => declaration.Kind is DeclarationKind.Enum or DeclarationKind.Union)
            .FirstOrDefault(declaration => string.Equals(declaration.Name, typeName, StringComparison.Ordinal));

        if (declaringType is null)
        {
            return Array.Empty<IndexedTypeMember>();
        }

        var memberKind = declaringType.Kind == DeclarationKind.Enum
            ? DeclarationKind.EnumMember
            : DeclarationKind.Variant;

        return _declarations
            .Where(declaration =>
                declaration.Kind == memberKind &&
                declaration.ScopeStart == declaringType.DeclStart &&
                declaration.ScopeEnd == declaringType.DeclEnd)
            .Select(declaration => new IndexedTypeMember(
                declaration.Name,
                declaringType.Name,
                declaringType.Kind == DeclarationKind.Enum ? "Enum member" : "Variant",
                declaration.DocComment))
            .ToArray();
    }

    /// <summary>
    /// The members a typed literal may set — a class's properties, a record's fields;
    /// <c>TOAST-0091</c>. This is what <c>new T {| … |}</c> completes to.
    /// </summary>
    /// <remarks>
    /// Shares the scope rule with <see cref="GetTypeMembers"/>: the type must be visible from the
    /// cursor, its members are found by their declaring scope. A struct is not answered for —
    /// struct definitions are not in the index at all, so there is nothing to offer and saying
    /// nothing is better than guessing.
    /// </remarks>
    public IReadOnlyList<IndexedTypeMember> GetInitializableMembers(int offset, string typeName)
    {
        var declaringType = GetVisibleDeclarations(
                offset,
                declaration => declaration.Kind is DeclarationKind.Class or DeclarationKind.Record)
            .FirstOrDefault(declaration => string.Equals(declaration.Name, typeName, StringComparison.Ordinal));

        if (declaringType is null)
        {
            return Array.Empty<IndexedTypeMember>();
        }

        var memberKind = declaringType.Kind == DeclarationKind.Class
            ? DeclarationKind.Property
            : DeclarationKind.RecordField;

        return _declarations
            .Where(declaration =>
                declaration.Kind == memberKind &&
                declaration.ScopeStart == declaringType.DeclStart &&
                declaration.ScopeEnd == declaringType.DeclEnd)
            .Select(declaration => new IndexedTypeMember(
                declaration.Name,
                declaringType.Name,
                declaringType.Kind == DeclarationKind.Class ? "Property" : "Field",
                declaration.DocComment))
            .ToArray();
    }

    public IReadOnlyList<string> GetVisibleModules(int offset)
    {
        return GetVisibleDeclarations(offset, declaration => declaration.Kind == DeclarationKind.Module)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public DocComment? GetDeclarationDocComment(int offset, string name)
    {
        return GetVisibleDeclarations(
                offset,
                declaration => declaration.Kind is DeclarationKind.Class or DeclarationKind.Record or DeclarationKind.Enum or DeclarationKind.Module or DeclarationKind.Subcommand or DeclarationKind.Flag or DeclarationKind.Argument or DeclarationKind.TypeAlias or DeclarationKind.Property or DeclarationKind.ClassMethod or DeclarationKind.EnumMember or DeclarationKind.Variant or DeclarationKind.RecordField
                    && string.Equals(declaration.Name, name, StringComparison.Ordinal))
            .Select(declaration => declaration.DocComment)
            .FirstOrDefault(doc => doc is not null);
    }

    public string? GetDeclarationKindLabel(int offset, string name)
    {
        return GetVisibleDeclarations(
                offset,
                declaration => declaration.Kind is DeclarationKind.Class or DeclarationKind.Record or DeclarationKind.Enum or DeclarationKind.Module or DeclarationKind.Subcommand or DeclarationKind.Flag or DeclarationKind.Argument or DeclarationKind.TypeAlias or DeclarationKind.Property or DeclarationKind.ClassMethod or DeclarationKind.EnumMember or DeclarationKind.Variant or DeclarationKind.RecordField
                    && string.Equals(declaration.Name, name, StringComparison.Ordinal))
            .Select(declaration => declaration.Kind switch
            {
                DeclarationKind.Class => "Class",
                DeclarationKind.Record => "Record",
                DeclarationKind.Enum => "Enum",
                DeclarationKind.Union => "Union",
                DeclarationKind.Module => "Module",
                DeclarationKind.Subcommand => "Subcommand",
                DeclarationKind.Flag => "Flag",
                DeclarationKind.Argument => "Argument",
                DeclarationKind.TypeAlias => "Type alias",
                DeclarationKind.Property => "Property",
                DeclarationKind.ClassMethod => "Method",
                DeclarationKind.EnumMember => "Enum member",
                DeclarationKind.Variant => "Variant",
                DeclarationKind.RecordField => "Field",
                _ => null,
            })
            .FirstOrDefault();
    }

    public IReadOnlyList<IndexedSymbol> GetSymbols()
    {
        var localDecls = _declarations.Where(d => !d.IsImported).ToList();
        var localFuncs = _functions.Where(f => !f.IsImported).ToList();

        bool IsInsideExecutableBlock(int pos)
        {
            if (localFuncs.Any(f => pos >= f.SelectionStart && pos <= f.ScopeEnd)) return true;
            if (localDecls.Any(d => d.Kind is DeclarationKind.ClassMethod or DeclarationKind.Subcommand && pos >= d.SelectionStart && pos <= d.ScopeEnd)) return true;
            return false;
        }

        var outlineDecls = localDecls
            .Where(d => d.Kind is not (DeclarationKind.Function or DeclarationKind.Parameter or DeclarationKind.LoopVariable or DeclarationKind.CatchVariable))
            .Where(d => d.Kind != DeclarationKind.Variable || !IsInsideExecutableBlock(d.SelectionStart))
            .ToList();

        IndexedDeclaration? FindClosestContainer(int pos, IndexedDeclaration? ignore = null)
        {
            return outlineDecls
                .Where(d => d != ignore && d.Kind is DeclarationKind.Class or DeclarationKind.Interface or DeclarationKind.Module or DeclarationKind.Enum or DeclarationKind.Union or DeclarationKind.Record or DeclarationKind.Subcommand)
                .Where(d => pos >= d.DeclStart && pos <= d.DeclEnd)
                .OrderByDescending(d => d.DeclStart)
                .FirstOrDefault();
        }

        IndexedSymbol BuildSymbol(IndexedDeclaration d, List<IndexedSymbol>? children = null)
        {
            var detail = d.Kind switch
            {
                DeclarationKind.Class => "class",
                DeclarationKind.Interface => "interface",
                DeclarationKind.Module => "module",
                DeclarationKind.Enum => "enum",
                DeclarationKind.Union => "union",
                DeclarationKind.Record => "record",
                DeclarationKind.Subcommand => "subcommand",
                DeclarationKind.TypeAlias => "type alias",
                DeclarationKind.Property => "property",
                DeclarationKind.ClassMethod => "method",
                DeclarationKind.EnumMember => "enum member",
                DeclarationKind.Variant => "variant",
                DeclarationKind.RecordField => "field",
                DeclarationKind.Flag => "flag",
                DeclarationKind.Argument => "argument",
                _ => "var",
            };

            var kind = d.Kind switch
            {
                DeclarationKind.Class => 5,       // Class
                DeclarationKind.Interface => 11,  // Interface
                DeclarationKind.Module => 2,      // Module
                DeclarationKind.Enum => 10,      // Enum
                DeclarationKind.Union => 10,     // Enum — LSP has no union symbol
                DeclarationKind.Record => 23,    // Struct
                DeclarationKind.Subcommand => 12,// Function
                DeclarationKind.TypeAlias => 5,  // Class
                DeclarationKind.Property => 7,   // Property
                DeclarationKind.ClassMethod => 6, // Method
                DeclarationKind.EnumMember => 22,// EnumMember
                DeclarationKind.Variant => 22,   // EnumMember
                DeclarationKind.RecordField => 8,// Field
                DeclarationKind.Flag => 7,       // Property
                DeclarationKind.Argument => 13,  // Variable
                _ => 13,                         // Variable
            };

            return new IndexedSymbol(
                d.Name,
                detail,
                kind,
                d.Range,
                d.SelectionRange,
                children is { Count: > 0 } ? children : null,
                DocComment: d.DocComment);
        }

        IndexedSymbol BuildContainerSymbol(IndexedDeclaration container)
        {
            var children = new List<IndexedSymbol>();

            var childDecls = outlineDecls
                .Where(child => child != container && FindClosestContainer(child.SelectionStart, ignore: child) == container)
                .Where(child => container.Kind is not (DeclarationKind.Class or DeclarationKind.Interface or DeclarationKind.Enum or DeclarationKind.Union or DeclarationKind.Record) || child.Kind != DeclarationKind.Variable)
                .OrderBy(child => child.SelectionStart)
                .Select(child => child.Kind is DeclarationKind.Class or DeclarationKind.Interface or DeclarationKind.Module or DeclarationKind.Enum or DeclarationKind.Union or DeclarationKind.Record or DeclarationKind.Subcommand
                    ? BuildContainerSymbol(child)
                    : BuildSymbol(child))
                .ToList();

            children.AddRange(childDecls);

            var childFuncs = localFuncs
                .Where(f => FindClosestContainer(f.SelectionStart) == container)
                .GroupBy(f => new { f.Name, f.ScopeStart, f.ScopeEnd })
                .Select(group =>
                {
                    var ordered = group.OrderBy(f => f.SelectionStart).ToArray();
                    var first = ordered[0];
                    var paramsText = string.Join(", ", first.Parameters.Select(p => p.Name + (!string.IsNullOrEmpty(p.TypeName) ? ": " + p.TypeName : "")));
                    var returnText = !string.IsNullOrEmpty(first.ReturnTypeName) ? $" -> {first.ReturnTypeName}" : "";
                    var detail = ordered.Length == 1 ? $"({paramsText}){returnText}" : $"({ordered.Length} overloads)";
                    return new IndexedSymbol(
                        first.Name,
                        detail,
                        12,
                        first.Range,
                        first.SelectionRange,
                        DocComment: first.DocComment);
                })
                .ToList();

            children.AddRange(childFuncs);

            var sortedChildren = children
                .OrderBy(c => c.SelectionRange.Start.Line)
                .ThenBy(c => c.SelectionRange.Start.Character)
                .ToList();

            return BuildSymbol(container, sortedChildren);
        }

        var topLevelDecls = outlineDecls
            .Where(d => FindClosestContainer(d.SelectionStart, ignore: d) == null)
            .ToList();

        var topLevelFuncs = localFuncs
            .Where(f => FindClosestContainer(f.SelectionStart) == null)
            .GroupBy(f => new { f.Name, f.ScopeStart, f.ScopeEnd })
            .Select(group =>
            {
                var ordered = group.OrderBy(f => f.SelectionStart).ToArray();
                var first = ordered[0];
                var paramsText = string.Join(", ", first.Parameters.Select(p => p.Name + (!string.IsNullOrEmpty(p.TypeName) ? ": " + p.TypeName : "")));
                var returnText = !string.IsNullOrEmpty(first.ReturnTypeName) ? $" -> {first.ReturnTypeName}" : "";
                var detail = ordered.Length == 1 ? $"({paramsText}){returnText}" : $"({ordered.Length} overloads)";
                return new IndexedSymbol(
                    first.Name,
                    detail,
                    12,
                    first.Range,
                    first.SelectionRange,
                    DocComment: first.DocComment);
            })
            .ToList();

        var topSymbols = new List<IndexedSymbol>();

        var mergedContainers = topLevelDecls
            .Where(d => d.Kind is DeclarationKind.Class or DeclarationKind.Interface or DeclarationKind.Module or DeclarationKind.Enum or DeclarationKind.Union or DeclarationKind.Record or DeclarationKind.Subcommand)
            .GroupBy(d => new { d.Name, d.Kind })
            .Select(group =>
            {
                var list = group.ToList();
                if (list.Count == 1)
                {
                    return BuildContainerSymbol(list[0]);
                }

                var combinedChildren = new List<IndexedSymbol>();
                foreach (var part in list)
                {
                    var built = BuildContainerSymbol(part);
                    if (built.Children != null)
                    {
                        combinedChildren.AddRange(built.Children);
                    }
                }

                var firstPart = list.OrderBy(p => p.SelectionStart).First();
                var detail = firstPart.Kind switch
                {
                    DeclarationKind.Class => "class",
                    DeclarationKind.Interface => "interface",
                    DeclarationKind.Module => "module",
                    DeclarationKind.Enum => "enum",
                    DeclarationKind.Union => "union",
                    DeclarationKind.Record => "record",
                    _ => "subcommand",
                };
                var kind = firstPart.Kind switch
                {
                    DeclarationKind.Class => 5,
                    DeclarationKind.Interface => 11,
                    DeclarationKind.Module => 2,
                    DeclarationKind.Enum => 10,
                    DeclarationKind.Union => 10,
                    DeclarationKind.Record => 23,
                    _ => 12,
                };

                return new IndexedSymbol(
                    firstPart.Name,
                    detail,
                    kind,
                    firstPart.Range,
                    firstPart.SelectionRange,
                    combinedChildren.OrderBy(c => c.SelectionRange.Start.Line).ThenBy(c => c.SelectionRange.Start.Character).ToList());
            });

        topSymbols.AddRange(mergedContainers);

        var topNonContainers = topLevelDecls
            .Where(d => d.Kind is not (DeclarationKind.Class or DeclarationKind.Interface or DeclarationKind.Module or DeclarationKind.Enum or DeclarationKind.Union or DeclarationKind.Record or DeclarationKind.Subcommand))
            .Select(d => BuildSymbol(d))
            .ToList();

        topSymbols.AddRange(topNonContainers);
        topSymbols.AddRange(topLevelFuncs);

        return topSymbols
            .OrderBy(symbol => symbol.SelectionRange.Start.Line)
            .ThenBy(symbol => symbol.SelectionRange.Start.Character)
            .ToArray();
    }

    private static bool IsInsideRange(LspRange inner, LspRange outer)
    {
        if (inner.Start.Line < outer.Start.Line || inner.End.Line > outer.End.Line) return false;
        if (inner.Start.Line == outer.Start.Line && inner.Start.Character < outer.Start.Character) return false;
        if (inner.End.Line == outer.End.Line && inner.End.Character > outer.End.Character) return false;
        return true;
    }

    public IReadOnlyList<LspLocation> FindDefinitions(LspPosition position)
    {
        var offset = _map.ToOffset(position);
        var token = FindWordAt(offset);

        if (string.IsNullOrWhiteSpace(token.Word))
        {
            return Array.Empty<LspLocation>();
        }

        if (TryGetVariableReferenceName(token.Word, out var variableName))
        {
            var variableDeclaration = ResolveVisibleDeclaration(offset, variableName, entry =>
                entry.Kind is DeclarationKind.Variable or DeclarationKind.Parameter or DeclarationKind.LoopVariable or DeclarationKind.CatchVariable);

            return variableDeclaration is null
                ? Array.Empty<LspLocation>()
                : [new LspLocation(_sourceName, variableDeclaration.SelectionRange)];
        }

        if (token.Word.Contains('.', StringComparison.Ordinal))
        {
            return Array.Empty<LspLocation>();
        }

        var functions = GetVisibleFunctionOverloads(offset, token.Word);
        if (functions.Count > 0)
        {
            return functions
                .Select(function => new LspLocation(_sourceName, function.SelectionRange))
                .ToArray();
        }

        var declaration = ResolveVisibleDeclaration(
            offset,
            token.Word,
            entry => entry.Kind is DeclarationKind.Function or DeclarationKind.Class or DeclarationKind.Module or DeclarationKind.Enum or DeclarationKind.Union or DeclarationKind.Record,
            requireDeclarationBeforeUse: false);
        return declaration is null
            ? Array.Empty<LspLocation>()
            : [new LspLocation(_sourceName, declaration.SelectionRange)];
    }

    /// <summary>
    /// Returns every reference to the symbol at <paramref name="position"/>
    /// within this document, including the declaration site when
    /// <paramref name="includeDeclaration"/> is <c>true</c>. The reference
    /// scan tokenises the source with the production lexer, then for each
    /// candidate identifier resolves it through the same scope-aware lookup
    /// used by <see cref="FindDefinitions(LspPosition)"/> and keeps only
    /// the occurrences that bind to the same declaration.
    /// </summary>
    public IReadOnlyList<LspLocation> FindReferences(LspPosition position, bool includeDeclaration)
    {
        var target = ResolveTargetAtPosition(position);
        if (target is null) return Array.Empty<LspLocation>();
        return CollectReferences(target.Declaration, target.IsVariable, includeDeclaration)
            .Select(range => new LspLocation(_sourceName, range))
            .ToArray();
    }

    /// <summary>
    /// Returns the editable identifier range at <paramref name="position"/>,
    /// or <c>null</c> if the cursor is not on a renamable symbol. For
    /// variable references (<c>$name</c>) the range excludes the leading
    /// <c>$</c> so editor clients show only the identifier in the rename
    /// affordance and the user types only the new identifier.
    /// </summary>
    public LspPrepareRenameResult? PrepareRename(LspPosition position)
    {
        var offset = _map.ToOffset(position);
        var target = ResolveTargetAtPosition(position);
        if (target is null) return null;

        // Compute the editable range at the cursor itself (not the
        // declaration site). For a `$name` reference, skip the leading `$`.
        var token = FindWordAt(offset);
        var start = token.Start;
        var end = token.End;
        if (target.IsVariable && start < _text.Length && _text[start] == '$')
        {
            start += 1;
        }
        // If the token is `$ns.member`, only the head identifier is renamable.
        var word = _text[start..end];
        var dot = word.IndexOf('.');
        if (dot >= 0)
        {
            end = start + dot;
        }
        var range = new LspRange(_map.ToPosition(start), _map.ToPosition(end));
        return new LspPrepareRenameResult(range, target.Declaration.Name);
    }

    /// <summary>
    /// Builds the workspace edit that renames the symbol at
    /// <paramref name="position"/> to <paramref name="newName"/>. Returns
    /// <c>null</c> when the position does not point at a renamable
    /// declaration. The edit includes the declaration site and every
    /// reference; for variable references only the identifier portion
    /// (after <c>$</c>) is rewritten.
    /// </summary>
    public LspWorkspaceEdit? BuildRenameEdits(LspPosition position, string newName)
    {
        var target = ResolveTargetAtPosition(position);
        if (target is null) return null;
        var ranges = CollectReferences(target.Declaration, target.IsVariable, includeDeclaration: true);
        var edits = new List<LspTextEdit>(ranges.Count);
        foreach (var range in ranges)
        {
            var editRange = target.IsVariable && IsVariableReferenceRange(range)
                ? StripDollarPrefix(range)
                : range;
            edits.Add(new LspTextEdit(editRange, newName));
        }
        var changes = new Dictionary<string, IReadOnlyList<LspTextEdit>>(StringComparer.Ordinal)
        {
            [_sourceName] = edits,
        };
        return new LspWorkspaceEdit(changes);
    }

    private TargetSymbol? ResolveTargetAtPosition(LspPosition position)
    {
        var offset = _map.ToOffset(position);
        var token = FindWordAt(offset);
        if (string.IsNullOrWhiteSpace(token.Word)) return null;

        // Fast path: cursor is exactly on a declaration's selection start
        // (e.g. the identifier in `var greeting`, `func greet`, `class Item`).
        // Resolve directly to that declaration so renames/refs work from the
        // declaration site, not just from references.
        var declAtCursor = _declarations.FirstOrDefault(d =>
            d.SelectionStart >= token.Start && d.SelectionStart < token.End);
        if (declAtCursor is not null)
        {
            var isVar = declAtCursor.Kind is DeclarationKind.Variable or DeclarationKind.Parameter or DeclarationKind.LoopVariable or DeclarationKind.CatchVariable;
            return new TargetSymbol(declAtCursor, isVar, declAtCursor.SelectionRange);
        }

        if (TryGetVariableReferenceName(token.Word, out var varName))
        {
            var decl = ResolveVisibleDeclaration(offset, varName, entry =>
                entry.Kind is DeclarationKind.Variable or DeclarationKind.Parameter or DeclarationKind.LoopVariable or DeclarationKind.CatchVariable);
            return decl is null ? null : new TargetSymbol(decl, IsVariable: true, decl.SelectionRange);
        }

        if (token.Word.Contains('.', StringComparison.Ordinal)) return null;

        // Function overloads share a name; treat them as a group keyed by name + scope.
        var overloads = GetVisibleFunctionOverloads(offset, token.Word);
        if (overloads.Count > 0)
        {
            var first = overloads[0];
            var anchor = _declarations.FirstOrDefault(d =>
                d.Kind == DeclarationKind.Function &&
                string.Equals(d.Name, first.Name, StringComparison.Ordinal) &&
                d.ScopeStart == first.ScopeStart &&
                d.ScopeEnd == first.ScopeEnd);
            if (anchor is null) return null;
            return new TargetSymbol(anchor, IsVariable: false, anchor.SelectionRange);
        }

        var typeDecl = ResolveVisibleDeclaration(
            offset,
            token.Word,
            entry => entry.Kind is DeclarationKind.Function or DeclarationKind.Class or DeclarationKind.Module or DeclarationKind.Enum or DeclarationKind.Union or DeclarationKind.Record,
            requireDeclarationBeforeUse: false);
        return typeDecl is null ? null : new TargetSymbol(typeDecl, IsVariable: false, typeDecl.SelectionRange);
    }

    private List<LspRange> CollectReferences(IndexedDeclaration target, bool isVariable, bool includeDeclaration)
    {
        var results = new List<LspRange>();
        if (includeDeclaration)
        {
            // Function overloads: include every overload's selection range as a "declaration".
            if (target.Kind == DeclarationKind.Function)
            {
                foreach (var fn in _functions)
                {
                    if (string.Equals(fn.Name, target.Name, StringComparison.Ordinal)
                        && fn.ScopeStart == target.ScopeStart
                        && fn.ScopeEnd == target.ScopeEnd)
                    {
                        results.Add(fn.SelectionRange);
                    }
                }
            }
            else
            {
                results.Add(target.SelectionRange);
            }
        }

        var lex = new ToshLexer(_text);
        var tokens = lex.Lex();
        foreach (var token in tokens)
        {
            if (token.Kind != SyntaxTokenKind.Bareword) continue;
            var text = token.Text;
            if (string.IsNullOrEmpty(text)) continue;

            string head;
            int headStart;
            if (isVariable)
            {
                if (text.Length < 2 || text[0] != '$') continue;
                var dot = text.IndexOf('.');
                head = dot < 0 ? text[1..] : text[1..dot];
                headStart = token.Position; // includes '$'
            }
            else
            {
                if (text[0] == '$') continue;
                var dot = text.IndexOf('.');
                head = dot < 0 ? text : text[..dot];
                headStart = token.Position;
            }
            if (!string.Equals(head, target.Name, StringComparison.Ordinal)) continue;

            // Probe inside the head identifier (token start +1 for variables to land past `$`).
            var probeOffset = isVariable ? headStart + 1 : headStart;

            IndexedDeclaration? resolved;
            if (isVariable)
            {
                resolved = ResolveVisibleDeclaration(probeOffset, head, entry =>
                    entry.Kind is DeclarationKind.Variable or DeclarationKind.Parameter or DeclarationKind.LoopVariable or DeclarationKind.CatchVariable);
            }
            else if (target.Kind == DeclarationKind.Function)
            {
                // Any in-scope overload of the same name counts as a reference.
                var overloads = GetVisibleFunctionOverloads(probeOffset, head);
                resolved = overloads.Count == 0 ? null
                    : _declarations.FirstOrDefault(d =>
                        d.Kind == DeclarationKind.Function &&
                        string.Equals(d.Name, target.Name, StringComparison.Ordinal) &&
                        d.ScopeStart == target.ScopeStart &&
                        d.ScopeEnd == target.ScopeEnd);
            }
            else
            {
                resolved = ResolveVisibleDeclaration(probeOffset, head, entry =>
                    entry.Kind is DeclarationKind.Function or DeclarationKind.Class or DeclarationKind.Module or DeclarationKind.Enum or DeclarationKind.Union or DeclarationKind.Record,
                    requireDeclarationBeforeUse: false);
            }
            if (resolved is null) continue;
            if (resolved.SelectionStart != target.SelectionStart) continue;

            var headEnd = headStart + (isVariable ? 1 + head.Length : head.Length);
            var range = new LspRange(_map.ToPosition(headStart), _map.ToPosition(headEnd));

            // Don't double-emit the declaration site.
            if (RangeEquals(range, target.SelectionRange) && includeDeclaration) continue;
            results.Add(range);
        }
        return results;
    }

    private LspRange StripDollarPrefix(LspRange range)
    {
        // Selection ranges for `$x` either start at `$` or already at `x`
        // depending on the declaration vs. reference. When the range starts
        // at a `$`, advance by one column.
        var startOffset = _map.ToOffset(range.Start);
        if (startOffset >= 0 && startOffset < _text.Length && _text[startOffset] == '$')
        {
            return new LspRange(_map.ToPosition(startOffset + 1), range.End);
        }
        return range;
    }

    private bool IsVariableReferenceRange(LspRange range)
    {
        var startOffset = _map.ToOffset(range.Start);
        return startOffset >= 0 && startOffset < _text.Length && _text[startOffset] == '$';
    }

    private static bool RangeEquals(LspRange a, LspRange b)
        => a.Start.Line == b.Start.Line
            && a.Start.Character == b.Start.Character
            && a.End.Line == b.End.Line
            && a.End.Character == b.End.Character;

    private sealed record TargetSymbol(IndexedDeclaration Declaration, bool IsVariable, LspRange SelectionRange);

    private IReadOnlyList<IndexedDeclaration> GetVisibleDeclarations(int offset, Func<IndexedDeclaration, bool> predicate)
    {
        return _declarations
            .Where(declaration =>
                predicate(declaration) &&
                declaration.ScopeStart <= offset &&
                offset <= declaration.ScopeEnd &&
                IsDeclarationVisibleAtOffset(declaration, offset))
            .OrderByDescending(declaration => declaration.ScopeDepth)
            .ThenByDescending(declaration => declaration.SelectionStart)
            .ToArray();
    }

    private IndexedDeclaration? ResolveVisibleDeclaration(
        int offset,
        string name,
        Func<IndexedDeclaration, bool> predicate,
        bool requireDeclarationBeforeUse = true)
    {
        return _declarations
            .Where(declaration =>
                string.Equals(declaration.Name, name, StringComparison.Ordinal) &&
                predicate(declaration) &&
                declaration.ScopeStart <= offset &&
                offset <= declaration.ScopeEnd &&
                (!requireDeclarationBeforeUse || IsDeclarationVisibleAtOffset(declaration, offset)))
            .OrderByDescending(declaration => declaration.ScopeDepth)
            .ThenByDescending(declaration => declaration.SelectionStart)
            .FirstOrDefault();
    }

    private static bool IsDeclarationVisibleAtOffset(IndexedDeclaration declaration, int offset)
    {
        return declaration.Kind == DeclarationKind.Function || declaration.SelectionStart <= offset;
    }

    private static bool TryGetVariableReferenceName(string word, out string name)
    {
        name = string.Empty;

        if (!word.StartsWith('$'))
        {
            return false;
        }

        var trimmed = word[1..];
        var dotIndex = trimmed.IndexOf('.');
        name = dotIndex >= 0 ? trimmed[..dotIndex] : trimmed;
        return name.Length > 0;
    }

    private (string Word, int Start, int End) FindWordAt(int offset)
    {
        if (string.IsNullOrEmpty(_text))
        {
            return (string.Empty, 0, 0);
        }

        var index = Math.Clamp(offset, 0, Math.Max(0, _text.Length - 1));

        bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch is '$' or '_' or '-' or '.';

        if (!IsWordChar(_text[index]) && index > 0 && IsWordChar(_text[index - 1]))
        {
            index--;
        }

        if (!IsWordChar(_text[index]))
        {
            return (string.Empty, index, index);
        }

        var start = index;
        while (start > 0 && IsWordChar(_text[start - 1]))
        {
            start--;
        }

        var end = index + 1;
        while (end < _text.Length && IsWordChar(_text[end]))
        {
            end++;
        }

        return (_text[start..end], start, end);
    }

    private enum DeclarationKind
    {
        Variable,
        Parameter,
        LoopVariable,
        CatchVariable,
        Function,
        Class,
        Interface,
        Trait,
        Module,
        Enum,
        Record,
        Subcommand,
        Flag,
        Argument,
        TypeAlias,
        Property,
        ClassMethod,
        EnumMember,
        Union,
        Variant,
        RecordField,
    }

    /// <summary>A name declared inside a type: an enum member or a union variant.</summary>
    public sealed record IndexedTypeMember(
        string Name,
        string DeclaringType,
        string KindLabel,
        DocComment? DocComment);

    public sealed record IndexedSymbol(
        string Name,
        string Detail,
        int SymbolKind,
        LspRange Range,
        LspRange SelectionRange,
        IReadOnlyList<IndexedSymbol>? Children = null,
        DocComment? DocComment = null);

    public sealed record IndexedFunctionDeclaration(
        string Name,
        IReadOnlyList<FunctionParameterSyntax> Parameters,
        string? ReturnTypeName,
        bool IsCommandWrapper,
        int ScopeStart,
        int ScopeEnd,
        int ScopeDepth,
        int SelectionStart,
        LspRange Range,
        LspRange SelectionRange,
        DocComment? DocComment = null,
        bool IsImported = false);

    private sealed record IndexedDeclaration(
        string Name,
        DeclarationKind Kind,
        int ScopeStart,
        int ScopeEnd,
        int DeclStart,
        int DeclEnd,
        int ScopeDepth,
        int SelectionStart,
        LspRange Range,
        LspRange SelectionRange,
        DocComment? DocComment = null,
        bool IsImported = false);

    private sealed class Collector
    {
        /// <summary>
        /// How far a chain of <c>require</c> statements is followed. A library that requires a
        /// library that requires a third is realistic; deeper than that is a sign of a cycle the
        /// visited set already handles, or of a graph too large to be worth parsing on every
        /// keystroke.
        /// </summary>
        private const int MaxRequireDepth = 3;

        /// <summary>Documents already pulled in, so a cycle terminates and a diamond parses once.</summary>
        private readonly HashSet<string> _visitedRequires = new(StringComparer.OrdinalIgnoreCase);

        private readonly string? _documentPath;
        private readonly string _text;
        private readonly TextCoordinateMap _map;
        private readonly List<IndexedDeclaration> _declarations = new();
        private readonly List<IndexedFunctionDeclaration> _functions = new();

        public Collector(string text, TextCoordinateMap map, string? documentPath = null)
        {
            _text = text;
            _map = map;
            _documentPath = documentPath;
        }

        public void Collect(StatementSyntax statement)
        {
            var rootScope = TextSpan.FromBounds(0, _text.Length);
            CollectStatement(statement, rootScope, depth: 0);
        }

        public IReadOnlyList<IndexedDeclaration> BuildDeclarations() => _declarations;

        public IReadOnlyList<IndexedFunctionDeclaration> BuildFunctions() => _functions;

        private void CollectStatement(StatementSyntax statement, TextSpan scopeSpan, int depth)
        {
            switch (statement)
            {
                case ScriptStatementSyntax script:
                    foreach (var child in script.Statements)
                    {
                        CollectStatement(child, scopeSpan, depth);
                    }
                    break;

                case PipelineStatementSyntax pipelineStatement:
                    CollectPipeline(pipelineStatement.Pipeline, scopeSpan, depth);
                    break;

                case VariableDeclarationStatementSyntax variable:
                    AddDeclaration(variable.Name, DeclarationKind.Variable, variable.Span, scopeSpan, depth);
                    if (variable.Value is not null)
                    {
                        CollectPipeline(variable.Value, scopeSpan, depth);
                    }
                    break;

                case VariableAssignmentStatementSyntax assignment:
                    CollectPipeline(assignment.Value, scopeSpan, depth);
                    break;

                case MemberAssignmentStatementSyntax assignment:
                    CollectArgument(assignment.Target, scopeSpan, depth);
                    CollectPipeline(assignment.Value, scopeSpan, depth);
                    break;

                case ReturnStatementSyntax @return when @return.Value is not null:
                    CollectPipeline(@return.Value, scopeSpan, depth);
                    break;

                case ThrowStatementSyntax @throw when @throw.Value is not null:
                    CollectPipeline(@throw.Value, scopeSpan, depth);
                    break;

                case FunctionDefinitionStatementSyntax function:
                    AddDeclaration(function.Name, DeclarationKind.Function, function.Span, scopeSpan, depth);
                    AddFunctionDeclaration(function, scopeSpan, depth);
                    var functionScope = function.Body.Span;
                    foreach (var parameter in function.Parameters)
                    {
                        AddDeclaration(parameter.Name, DeclarationKind.Parameter, parameter.Span, functionScope, depth + 1);
                    }

                    foreach (var child in function.Body.Statements)
                    {
                        CollectStatement(child, functionScope, depth + 1);
                    }
                    break;

                case RequireStatementSyntax require:
                    CollectRequiredDocument(
                        require,
                        scopeSpan,
                        depth,
                        Path.GetDirectoryName(_documentPath));
                    break;

                case ClassDefinitionStatementSyntax @class:
                    var classSpan = @class.Members.Count > 0
                        ? TextSpan.FromBounds(@class.Span.Start, Math.Max(@class.Span.End, @class.Members.Max(m => m.Span.End)))
                        : @class.Span;
                    AddDeclaration(@class.Name, DeclarationKind.Class, classSpan, scopeSpan, depth, @class.DocComment);
                    foreach (var member in @class.Members)
                    {
                        switch (member)
                        {
                            case ClassPropertyMemberSyntax prop:
                                AddDeclaration(prop.Name, DeclarationKind.Property, prop.Span, classSpan, depth + 1, prop.DocComment);
                                if (prop.Initializer is not null)
                                {
                                    CollectPipeline(prop.Initializer, classSpan, depth + 1);
                                }
                                if (prop.GetterBody is not null)
                                {
                                    CollectBlock(prop.GetterBody, depth + 2);
                                }
                                if (prop.SetterBody is not null)
                                {
                                    CollectBlock(prop.SetterBody, depth + 2);
                                }
                                break;

                            case ClassMethodMemberSyntax method:
                                AddDeclaration(method.Method.Name, DeclarationKind.ClassMethod, method.Span, classSpan, depth + 1, method.Method.DocComment);
                                if (method.Method.Body is not null)
                                {
                                    var methodScope = method.Method.Body.Span;
                                    foreach (var parameter in method.Method.Parameters)
                                    {
                                        AddDeclaration(parameter.Name, DeclarationKind.Parameter, parameter.Span, methodScope, depth + 2);
                                    }
                                    foreach (var child in method.Method.Body.Statements)
                                    {
                                        CollectStatement(child, methodScope, depth + 2);
                                    }
                                }
                                break;
                        }
                    }
                    break;

                case InterfaceDefinitionStatementSyntax @interface:
                    var interfaceSpan = @interface.Methods.Count > 0
                        ? TextSpan.FromBounds(@interface.Span.Start, Math.Max(@interface.Span.End, @interface.Methods.Max(m => m.Span.End)))
                        : @interface.Span;
                    AddDeclaration(@interface.Name, DeclarationKind.Interface, interfaceSpan, scopeSpan, depth, @interface.DocComment);
                    foreach (var method in @interface.Methods)
                    {
                        AddDeclaration(method.Name, DeclarationKind.ClassMethod, method.Span, interfaceSpan, depth + 1);
                    }
                    break;

                // `TS-P2-101`. A trait was absent from this index entirely, so a documented
                // trait had no hover, no go-to-definition and no symbol — the one declaration
                // kind that fell out of the editor completely. Mirrors the interface case
                // above, which is the nearest shape: a name plus required members.
                case TraitDefinitionStatementSyntax trait:
                    var traitEnd = trait.Span.End;

                    if (trait.Methods.Count > 0)
                    {
                        traitEnd = Math.Max(traitEnd, trait.Methods.Max(m => m.Span.End));
                    }

                    if (trait.Properties.Count > 0)
                    {
                        traitEnd = Math.Max(traitEnd, trait.Properties.Max(p => p.Span.End));
                    }

                    var traitSpan = TextSpan.FromBounds(trait.Span.Start, traitEnd);
                    AddDeclaration(trait.Name, DeclarationKind.Trait, traitSpan, scopeSpan, depth, trait.DocComment);

                    foreach (var traitMethod in trait.Methods)
                    {
                        AddDeclaration(traitMethod.Name, DeclarationKind.ClassMethod, traitMethod.Span, traitSpan, depth + 1);
                    }

                    foreach (var traitProperty in trait.Properties)
                    {
                        AddDeclaration(traitProperty.Name, DeclarationKind.Property, traitProperty.Span, traitSpan, depth + 1);
                    }
                    break;

                case ModuleDefinitionStatementSyntax module:
                    var moduleSpan = TextSpan.FromBounds(module.Span.Start, Math.Max(module.Span.End, module.Body.Span.End));
                    AddDeclaration(module.Name, DeclarationKind.Module, moduleSpan, scopeSpan, depth, module.DocComment);
                    CollectBlock(module.Body, depth + 1);
                    break;

                case EnumDefinitionStatementSyntax @enum:
                    var enumSpan = @enum.Members.Count > 0
                        ? TextSpan.FromBounds(@enum.Span.Start, Math.Max(@enum.Span.End, @enum.Members.Max(m => m.Span.End)))
                        : @enum.Span;
                    AddDeclaration(@enum.Name, DeclarationKind.Enum, enumSpan, scopeSpan, depth, @enum.DocComment);
                    foreach (var member in @enum.Members)
                    {
                        AddDeclaration(member.Name, DeclarationKind.EnumMember, member.Span, enumSpan, depth + 1);
                        if (member.Value is not null)
                        {
                            CollectPipeline(member.Value, scopeSpan, depth);
                        }
                    }
                    break;

                case UnionDefinitionStatementSyntax union:
                    var unionSpan = union.Variants.Count > 0
                        ? TextSpan.FromBounds(union.Span.Start, Math.Max(union.Span.End, union.Variants.Max(v => v.Span.End)))
                        : union.Span;
                    AddDeclaration(union.Name, DeclarationKind.Union, unionSpan, scopeSpan, depth, union.DocComment);
                    foreach (var variant in union.Variants)
                    {
                        AddDeclaration(variant.Name, DeclarationKind.Variant, variant.Span, unionSpan, depth + 1);
                    }
                    break;

                case RecordDefinitionStatementSyntax record:
                    var recordSpan = record.Fields.Count > 0
                        ? TextSpan.FromBounds(record.Span.Start, Math.Max(record.Span.End, record.Fields.Max(f => f.Span.End)))
                        : record.Span;
                    AddDeclaration(record.Name, DeclarationKind.Record, recordSpan, scopeSpan, depth, record.DocComment);
                    foreach (var field in record.Fields)
                    {
                        AddDeclaration(field.Name, DeclarationKind.RecordField, field.Span, recordSpan, depth + 1);
                        if (field.DefaultValue is not null)
                        {
                            CollectPipeline(field.DefaultValue, scopeSpan, depth);
                        }
                    }
                    break;

                case TypeAliasStatementSyntax typeAlias:
                    AddDeclaration(typeAlias.Name, DeclarationKind.TypeAlias, typeAlias.Span, scopeSpan, depth, typeAlias.DocComment);
                    if (typeAlias.Refinement is not null)
                    {
                        CollectArgument(typeAlias.Refinement, scopeSpan, depth);
                    }
                    break;

                case SubcommandStatementSyntax subcommand:
                    AddDeclaration(
                        subcommand.Name,
                        DeclarationKind.Subcommand,
                        subcommand.Span,
                        scopeSpan,
                        depth,
                        subcommand.DocComment);
                    CollectBlock(subcommand.Body, depth + 1);
                    break;

                case ScriptInputStatementSyntax scriptInput:
                    {
                        // Each `flag` / `arg` declaration scopes its parameter
                        // name(s) to the enclosing block (subcommand body, or
                        // top-level script body) so hovering over the name
                        // anywhere in that block surfaces the doc-comment.
                        var inputKind = scriptInput.Kind == ScriptInputDeclarationKind.Flag
                            ? DeclarationKind.Flag
                            : DeclarationKind.Argument;
                        foreach (var parameter in scriptInput.Parameters)
                        {
                            // Synthesise a per-parameter DocComment when the
                            // raw doc-comment block is missing but the parser
                            // captured a one-line description on the parameter
                            // itself (the common single-flag case).
                            var doc = scriptInput.DocComment;
                            if (doc is null && !string.IsNullOrEmpty(parameter.Description))
                            {
                                doc = new DocComment(
                                    parameter.Description!,
                                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                                    Returns: null,
                                    Examples: Array.Empty<string>());
                            }

                            // Also expose as a plain Variable declaration so
                            // existing variable hover / completion code paths
                            // continue to work for `$year`, `$dryRun`, etc.
                            AddDeclaration(parameter.Name, DeclarationKind.Variable, parameter.Span, scopeSpan, depth);
                            AddDeclaration(parameter.Name, inputKind, parameter.Span, scopeSpan, depth, doc);
                        }
                    }
                    break;

                case IfStatementSyntax @if:
                    CollectArgument(@if.Condition, scopeSpan, depth);
                    CollectBlock(@if.ThenBlock, depth + 1);
                    if (@if.ElseBlock is not null)
                    {
                        CollectBlock(@if.ElseBlock, depth + 1);
                    }
                    break;

                case ForStatementSyntax @for:
                    CollectPipeline(@for.Source, scopeSpan, depth);
                    AddDeclaration(@for.VariableName, DeclarationKind.LoopVariable, @for.Span, @for.Body.Span, depth + 1);
                    CollectBlock(@for.Body, depth + 1);
                    break;

                case WhileStatementSyntax @while:
                    CollectArgument(@while.Condition, scopeSpan, depth);
                    CollectBlock(@while.Body, depth + 1);
                    break;

                case UntilStatementSyntax until:
                    CollectArgument(until.Condition, scopeSpan, depth);
                    CollectBlock(until.Body, depth + 1);
                    break;

                case TryStatementSyntax @try:
                    CollectBlock(@try.TryBlock, depth + 1);
                    if (@try.CatchClause is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(@try.CatchClause.VariableName))
                        {
                            AddDeclaration(@try.CatchClause.VariableName!, DeclarationKind.CatchVariable, @try.CatchClause.Span, @try.CatchClause.Body.Span, depth + 1);
                        }

                        CollectBlock(@try.CatchClause.Body, depth + 1);
                    }

                    if (@try.FinallyBlock is not null)
                    {
                        CollectBlock(@try.FinallyBlock, depth + 1);
                    }
                    break;

                case DeferStatementSyntax @defer:
                    CollectBlock(@defer.Body, depth + 1);
                    break;

                case SwitchStatementSyntax @switch:
                    CollectArgument(@switch.Value, scopeSpan, depth);
                    foreach (var @case in @switch.Cases)
                    {
                        CollectArgument(@case.MatchExpression, scopeSpan, depth);
                        CollectBlock(@case.Body, depth + 1);
                    }

                    if (@switch.DefaultBlock is not null)
                    {
                        CollectBlock(@switch.DefaultBlock, depth + 1);
                    }
                    break;
            }
        }

        private void CollectBlock(BlockSyntax block, int depth)
        {
            foreach (var statement in block.Statements)
            {
                CollectStatement(statement, block.Span, depth);
            }
        }

        private void CollectPipeline(PipelineSyntax pipeline, TextSpan scopeSpan, int depth)
        {
            foreach (var stage in pipeline.Stages)
            {
                switch (stage)
                {
                    case CommandSyntax command:
                        foreach (var argument in command.Arguments)
                        {
                            CollectArgument(argument, scopeSpan, depth);
                        }
                        break;

                    case ExpressionPipelineStageSyntax expression:
                        CollectArgument(expression.Expression, scopeSpan, depth);
                        break;
                }
            }

            if (pipeline.Redirections is not null)
            {
                foreach (var redirection in pipeline.Redirections)
                {
                    CollectArgument(redirection.Target, scopeSpan, depth);
                }
            }
        }

        private void CollectArgument(ArgumentSyntax argument, TextSpan scopeSpan, int depth)
        {
            switch (argument)
            {
                case SplatArgumentSyntax splat:
                    CollectArgument(splat.Value, scopeSpan, depth);
                    break;

                case NewObjectArgumentSyntax newObject:
                    foreach (var child in newObject.Arguments)
                    {
                        CollectArgument(child, scopeSpan, depth);
                    }
                    break;

                case StaticMethodCallArgumentSyntax staticCall:
                    foreach (var child in staticCall.Arguments)
                    {
                        CollectArgument(child, scopeSpan, depth);
                    }
                    break;

                case ArrayLiteralArgumentSyntax list:
                    foreach (var item in list.Items)
                    {
                        CollectArgument(item, scopeSpan, depth);
                    }
                    break;

                case TupleLiteralArgumentSyntax tuple:
                    foreach (var item in tuple.Items)
                    {
                        CollectArgument(item, scopeSpan, depth);
                    }
                    break;

                case SetLiteralArgumentSyntax set:
                    foreach (var item in set.Items)
                    {
                        CollectArgument(item, scopeSpan, depth);
                    }
                    break;

                case ComparisonPatternSyntax comparisonPattern:
                    CollectArgument(comparisonPattern.Operand, scopeSpan, depth);
                    break;

                case RecordLiteralArgumentSyntax record:
                    foreach (var entry in record.Fields)
                    {
                        if (entry is RecordFieldSyntax field)
                        {
                            CollectArgument(field.Value, scopeSpan, depth);
                        }
                        else if (entry is ComputedRecordFieldSyntax computed)
                        {
                            CollectArgument(computed.NameExpression, scopeSpan, depth);
                            CollectArgument(computed.Value, scopeSpan, depth);
                        }
                        else if (entry is SpreadRecordEntrySyntax spread)
                        {
                            CollectArgument(spread.Value, scopeSpan, depth);
                        }
                    }
                    break;

                case BlockArgumentSyntax blockArgument:
                    CollectBlock(blockArgument.Block, depth + 1);
                    break;

                case MemberAccessArgumentSyntax member:
                    CollectArgument(member.Target, scopeSpan, depth);
                    break;

                case MethodCallArgumentSyntax method:
                    CollectArgument(method.Target, scopeSpan, depth);
                    foreach (var child in method.Arguments)
                    {
                        CollectArgument(child, scopeSpan, depth);
                    }
                    break;

                case SubexpressionArgumentSyntax subexpression:
                    CollectPipeline(subexpression.Pipeline, scopeSpan, depth);
                    break;

                case OperatorArgumentSyntax operation:
                    CollectArgument(operation.Left, scopeSpan, depth);
                    CollectArgument(operation.Right, scopeSpan, depth);
                    break;

                case UnaryOperatorArgumentSyntax unary:
                    CollectArgument(unary.Operand, scopeSpan, depth);
                    break;

                case RangeArgumentSyntax range:
                    CollectArgument(range.Start, scopeSpan, depth);
                    if (range.Step is not null)
                    {
                        CollectArgument(range.Step, scopeSpan, depth);
                    }
                    if (range.End is not null)
                    {
                        CollectArgument(range.End, scopeSpan, depth);
                    }
                    break;
            }
        }

        /// <summary>
        /// Pulls the type-like and function declarations of a <c>require</c>d file into this
        /// index, so the editor knows about names the document did not declare itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The index was document-local, so a file built on `require "./lib/Point.tosh"` got no
        /// completion, no hover and no semantic-token colouring for anything that library
        /// declared — every name from it read as unknown. The REPL never had this gap, because
        /// its highlighter consults the live runtime; only the editor did (<c>TS-P3-12</c>).
        /// </para>
        /// <para>
        /// Bounded deliberately. This runs on every keystroke that rebuilds the index, so a
        /// require graph is followed to <see cref="MaxRequireDepth"/> and each document is parsed
        /// once per build. A missing or unreadable target is silently skipped: a half-typed path
        /// is the normal state of a file being edited, and an editor feature must not throw
        /// because a `require` does not resolve yet.
        /// </para>
        /// <para>
        /// Imported declarations are given the requiring statement's position as their scope
        /// start, so they become visible exactly where the `require` appears — the same rule the
        /// runtime follows, and it keeps "declared before use" honest for offsets above it.
        /// </para>
        /// </remarks>
        private void CollectRequiredDocument(
            RequireStatementSyntax require,
            TextSpan scopeSpan,
            int depth,
            string? baseDirectory)
        {
            // A native library exposes CLR types rather than ToastScript declarations, and there
            // is no source file to parse.
            if (require.IsNative || depth >= MaxRequireDepth)
            {
                return;
            }

            var path = ResolveRequirePath(require.Target, baseDirectory);

            if (path is null || !_visitedRequires.Add(path))
            {
                return;
            }

            string importedText;
            try
            {
                importedText = File.ReadAllText(path);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            ParseResult imported;
            try
            {
                imported = ToshParser.Parse(importedText, path);
            }
            catch (Exception)
            {
                // A library that does not parse yet is not a reason to lose the names this
                // document does declare.
                return;
            }

            var importScope = TextSpan.FromBounds(require.Span.Start, scopeSpan.End);

            CollectImportedDeclarations(
                imported.Statement,
                importScope,
                depth + 1,
                require.Span,
                Path.GetDirectoryName(path));
        }

        /// <summary>
        /// Walks a required document's top level, recording the names it publishes against the
        /// requiring document's own coordinates.
        /// </summary>
        /// <remarks>
        /// Only top-level type-like and function declarations are taken. Ranges point at the
        /// <c>require</c> statement rather than into the other file, because this index's
        /// coordinate map belongs to *this* document — a range from another file would send
        /// go-to-definition to a nonsensical offset. Cross-file navigation is its own feature.
        /// </remarks>
        private void CollectImportedDeclarations(
            StatementSyntax statement,
            TextSpan importScope,
            int depth,
            TextSpan requireSpan,
            string? baseDirectory)
        {
            switch (statement)
            {
                case ScriptStatementSyntax script:
                    foreach (var child in script.Statements)
                    {
                        CollectImportedDeclarations(child, importScope, depth, requireSpan, baseDirectory);
                    }
                    break;

                // A library requiring another library. The nested target resolves against *its*
                // directory, which is why the base is threaded rather than taken from the
                // document being edited.
                case RequireStatementSyntax nested:
                    CollectRequiredDocument(nested, importScope, depth, baseDirectory);
                    break;

                case ClassDefinitionStatementSyntax @class:
                    AddImported(@class.Name, DeclarationKind.Class, importScope, depth, requireSpan, @class.DocComment);
                    foreach (var member in @class.Members)
                    {
                        switch (member)
                        {
                            case ClassPropertyMemberSyntax prop:
                                AddImported(prop.Name, DeclarationKind.Property, importScope, depth + 1, requireSpan, prop.DocComment);
                                break;
                            case ClassMethodMemberSyntax method:
                                AddImported(method.Method.Name, DeclarationKind.ClassMethod, importScope, depth + 1, requireSpan, method.Method.DocComment);
                                break;
                        }
                    }
                    break;

                case RecordDefinitionStatementSyntax record:
                    AddImported(record.Name, DeclarationKind.Record, importScope, depth, requireSpan);
                    break;

                case EnumDefinitionStatementSyntax @enum:
                    AddImported(@enum.Name, DeclarationKind.Enum, importScope, depth, requireSpan);
                    break;

                case TypeAliasStatementSyntax alias:
                    AddImported(alias.Name, DeclarationKind.TypeAlias, importScope, depth, requireSpan);
                    break;

                case FunctionDefinitionStatementSyntax function:
                    AddImported(function.Name, DeclarationKind.Function, importScope, depth, requireSpan, function.DocComment);
                    break;

                // A trait imported from another file is a symbol like any other type
                // (`TS-P2-101`). This switch records imports; the declaring switch above
                // records the members.
                case TraitDefinitionStatementSyntax trait:
                    AddImported(trait.Name, DeclarationKind.Trait, importScope, depth, requireSpan);
                    break;

                case ModuleDefinitionStatementSyntax module:
                    AddImported(module.Name, DeclarationKind.Module, importScope, depth, requireSpan);

                    // A module's exports are reached as `Module.Name`, so the members themselves
                    // are not top-level names here — but nested modules and the types inside are
                    // what completion offers after the dot, and they still have to be indexed.
                    foreach (var child in module.Body.Statements)
                    {
                        CollectImportedDeclarations(child, importScope, depth, requireSpan, baseDirectory);
                    }
                    break;
            }
        }

        private void AddImported(
            string name,
            DeclarationKind kind,
            TextSpan importScope,
            int depth,
            TextSpan requireSpan,
            DocComment? docComment = null)
        {
            var range = _map.ToRange(requireSpan.Start, requireSpan.End);

            _declarations.Add(new IndexedDeclaration(
                name,
                kind,
                importScope.Start,
                importScope.End,
                requireSpan.Start,
                requireSpan.End,
                depth,
                requireSpan.Start,
                range,
                range,
                docComment,
                IsImported: true));
        }

        /// <summary>
        /// Turns a <c>require</c> target into a readable path, or <see langword="null"/> when it
        /// cannot be one.
        /// </summary>
        /// <remarks>
        /// Relative targets resolve against the requiring document's directory, which is the rule
        /// the runtime uses. A bare module name — `require ToastLib.Math` — names something the
        /// runtime finds on its own search path, which the language server does not have; those
        /// are skipped rather than guessed at.
        /// </remarks>
        private string? ResolveRequirePath(string target, string? baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            var trimmed = target.Trim('"', '\'', ' ');

            if (trimmed.Length == 0 || !trimmed.EndsWith(".tosh", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                if (Path.IsPathRooted(trimmed))
                {
                    return File.Exists(trimmed) ? Path.GetFullPath(trimmed) : null;
                }

                var directory = baseDirectory;

                if (string.IsNullOrEmpty(directory))
                {
                    return null;
                }

                var combined = Path.GetFullPath(Path.Combine(directory, trimmed));
                return File.Exists(combined) ? combined : null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private void AddDeclaration(string name, DeclarationKind kind, TextSpan declarationSpan, TextSpan scopeSpan, int depth, DocComment? docComment = null)
        {
            var selectionSpan = FindIdentifierSpan(name, declarationSpan);
            _declarations.Add(new IndexedDeclaration(
                name,
                kind,
                scopeSpan.Start,
                scopeSpan.End,
                declarationSpan.Start,
                declarationSpan.End,
                depth,
                selectionSpan.Start,
                _map.ToRange(declarationSpan.Start, declarationSpan.End),
                _map.ToRange(selectionSpan.Start, selectionSpan.End),
                docComment));
        }

        private void AddFunctionDeclaration(FunctionDefinitionStatementSyntax function, TextSpan scopeSpan, int depth)
        {
            var selectionSpan = FindIdentifierSpan(function.Name, function.Span);
            _functions.Add(new IndexedFunctionDeclaration(
                function.Name,
                function.Parameters,
                function.ReturnTypeName,
                function.IsCommandWrapper,
                scopeSpan.Start,
                scopeSpan.End,
                depth,
                selectionSpan.Start,
                _map.ToRange(function.Span.Start, function.Span.End),
                _map.ToRange(selectionSpan.Start, selectionSpan.End),
                function.DocComment));
        }

        private TextSpan FindIdentifierSpan(string name, TextSpan searchSpan)
        {
            if (searchSpan.IsEmpty || searchSpan.Start >= _text.Length)
            {
                return searchSpan;
            }

            var boundedStart = Math.Clamp(searchSpan.Start, 0, _text.Length);
            var boundedEnd = Math.Clamp(searchSpan.End, boundedStart, _text.Length);
            var length = boundedEnd - boundedStart;
            var segment = _text.Substring(boundedStart, length);
            var match = Regex.Match(segment, $@"(?<![A-Za-z0-9_]){Regex.Escape(name)}(?![A-Za-z0-9_])");

            return match.Success
                ? TextSpan.FromBounds(boundedStart + match.Index, boundedStart + match.Index + match.Length)
                : searchSpan;
        }
    }
}
