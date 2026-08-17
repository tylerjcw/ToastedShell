using System.Text;
using System.Globalization;
using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public static partial class ToshParser
{
    /// <summary>
    /// The single source of truth for "can a statement or expression
    /// begin with this token" (TS-P2-06). Statement-boundary detection
    /// previously carried its own shorter list that omitted interpolated
    /// strings, command substitution, process substitution, and function
    /// references, so a line starting with one of those was not always
    /// recognised as a new statement. Structural decisions should consult
    /// this rather than re-enumerating token kinds.
    /// </summary>
    internal static bool IsExpressionStartToken(SyntaxTokenKind kind)
    {
        return kind switch
        {
            SyntaxTokenKind.String or
            SyntaxTokenKind.Number or
            SyntaxTokenKind.Boolean or
            SyntaxTokenKind.Null or
            SyntaxTokenKind.UnitLiteral or
            SyntaxTokenKind.InterpolatedString or
            SyntaxTokenKind.Bareword or
            SyntaxTokenKind.OpenBrace or
            SyntaxTokenKind.OpenBraceColon or
            SyntaxTokenKind.OpenBracePipe or
            SyntaxTokenKind.OpenBracePercent or
            SyntaxTokenKind.OpenParen or
            SyntaxTokenKind.OpenBracket or
            SyntaxTokenKind.DollarOpenParen or
            SyntaxTokenKind.LessThanOpenParen or
            SyntaxTokenKind.Ampersand => true,
            _ => false,
        };
    }

    /// <summary>
    /// Shared command/function-reference name predicate. Structural passes
    /// use this same rule so an adjacent <c>&amp;name</c> is not classified
    /// differently from the recursive parser.
    /// </summary>
    /// <summary>
    /// The name predicate for <c>&amp;name</c>, which admits a dotted path where a
    /// command name does not.
    /// </summary>
    /// <remarks>
    /// `TS-P2-94`. A command name may not contain a dot, and <c>&amp;</c> reused
    /// that rule — so <c>&amp;M.Exported</c> and <c>&amp;C.Static</c> failed the
    /// guard, fell through, and were reported as a stray background operator. The
    /// rule is relaxed here only, because a dotted *command* name is still not a
    /// thing; each segment must be a valid name on its own.
    /// </remarks>
    internal static bool IsValidFunctionReferenceName(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var segment in text.Split('.'))
        {
            if (!IsValidCommandName(segment))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsValidCommandName(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!(char.IsLetter(text[0]) || text[0] == '_'))
        {
            return false;
        }

        for (var index = 1; index < text.Length; index++)
        {
            var character = text[index];

            if (character == '-')
            {
                // No trailing hyphen, no consecutive hyphens.
                if (index == text.Length - 1 || text[index + 1] == '-')
                {
                    return false;
                }

                continue;
            }

            if (!(char.IsLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <param name="context">
    /// What the host knows about commands, modules, and types
    /// (TS-P2-23). Omitting it parses purely syntactically, which is what
    /// the formatter, the REPL continuation classifier, and
    /// interpolation-hole parsing want.
    /// </param>
    public static ParseResult Parse(
        string source,
        string sourceName = "<input>",
        ParseContext? context = null)
    {
        try
        {
            var sourceText = source ?? string.Empty;
            var lexer = new ToshLexer(sourceText);
            var tokens = lexer.Lex();
            var parser = new InternalParser(
                sourceName,
                sourceText,
                tokens,
                lexer.LineHushDirectives,
                lexer.LineComments,
                context ?? ParseContext.Empty);
            return parser.Parse();
        }
        catch (ToshLexer.LexerDiagnosticException exception)
        {
            return new ParseResult(
                sourceName,
                source ?? string.Empty,
                new PipelineStatementSyntax(
                    new PipelineSyntax(Array.Empty<PipelineStageSyntax>()),
                    new TextSpan(0, 0)),
                [exception.Diagnostic]);
        }
    }

    private sealed partial class InternalParser
    {
        private enum PendingPipelineSeparator
        {
            None,
            Pipe,
            PipeForward,
        }

        private readonly string _sourceName;
        private readonly string _sourceText;
        private readonly IReadOnlyList<SyntaxToken> _tokens;
        private readonly IReadOnlyList<LineHushDirective> _lineHushDirectives;
        private readonly IReadOnlyList<LineComment> _lineComments;
        private readonly List<SyntaxDiagnostic> _diagnostics = [];
        private readonly HashSet<string> _userFunctionNames;
        private readonly IReadOnlyDictionary<int, LiteBoundary> _liteBoundariesByTokenIndex;
        private readonly HashSet<int> _liteTopLevelStatementStartTokenIndices;
        private readonly IReadOnlyDictionary<int, LiteSeparatorKind> _liteTopLevelSeparatorsByEndTokenIndex;
        private readonly Stack<int> _elementBoundaryOwnerTokenIndices = [];

        /// <summary>
        /// Registers a brace as the owner of the boundaries inside it, so
        /// <see cref="HasElementBoundaryAfter"/> can consult the structural
        /// pass instead of re-deriving from line breaks (<c>TS-P2-24</c>).
        /// </summary>
        /// <remarks>
        /// Every brace-delimited member list whose members separate by newline
        /// needs one: blocks, class bodies, match arms, and native bind blocks.
        /// A list that registers no owner silently falls back to the line-break
        /// heuristic, which is how class bodies were missed.
        /// </remarks>
        private BoundaryOwnerScope PushBoundaryOwner(int openBraceTokenIndex)
        {
            _elementBoundaryOwnerTokenIndices.Push(openBraceTokenIndex);
            return new BoundaryOwnerScope(_elementBoundaryOwnerTokenIndices);
        }

        private readonly struct BoundaryOwnerScope : IDisposable
        {
            private readonly Stack<int> _owners;

            public BoundaryOwnerScope(Stack<int> owners) => _owners = owners;

            public void Dispose() => _owners.Pop();
        }

        /// <summary>
        /// Names declared as modules in this source (TS-P2-23). Lets the
        /// parser decide `Geo.area` from a fact rather than from
        /// capitalization: a declared module is module dispatch whatever
        /// its casing, and the spelling heuristic is only consulted for
        /// names this source never declares.
        /// </summary>
        private readonly HashSet<string> _declaredModuleNames;
        private readonly ParseContext _context;

        /// <summary>
        /// Frame openers that enclose at least one <c>|</c> they own, from the
        /// structural pass. Replaces the token re-scan
        /// <c>HasTopLevelPipeBeforeCloseParen</c> used to perform (<c>TS-P2-24</c>).
        /// </summary>
        private readonly HashSet<int> _stageDivisionOwnerTokenIndices;

        /// <summary>
        /// Token span start to token index. The two call sites hold the opening
        /// token rather than its index, and spans are unique and ordered, so this
        /// resolves one to the other without changing their signatures.
        /// </summary>
        private readonly Dictionary<int, int> _tokenIndexBySpanStart;

        private int _position;
        private bool _isParsingTopLevelStatement;
        private bool _stopRefinementAtEquals;

        public InternalParser(
            string sourceName,
            string sourceText,
            IReadOnlyList<SyntaxToken> tokens,
            IReadOnlyList<LineHushDirective> lineHushDirectives,
            IReadOnlyList<LineComment> lineComments,
            ParseContext context)
        {
            _context = context;
            _sourceName = sourceName;
            _sourceText = sourceText;
            _tokens = tokens;
            _lineHushDirectives = lineHushDirectives;
            _lineComments = lineComments;
            _liteBoundariesByTokenIndex = LiteParser
                .CandidateBoundaries(tokens, sourceText, out var liteStageDivisions)
                .ToDictionary(boundary => boundary.TokenIndex);
            _stageDivisionOwnerTokenIndices = liteStageDivisions
                .Where(division => division.OwnerOpenTokenIndex is not null)
                .Select(division => division.OwnerOpenTokenIndex!.Value)
                .ToHashSet();
            _tokenIndexBySpanStart = new Dictionary<int, int>(tokens.Count);
            for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                _tokenIndexBySpanStart.TryAdd(tokens[tokenIndex].Span.Start, tokenIndex);
            }
            var liteScript = LiteParser.Parse(tokens, sourceText);
            _liteTopLevelStatementStartTokenIndices = liteScript
                .Statements
                .Where(statement => statement.StartIndex >= 0)
                .Select(statement => statement.StartIndex)
                .ToHashSet();
            _liteTopLevelSeparatorsByEndTokenIndex = liteScript
                .Statements
                .SelectMany(statement => statement.Stages)
                .ToDictionary(stage => stage.EndIndex, stage => stage.Separator);
            var declarations = ScanDeclarations(tokens, sourceText);
            _userFunctionNames = declarations.Functions;
            _declaredModuleNames = declarations.Modules;
        }

        /// <summary>
        /// Collects the names this source declares, so later parse
        /// decisions consult a table instead of guessing from spelling
        /// (TS-P2-23).
        ///
        /// A declaration keyword only counts at a statement start, which
        /// is what keeps unrelated text from poisoning the scan
        /// (TS-P2-08): in <c>echo func bar</c> the word <c>func</c> is an
        /// argument, so <c>bar</c> is not registered as a function.
        /// </summary>
        private static (HashSet<string> Functions, HashSet<string> Modules) ScanDeclarations(
            IReadOnlyList<SyntaxToken> tokens,
            string sourceText)
        {
            var functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < tokens.Count - 1; i++)
            {
                var token = tokens[i];
                if (token.Kind != SyntaxTokenKind.Bareword)
                {
                    continue;
                }

                var isFunction = string.Equals(token.Text, "func", StringComparison.OrdinalIgnoreCase);
                var isModule = string.Equals(token.Text, "module", StringComparison.OrdinalIgnoreCase);
                if (!isFunction && !isModule)
                {
                    continue;
                }

                if (!IsAtDeclarationStart(tokens, i, sourceText))
                {
                    continue;
                }

                var next = tokens[i + 1];
                if (next.Kind != SyntaxTokenKind.Bareword || string.IsNullOrEmpty(next.Text))
                {
                    continue;
                }

                (isFunction ? functions : modules).Add(next.Text);
            }

            return (functions, modules);
        }

        /// <summary>
        /// True when the token at <paramref name="index"/> begins a
        /// statement: it is first in the source, or follows a separator,
        /// a block brace, a pipe, or a line break. Modifiers that may
        /// precede a declaration (<c>export</c>, <c>shy</c>, and friends)
        /// are skipped so <c>export func f()</c> still registers.
        /// </summary>
        private static bool IsAtDeclarationStart(
            IReadOnlyList<SyntaxToken> tokens,
            int index,
            string sourceText)
        {
            var cursor = index - 1;

            while (cursor >= 0 &&
                   tokens[cursor].Kind == SyntaxTokenKind.Bareword &&
                   IsDeclarationModifierWord(tokens[cursor].Text))
            {
                cursor--;
            }

            if (cursor < 0)
            {
                return true;
            }

            var previous = tokens[cursor];
            if (previous.Kind is SyntaxTokenKind.Semicolon
                or SyntaxTokenKind.OpenBrace
                or SyntaxTokenKind.CloseBrace
                or SyntaxTokenKind.Pipe)
            {
                return true;
            }

            var gapStart = Math.Min(previous.Span.End, sourceText.Length);
            var gapEnd = Math.Min(tokens[index].Span.Start, sourceText.Length);
            return gapEnd > gapStart
                && sourceText.AsSpan(gapStart, gapEnd - gapStart).IndexOf('\n') >= 0;
        }

        private static bool IsDeclarationModifierWord(string text) => text is
            "export" or "shy" or "global" or "local" or "shared" or "static"
            or "sealed" or "hollow" or "hermit" or "strict" or "partial"
            or "abstract" or "private" or "public" or "proud";

        public ParseResult Parse()
        {
            var statement = ParseScript();
            return new ParseResult(_sourceName, _sourceText, statement, _diagnostics, _lineHushDirectives, _lineComments);
        }

        private StatementSyntax ParseScript()
        {
            var statements = new List<StatementSyntax>();

            // Capture a free-floating leading doc-comment block as the
            // script's own documentation: the run of `##` lines at the
            // very top of the file that is followed by at least one
            // blank line before the first real statement. This lets a
            // file-level `## @summary ...` block describe the whole
            // script (surfaced in `--help` and via the LSP).
            DocComment? scriptDoc = null;
            if (Current.Kind == SyntaxTokenKind.DocComment)
            {
                // Walk the contiguous block: stop at the first gap that
                // contains a blank line, so we don't merge a file-level
                // doc-block with the doc-block of the next declaration.
                var lookahead = _position;
                while (lookahead < _tokens.Count && _tokens[lookahead].Kind == SyntaxTokenKind.DocComment)
                {
                    if (lookahead + 1 < _tokens.Count
                        && _tokens[lookahead + 1].Kind == SyntaxTokenKind.DocComment
                        && CountNewlinesBetween(_tokens[lookahead].Span.End, _tokens[lookahead + 1].Span.Start) >= 2)
                    {
                        lookahead++;
                        break;
                    }
                    lookahead++;
                }

                var lastDocEnd = _tokens[lookahead - 1].Span.End;
                var nextStart = lookahead < _tokens.Count ? _tokens[lookahead].Span.Start : _sourceText.Length;
                var trailingNewlines = CountNewlinesBetween(lastDocEnd, nextStart);

                // Only adopt as a script-level doc when the block ends
                // with a blank line (or hits EOF) — otherwise the block
                // belongs to whatever declaration follows it.
                if (trailingNewlines >= 2 || lookahead >= _tokens.Count - 1)
                {
                    var docTokens = new List<SyntaxToken>(lookahead - _position);
                    while (_position < lookahead)
                        docTokens.Add(NextToken());
                    scriptDoc = DocComment.Parse(docTokens);
                }
            }

            while (Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                var positionBeforeStatement = _position;
                StatementSyntax statement;
                _isParsingTopLevelStatement = true;
                try
                {
                    statement = ParseStatement(stopAtSemicolon: true);
                }
                finally
                {
                    _isParsingTopLevelStatement = false;
                }

                statements.Add(statement);

                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (IsExplicitBackgroundStatementBoundary(statements[^1]))
                {
                    continue;
                }

                if (_position != positionBeforeStatement &&
                    IsCurrentTopLevelLiteStatementStart())
                {
                    continue;
                }

                if (Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_statement_separator",
                        Title: "Top-level statements must be separated by a newline or ';'.",
                        Span: Current.Span,
                        Label: "insert a newline or ';' between statements"));
                    // Only skip when the statement parse made no
                    // progress, which is what guarantees termination.
                    // Skipping unconditionally discarded whatever
                    // followed on the same line: for
                    // `func f() {…} func g() {…}` the scan ran past `g`
                    // and the declaration was lost from the tree. When
                    // the parser has advanced it is already positioned at
                    // the next construct, so continuing recovers it.
                    if (_position == positionBeforeStatement)
                    {
                        SkipToStageBoundary(untilCloseParen: false, untilCloseBrace: false, untilSemicolon: false);
                    }

                    if (Current.Kind == SyntaxTokenKind.Semicolon)
                    {
                        NextToken();
                    }
                }
            }

            return statements.Count switch
            {
                0 => new ScriptStatementSyntax(Array.Empty<StatementSyntax>(), new TextSpan(0, 0), scriptDoc),
                1 when statements[0] is ScriptInputStatementSyntax or SubcommandStatementSyntax => new ScriptStatementSyntax(
                    statements,
                    statements[0].Span,
                    scriptDoc),
                1 when scriptDoc is not null => new ScriptStatementSyntax(
                    statements,
                    statements[0].Span,
                    scriptDoc),
                1 => statements[0],
                _ => new ScriptStatementSyntax(
                    statements,
                    TextSpan.FromBounds(statements[0].Span.Start, statements[^1].Span.End),
                    scriptDoc),
            };
        }

        private StatementSyntax ParseDestructuringDeclaration(
            int declarationStart,
            DeclarationModifier modifier,
            bool isConst,
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            DestructuringPatternSyntax pattern;
            var patternStart = Current.Span.Start;

            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                NextToken(); // consume {
                var names = new List<string>();

                while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
                {
                    if (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken();
                        continue;
                    }

                    if (Current.Kind == SyntaxTokenKind.Bareword && IsValidIdentifier(Current.Text))
                    {
                        names.Add(NextToken().Text);
                    }
                    else
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.expected_destructuring_name",
                            Title: "Expected a variable name in the destructuring pattern.",
                            Span: Current.Span,
                            Label: "write a variable name like 'name' or 'size'"));
                        NextToken();
                    }

                    if (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken();
                    }
                }

                var closeSpan = Current.Span;
                if (Current.Kind == SyntaxTokenKind.CloseBrace)
                {
                    NextToken();
                }

                pattern = new RecordDestructuringPatternSyntax(names, TextSpan.FromBounds(patternStart, closeSpan.End));
            }
            else // OpenBracket or OpenParen — both positional
            {
                var closer = Current.Kind == SyntaxTokenKind.OpenParen
                    ? SyntaxTokenKind.CloseParen
                    : SyntaxTokenKind.CloseBracket;

                NextToken(); // consume [ or (
                var names = new List<string>();

                while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != closer)
                {
                    if (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken();
                        continue;
                    }

                    if (Current.Kind == SyntaxTokenKind.Bareword && IsValidIdentifier(Current.Text))
                    {
                        names.Add(NextToken().Text);
                    }
                    else
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.expected_destructuring_name",
                            Title: "Expected a variable name in the destructuring pattern.",
                            Span: Current.Span,
                            Label: "write a variable name like 'a' or 'first'"));
                        NextToken();
                    }

                    if (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken();
                    }
                }

                var closeSpan = Current.Span;
                if (Current.Kind == closer)
                {
                    NextToken();
                }

                pattern = new ArrayDestructuringPatternSyntax(names, TextSpan.FromBounds(patternStart, closeSpan.End));
            }

            var equalsToken = ExpectEqualsToken("Destructuring declarations use '=' after the pattern.");
            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, equalsToken.Span.End);

            return new DestructuringDeclarationStatementSyntax(
                pattern,
                value,
                modifier,
                isConst,
                TextSpan.FromBounds(declarationStart, end));
        }

        private ArgumentSyntax? MergeRefinementSpecifications(params ArgumentSyntax?[] refinements)
        {
            var clauses = new List<RefinementDefinitionClauseSyntax>();

            foreach (var refinement in refinements)
            {
                AppendRefinementClauses(clauses, refinement);
            }

            if (clauses.Count == 0)
            {
                return null;
            }

            return new RefinementClauseArgumentSyntax(
                clauses,
                TextSpan.FromBounds(clauses[0].Span.Start, clauses[^1].Span.End));
        }

        private static void AppendRefinementClauses(List<RefinementDefinitionClauseSyntax> clauses, ArgumentSyntax? refinement)
        {
            switch (refinement)
            {
                case null:
                    return;
                case RefinementClauseArgumentSyntax specification:
                    clauses.AddRange(specification.Clauses);
                    return;
                default:
                    clauses.Add(new RefinementWhereClauseSyntax(refinement, refinement.Span));
                    return;
            }
        }

        private IReadOnlyList<NativeFunctionBindingSyntax> ParseNativeBindingBlock()
        {
            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_bind_body",
                    Title: "Bind statements require a body.",
                    Span: Current.Span,
                    Label: "write '{ ... }' after the bind target"));
                return Array.Empty<NativeFunctionBindingSyntax>();
            }

            var openBraceTokenIndex = _position;
            var openBrace = NextToken();
            using var boundaryOwner = PushBoundaryOwner(openBraceTokenIndex);
            var functions = new List<NativeFunctionBindingSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                var function = ParseNativeBindingFunction();
                functions.Add(function);

                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (IsAtElementBoundary())
                {
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_bind_member_separator",
                        Title: "Bound functions must be separated by a newline or ';'.",
                        Span: Current.Span,
                        Label: "insert a newline or ';' between bound functions"));
                    SkipToBlockBoundary();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this bind block never closes"));
            }

            return functions;
        }

        private NativeFunctionBindingSyntax ParseNativeBindingFunction()
        {
            var memberStart = Current.Span.Start;

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "native", StringComparison.OrdinalIgnoreCase))
            {
                NextToken();
            }

            if (!(Current.Kind == SyntaxTokenKind.Bareword &&
                  string.Equals(Current.Text, "func", StringComparison.OrdinalIgnoreCase)))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_bind_function",
                    Title: "Bind blocks only support function bindings.",
                    Span: Current.Span,
                    Label: "write 'func name(...) -> type' here"));

                if (Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    NextToken();
                }

                return new NativeFunctionBindingSyntax(string.Empty, string.Empty, Array.Empty<NativeFunctionParameterSyntax>(), null, null, new TextSpan(memberStart, 0));
            }

            NextToken();
            var nameToken = ExpectVariableName();
            var parameters = ParseNativeBindingParameters();
            var returnTypeName = ParseNativeReturnAnnotation();
            var symbolName = nameToken.Text;
            string? callingConventionName = null;
            string? nativeTarget = null;

            // `from "lib"` — the standalone `raw func` form, for a single binding
            // that does not justify a whole block.
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "from", StringComparison.OrdinalIgnoreCase))
            {
                NextToken();

                if (Current.Kind is SyntaxTokenKind.Bareword or SyntaxTokenKind.String)
                {
                    nativeTarget = NextToken().Text.Trim('"');
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_native_library",
                        Title: "A raw function needs a library after 'from'.",
                        Span: Current.Span,
                        Label: "write something like 'from \"libc.so.6\"'"));
                }
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "as", StringComparison.OrdinalIgnoreCase))
            {
                NextToken();

                if (Current.Kind is SyntaxTokenKind.Bareword or SyntaxTokenKind.String)
                {
                    symbolName = NextToken().Text.Trim('"');
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_native_symbol_name",
                        Title: "Native function bindings need a symbol name after 'as'.",
                        Span: Current.Span,
                        Label: "write a native export name here"));
                }
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "callconv", StringComparison.OrdinalIgnoreCase))
            {
                NextToken();

                if (Current.Kind is SyntaxTokenKind.Bareword or SyntaxTokenKind.String)
                {
                    callingConventionName = NextToken().Text.Trim('"');
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_native_calling_convention",
                        Title: "Native function bindings need a calling convention name after 'callconv'.",
                        Span: Current.Span,
                        Label: "write something like 'cdecl' or 'stdcall' here"));
                }
            }

            // `where (…)` is the success contract. It reuses the refinement
            // clause verbatim — same keyword, same `_` placeholder, same meaning
            // — with `_` bound to the native return value.
            var successPredicate = TryParseRefinementClause();

            var end = Peek(-1).Span.End;

            return new NativeFunctionBindingSyntax(
                nameToken.Text,
                symbolName,
                parameters,
                returnTypeName,
                callingConventionName,
                TextSpan.FromBounds(memberStart, Math.Max(end, nameToken.Span.End)),
                successPredicate,
                nativeTarget);
        }

        /// <summary>
        /// Consumes a <c>[n]</c> capacity suffix on a native parameter type, so
        /// <c>buffer[256]</c> and <c>double[3]</c> survive as one type name.
        /// Kept separate from <see cref="ParseTypeNameSuffix"/> because a bracket
        /// suffix means a fixed inline capacity here, not a CLR array type.
        /// </summary>
        private string? ParseNativeBufferSuffix(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName) || Current.Kind != SyntaxTokenKind.OpenBracket)
            {
                return typeName;
            }

            NextToken(); // consume '['

            if (Current.Kind is not (SyntaxTokenKind.Number or SyntaxTokenKind.Bareword) ||
                !int.TryParse(Current.Text, out var count) ||
                count <= 0)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.native_buffer_requires_length",
                    Title: $"'{typeName}' needs a positive capacity.",
                    Span: Current.Span,
                    Label: "write something like 'buffer[256]'"));
                return typeName;
            }

            NextToken(); // consume the count

            if (Current.Kind == SyntaxTokenKind.CloseBracket)
            {
                NextToken();
            }

            return $"{typeName}[{count}]";
        }

        private IReadOnlyList<RequireImportSyntax> ParseRequireImportList()
        {
            var openBrace = NextToken();
            var imports = new List<RequireImportSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                var nameToken = ExpectVariableName();
                var alias = TryParseRequireAlias(out var aliasToken);
                imports.Add(new RequireImportSyntax(nameToken.Text, alias, TextSpan.FromBounds(nameToken.Span.Start, aliasToken?.Span.End ?? nameToken.Span.End)));

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this require import list never closes",
                    Help: "close the import list with '}' after the last name."));
                return imports;
            }

            NextToken();
            return imports;
        }

        private string? TryParseRequireAlias(out SyntaxToken? aliasToken)
        {
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                (string.Equals(Current.Text, "as", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Current.Text, "=", StringComparison.Ordinal)))
            {
                NextToken();
                aliasToken = ExpectVariableName();
                return aliasToken.Text;
            }

            aliasToken = null;
            return null;
        }

        private SyntaxToken ExpectRequireTarget()
        {
            if (Current.Kind is SyntaxTokenKind.Bareword or SyntaxTokenKind.String)
            {
                return NextToken();
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_require_target",
                Title: "Require statements need a ToSh file, module, assembly, or project path.",
                Span: Current.Span,
                Label: "write something like 'require Inventory from \"./inventory.tosh\"'"));
            return Current;
        }

        private static string GetRequireTargetText(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.String && token.Value is string literalValue
                ? literalValue
                : token.Text;
        }

        private static string GetDefaultNativeBindModuleName(string target)
        {
            var fileName = Path.GetFileNameWithoutExtension(target);
            var candidate = string.IsNullOrWhiteSpace(fileName) ? target : fileName;
            var sanitized = new StringBuilder(candidate.Length);

            foreach (var ch in candidate)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_')
                {
                    sanitized.Append(ch);
                }
            }

            return sanitized.Length == 0 ? "Native" : sanitized.ToString();
        }

        /// <summary>
        /// Finds a <c>yield</c> that belongs to a deferred block, if there is one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// `TS-P2-58`. A <c>yield</c> inside <c>defer</c> was evaluated and its value discarded:
        /// <c>func g() { defer { yield 9 }\n yield 1 }</c> produced only <c>1</c>, and the
        /// generator terminated cleanly, so nothing reported the loss. The deferred block itself
        /// ran — the side effects landed — which made the silence look like correct behaviour.
        /// </para>
        /// <para>
        /// Refused rather than delivered, per the decision recorded for the item: a deferred block
        /// runs while the function unwinds, after a consumer may have stopped pulling, so there is
        /// no stream for it to join and no ordering that could be written down honestly. Refusing
        /// at parse time costs nothing at runtime and cannot be got wrong.
        /// </para>
        /// <para>
        /// A <c>yield</c> inside a function *declared* in the deferred block belongs to that
        /// function, not to the defer, so the walk stops at a nested declaration.
        /// </para>
        /// </remarks>
        private static TextSpan? FindYieldInDeferredBlock(BlockSyntax block)
        {
            foreach (var statement in block.Statements)
            {
                if (FindYieldInDeferredStatement(statement) is { } span)
                {
                    return span;
                }
            }

            return null;
        }

        private MatchArmSyntax ParseMatchArm(bool implicitCurrentItem)
        {
            var armStart = Current.Span.Start;
            var isWildcard = false;
            ArgumentSyntax? pattern = null;

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "default", StringComparison.OrdinalIgnoreCase))
            {
                isWildcard = true;
                NextToken();
            }
            else
            {
                // Only require _ prefix before comparison/type-check patterns
                // (is, >, >=, <, <=, =~) to disambiguate from redirections.
                // Plain value arms like "gz" => ... or 42 => ... do not need it.
                if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == "_")
                {
                    // Check if this is `_ =>` (wildcard shorthand) — suggest `default` instead.
                    if (IsFatArrow(Peek(1)))
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.match_default_keyword_required",
                            Title: "Use 'default' instead of '_' for the wildcard arm.",
                            Span: Current.Span,
                            Label: "replace '_' with 'default'",
                            Help: "In tosh, the wildcard match arm uses the 'default' keyword, not '_'."));
                        NextToken(); // consume _
                        // pattern stays null — this is a wildcard arm
                        isWildcard = true;
                    }
                    else
                    {
                        NextToken(); // consume _ as pattern prefix
                        pattern = TryParsePatternExpression()
                                  ?? ParseArgument(implicitCurrentItem: implicitCurrentItem)
                                  ?? new BarewordArgumentSyntax(string.Empty, Current.Span);
                    }
                }
                else if (IsPatternExpressionStart())
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_match_arm_underscore",
                        Title: "Match arms must start with '_'",
                        Span: Current.Span,
                        Label: "write '_' before comparison or type-check patterns",
                        Help: "To disambiguate patterns from redirections, write '_ pattern => ...' for arms using is, >, >=, <, <=, or =~."));
                    pattern = TryParsePatternExpression()
                              ?? ParseArgument(implicitCurrentItem: implicitCurrentItem)
                              ?? new BarewordArgumentSyntax(string.Empty, Current.Span);
                }
                else
                {
                    pattern = ParseArgument(implicitCurrentItem: implicitCurrentItem)
                              ?? new BarewordArgumentSyntax(string.Empty, Current.Span);
                }
            }

            ArgumentSyntax? guard = null;
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "if", StringComparison.OrdinalIgnoreCase))
            {
                var ifToken = NextToken();

                if (Current.Kind == SyntaxTokenKind.OpenParen)
                {
                    guard = ParseParenthesizedArgument(implicitCurrentItem);
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_match_guard_condition",
                        Title: "Match guards require a parenthesized condition.",
                        Span: ifToken.Span,
                        Label: "write `if (<condition>)` before `=>`"));
                }
            }

            if (!IsFatArrow(Current))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_match_arm_arrow",
                    Title: "Match arms require `=>` between the pattern and result.",
                    Span: Current.Span,
                    Label: "write `=>` here"));

                var missingBody = new MatchArmPipelineBodySyntax(new PipelineSyntax(Array.Empty<PipelineStageSyntax>()), Current.Span);
                return new MatchArmSyntax(
                    pattern,
                    guard,
                    missingBody,
                    isWildcard,
                    TextSpan.FromBounds(armStart, missingBody.Span.End));
            }

            var arrowStart = Current.Span.Start;
            ConsumeFatArrow();

            MatchArmBodySyntax body;
            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                var block = ParseBlock();
                body = new MatchArmBlockBodySyntax(block, block.Span);
            }
            else if (Current.Kind == SyntaxTokenKind.Bareword &&
                     IsJumpStatementKeyword(Current.Text))
            {
                // Allow jump statements (`throw`, `return`, `yield`, `break`,
                // `continue`) to appear as the head of a match-arm body. Without
                // this branch the pipeline parser sees `throw` as a bareword
                // command name and emits a "Command 'throw' was not found" error.
                var jumpStart = Current.Span.Start;
                var jump = ParseJumpStatementForArm();
                var wrapped = new BlockSyntax(new[] { jump }, TextSpan.FromBounds(jumpStart, jump.Span.End));
                body = new MatchArmBlockBodySyntax(wrapped, wrapped.Span);
            }
            else
            {
                var pipeline = ParsePipeline(
                    untilCloseParen: false,
                    untilCloseBrace: true,
                    untilSemicolon: true,
                    allowExpressionStart: true);
                var span = GetPipelineSpan(pipeline, new TextSpan(arrowStart, 0));
                body = new MatchArmPipelineBodySyntax(pipeline, span);
            }

            return new MatchArmSyntax(
                pattern,
                guard,
                body,
                isWildcard,
                TextSpan.FromBounds(armStart, body.Span.End));
        }

        /// <summary>
        /// If the statement is followed by <c>if &lt;cond&gt;</c> or
        /// <c>unless &lt;cond&gt;</c> (no leading <c>|</c>, no statement
        /// boundary), wraps it in an <see cref="IfStatementSyntax"/>.
        /// For <c>unless</c>, the inner statement goes in the else branch
        /// so the original condition expression is preserved verbatim.
        /// </summary>
        private StatementSyntax TryWrapPostfixConditional(StatementSyntax inner)
        {
            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                return inner;
            }

            var isIf = string.Equals(Current.Text, "if", StringComparison.OrdinalIgnoreCase);
            var isUnless = string.Equals(Current.Text, "unless", StringComparison.OrdinalIgnoreCase);

            if (!isIf && !isUnless)
            {
                return inner;
            }

            if (IsAtElementBoundary())
            {
                return inner;
            }

            var keyword = NextToken();

            // `TS-P2-19`. The condition is a full expression. It used to be a single
            // argument, so `return "big" if $x > 5` stopped after `$x` and the parser met
            // `>` where it expected the end of the statement — reported as "Block
            // statements must be separated by a newline or ';'", a message about
            // separators for a defect in the condition. The specification's advice that
            // "non-trivial conditions should be parenthesised" described that limit rather
            // than a design, and is dropped with it.
            //
            // Asking first whether anything can start an expression keeps the documented
            // `expected_postfix_condition` for an omitted condition: parsing straight into
            // `}` produced a generic `unexpected_token` ahead of it.
            var condition = IsExpressionStartToken(Current.Kind)
                ? ParseOperatorExpression(Current.Span.Start, implicitCurrentItem: false)
                : null;

            if (condition is null)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_postfix_condition",
                    Title: $"Postfix '{keyword.Text}' requires a condition.",
                    Span: keyword.Span,
                    Label: $"write a condition after '{keyword.Text}'"));
                return inner;
            }

            var innerBlock = new BlockSyntax([inner], inner.Span);
            var emptyBlock = new BlockSyntax(Array.Empty<StatementSyntax>(), keyword.Span);
            var span = TextSpan.FromBounds(inner.Span.Start, condition.Span.End);

            return isIf
                ? new IfStatementSyntax(condition, innerBlock, null, span)
                : new IfStatementSyntax(condition, emptyBlock, innerBlock, span);
        }

        /// <summary>
        /// A modifier that may precede <c>subcommand</c> (<c>TS-P2-23</c>).
        /// </summary>
        /// <remarks>
        /// Asked of <c>LanguageSurface</c> rather than spelled out here. The literal list
        /// this replaces — <c>eager</c>, <c>hidden</c>, <c>hollow</c>, <c>vital</c>,
        /// <c>default</c> — was one of the copies that made <c>TS-P2-10</c> necessary:
        /// the registry gained a <c>SubcommandModifier</c> kind precisely because
        /// <c>eager</c> and <c>hidden</c> exist nowhere else, and keeping a second list
        /// here is how the two would drift apart again.
        /// </remarks>
        private static bool IsSubcommandModifierKeyword(string text) =>
            LanguageSurface.SubcommandModifiers.Contains(text);

        private static bool IsSubcommandKeyword(string text) =>
            text is "subcommand" or "subcmd";

        private bool TryReadRawStructInteger(out int value)
        {
            value = 0;
            var text = Current.Text;

            if (Current.Kind is not (SyntaxTokenKind.Number or SyntaxTokenKind.Bareword) ||
                !int.TryParse(text, out value) ||
                value <= 0)
            {
                return false;
            }

            NextToken();
            return true;
        }

        /// <summary>
        /// One field line: <c>name: type</c>, <c>name: type[count]</c>, with an
        /// optional <c>= default</c>. Returns null when the line cannot be
        /// parsed, so the caller stops rather than looping on a bad token.
        /// </summary>
        private RawStructFieldSyntax? ParseRawStructField()
        {
            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_raw_struct_field",
                    Title: "Expected a field name inside the raw struct body.",
                    Span: Current.Span,
                    Label: "write 'name: type' here"));
                return null;
            }

            var nameToken = NextToken();
            ParseTypedIdentifierToken(nameToken.Text, out var fieldName, out var inlineTypeName, out var expectsFollowingTypeName);

            string? typeName = inlineTypeName;
            var end = nameToken.Span.End;

            if (expectsFollowingTypeName)
            {
                typeName = ParseTypeName("raw struct field type");
                end = Current.Span.End;
            }
            else if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
            {
                NextToken();
                typeName = ParseTypeName("raw struct field type");
                end = Current.Span.End;
            }
            else
            {
                typeName = ParseTypeNameSuffix(typeName);
            }

            if (string.IsNullOrWhiteSpace(typeName))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_raw_struct_field_type",
                    Title: $"Field '{fieldName}' needs a type.",
                    Span: Current.Span,
                    Label: "write a native type like 'long', 'ulong[3]', or 'cstring[65]'"));
                return null;
            }

            // `ulong[3]` — the element count is part of the ABI (char[65] and
            // char[256] are different layouts), so it is never inferred. It may
            // arrive glued to the type name or as separate bracket tokens.
            int? arrayLength = null;
            var bracketIndex = typeName!.IndexOf('[');

            if (bracketIndex > 0 && typeName.EndsWith(']'))
            {
                var countText = typeName[(bracketIndex + 1)..^1];

                if (!int.TryParse(countText, out var glued) || glued <= 0)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.raw_struct_array_requires_length",
                        Title: $"Field '{fieldName}' needs a positive array length.",
                        Span: nameToken.Span,
                        Label: "write something like 'ulong[3]' or 'cstring[65]'"));
                    return null;
                }

                arrayLength = glued;
                typeName = typeName[..bracketIndex];
            }
            else if (Current.Kind == SyntaxTokenKind.OpenBracket)
            {
                NextToken();

                if (!TryReadRawStructInteger(out var count))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.raw_struct_array_requires_length",
                        Title: $"Field '{fieldName}' needs a positive array length.",
                        Span: Current.Span,
                        Label: "the element count is part of the ABI and cannot be inferred"));
                    return null;
                }

                arrayLength = count;
                end = Current.Span.End;

                if (Current.Kind == SyntaxTokenKind.CloseBracket)
                {
                    end = Current.Span.End;
                    NextToken();
                }
            }

            // Defaults apply when TōSh constructs a value, never when the
            // marshaller produces one — an `out` parameter still arrives zeroed.
            PipelineSyntax? defaultValue = null;

            if (IsEqualsToken(Current))
            {
                var equalsToken = NextToken();
                var expression = ParseOperatorExpression(Current.Span.Start, implicitCurrentItem: false);

                if (expression is null)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_raw_struct_field_default",
                        Title: "Raw struct fields require a value after '='.",
                        Span: equalsToken.Span,
                        Label: $"write a default value for field '{fieldName}'"));
                }
                else
                {
                    defaultValue = new PipelineSyntax([new ExpressionPipelineStageSyntax(expression, expression.Span)]);
                    end = expression.Span.End;
                }
            }

            return new RawStructFieldSyntax(
                fieldName,
                typeName!,
                arrayLength,
                defaultValue,
                TextSpan.FromBounds(nameToken.Span.Start, end));
        }

        private IReadOnlyList<EventFieldDefinitionSyntax> ParseEventDefinitionFields(out int closeBraceEnd)
        {
            closeBraceEnd = Current.Span.End;
            NextToken(); // {
            var fields = new List<EventFieldDefinitionSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.CloseBrace)
                {
                    break;
                }

                var fieldStart = Current.Span.Start;
                var nameToken = Current.Kind == SyntaxTokenKind.Bareword ? NextToken() : ExpectVariableName();
                var name = nameToken.Text;
                string? typeName = null;
                PipelineSyntax? defaultValue = null;

                if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
                {
                    NextToken();
                    typeName = ParseTypeName("event field type");
                }

                if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == "=")
                {
                    NextToken();
                    defaultValue = ParsePipeline(
                        untilCloseParen: false,
                        untilCloseBrace: true,
                        untilSemicolon: true,
                        allowExpressionStart: true);
                }

                var fieldEnd = Current.Span.Start;
                fields.Add(new EventFieldDefinitionSyntax(
                    name,
                    typeName,
                    defaultValue,
                    TextSpan.FromBounds(fieldStart, fieldEnd)));

                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                }
            }

            if (Current.Kind == SyntaxTokenKind.CloseBrace)
            {
                closeBraceEnd = Current.Span.End;
                NextToken();
            }

            return fields;
        }

        private IReadOnlyList<RecordFieldDefinitionSyntax> ParseRecordDefinitionFields(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var openParen = NextToken();
            var fields = new List<RecordFieldDefinitionSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseParen)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                var nameToken = Current.Kind == SyntaxTokenKind.Bareword ? NextToken() : ExpectVariableName();
                ParseTypedIdentifierToken(nameToken.Text, out var name, out var inlineTypeName, out var expectsFollowingTypeName);
                string? typeName = inlineTypeName;
                var isOptional = false;
                PipelineSyntax? defaultValue = null;
                var end = nameToken.Span.End;

                if (name.EndsWith('?'))
                {
                    isOptional = true;
                    name = name[..^1];
                }

                if (!expectsFollowingTypeName)
                {
                    if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
                    {
                        NextToken();
                        typeName = ParseTypeName("record field type");
                        end = Current.Span.End;
                    }
                    else
                    {
                        typeName = ParseTypeNameSuffix(typeName);
                    }
                }
                else
                {
                    typeName = ParseTypeName("record field type");
                    end = Current.Span.End;
                }

                if (typeName is not null && typeName.EndsWith("?", StringComparison.Ordinal))
                {
                    isOptional = true;
                }

                var refinement = typeName is not null ? TryParseRefinementClause() : null;
                end = refinement?.Span.End ?? end;

                if (IsEqualsToken(Current))
                {
                    var equalsToken = NextToken();
                    var expression = ParseOperatorExpression(Current.Span.Start, implicitCurrentItem: false);

                    if (expression is null)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.expected_record_field_default",
                            Title: "Record fields require a value after '='.",
                            Span: equalsToken.Span,
                            Label: $"write a default value for field '{name}'"));
                    }
                    else
                    {
                        var stage = new ExpressionPipelineStageSyntax(expression, expression.Span);
                        defaultValue = new PipelineSyntax([stage]);
                        end = expression.Span.End;
                    }
                }

                fields.Add(new RecordFieldDefinitionSyntax(
                    name,
                    typeName,
                    defaultValue,
                    isOptional,
                    TextSpan.FromBounds(nameToken.Span.Start, end),
                    refinement));

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_paren",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this record field list never closes",
                    Help: "close the record field list with ')' after the last field."));
                return fields;
            }

            NextToken();
            return fields;
        }

        private IReadOnlyList<ClassMemberSyntax> ParseClassBody(string className)
        {
            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_class_body",
                    Title: "Class definitions require a body.",
                    Span: Current.Span,
                    Label: $"write '{{ ... }}' after class '{className}'"));
                return Array.Empty<ClassMemberSyntax>();
            }

            // A class body owns its member boundaries the same way a block owns
            // its statement boundaries, so register the opener and let
            // HasElementBoundaryAfter consult the structural pass here too
            // (TS-P2-24). Without this only the line-break fallback answered,
            // because the owner stack was pushed exclusively by ParseBlock.
            var openBraceTokenIndex = _position;
            var openBrace = NextToken();
            using var boundaryOwner = PushBoundaryOwner(openBraceTokenIndex);
            var members = new List<ClassMemberSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                var member = ParseClassMember(className);
                members.Add(member);

                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (IsAtElementBoundary())
                {
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_class_member_separator",
                        Title: "Class members must be separated by a newline or ';'.",
                        Span: Current.Span,
                        Label: "insert a newline or ';' between class members"));
                    SkipToBlockBoundary();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this class body never closes",
                    Help: "close the class body with '}' after the last member."));
                return members;
            }

            NextToken();
            return members;
        }

        private (BlockSyntax? Getter, BlockSyntax? Setter, int End) ParsePropertyAccessorBlock()
        {
            // An accessor list owns the boundary between `get` and `set` exactly
            // as a class body owns the one between members (TS-P2-24). Without the
            // owner, `get => $this.X` had no structural boundary after it and its
            // arrow body ran on into the following `set`.
            var openBraceTokenIndex = _position;
            var openBrace = NextToken();
            using var boundaryOwner = PushBoundaryOwner(openBraceTokenIndex);
            BlockSyntax? getter = null;
            BlockSyntax? setter = null;

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind != SyntaxTokenKind.Bareword)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_property_accessor",
                        Title: "Property accessors must be 'get' or 'set'.",
                        Span: Current.Span,
                        Label: "write 'get' or 'set' here"));
                    NextToken();
                    continue;
                }

                var accessorName = NextToken().Text;
                var accessorBody = ParseAccessorBody($"property {accessorName}");

                if (string.Equals(accessorName, "get", StringComparison.Ordinal))
                {
                    getter = accessorBody;
                }
                else if (string.Equals(accessorName, "set", StringComparison.Ordinal))
                {
                    setter = accessorBody;
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unknown_property_accessor",
                        Title: $"Unknown property accessor '{accessorName}'.",
                        Span: accessorBody.Span,
                        Label: "use 'get' or 'set'"));
                }

                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this property accessor block never closes"));
                return (getter, setter, openBrace.Span.End);
            }

            var closeBrace = NextToken();
            return (getter, setter, closeBrace.Span.End);
        }

        /// <summary>
        /// Parses a property accessor's body, which may be either
        /// <c>get =&gt; expr</c> or <c>get { ... }</c>.
        /// </summary>
        /// <remarks>
        /// The brace form was never supported and did not fail either. Accessor
        /// bodies went through <see cref="ParseArrowStatementBlock"/>, whose
        /// <c>ConsumeFatArrow</c> is lenient — it consumes an arrow if one is there
        /// and shrugs otherwise — so <c>get { return $this.b }</c> fell into
        /// <c>ParseStatement</c>, where a block-only <c>{</c> (<c>TS-P2-25</c>) made
        /// it a first-class block *value*. The accessor returned a
        /// <c>ShellBlock</c> instead of running, with no diagnostic: a silent wrong
        /// answer rather than a refusal.
        ///
        /// Supporting the brace form was the decision (<c>TS-P2-31</c>) rather than
        /// diagnosing it, because a getter restricted to one expression pushes
        /// anything conditional into a helper method, and <c>{ ... }</c> is what a
        /// method body already looks like.
        /// </remarks>
        private BlockSyntax ParseAccessorBody(string owner)
        {
            return Current.Kind == SyntaxTokenKind.OpenBrace
                ? ParseRequiredBlock(owner)
                : ParseArrowStatementBlock(owner);
        }

        private static void ScanForPositionalRefs(ArgumentSyntax argument, ref int maxPositional)
        {
            switch (argument)
            {
                case SplatArgumentSyntax splat:
                    ScanForPositionalRefs(splat.Value, ref maxPositional);
                    break;
                case VariableReferenceArgumentSyntax varRef:
                    if (int.TryParse(varRef.Name, out var index) && index > maxPositional)
                    {
                        maxPositional = index;
                    }
                    break;
                case OperatorArgumentSyntax op:
                    ScanForPositionalRefs(op.Left, ref maxPositional);
                    ScanForPositionalRefs(op.Right, ref maxPositional);
                    break;
                case MemberAccessArgumentSyntax member:
                    ScanForPositionalRefs(member.Target, ref maxPositional);
                    break;
                case IndexAccessArgumentSyntax indexAccess:
                    ScanForPositionalRefs(indexAccess.Target, ref maxPositional);
                    ScanForPositionalRefs(indexAccess.Index, ref maxPositional);
                    break;
                case MethodCallArgumentSyntax methodCall:
                    ScanForPositionalRefs(methodCall.Target, ref maxPositional);
                    foreach (var child in methodCall.Arguments)
                    {
                        ScanForPositionalRefs(child, ref maxPositional);
                    }
                    break;
                case CallableInvocationArgumentSyntax callableInvocation:
                    ScanForPositionalRefs(callableInvocation.Target, ref maxPositional);
                    foreach (var child in callableInvocation.Arguments)
                    {
                        ScanForPositionalRefs(child, ref maxPositional);
                    }
                    break;
                case ArrayLiteralArgumentSyntax list:
                    foreach (var item in list.Items)
                    {
                        ScanForPositionalRefs(item, ref maxPositional);
                    }
                    break;
                case TupleLiteralArgumentSyntax tuple:
                    foreach (var item in tuple.Items)
                    {
                        ScanForPositionalRefs(item, ref maxPositional);
                    }
                    break;
                case SetLiteralArgumentSyntax set:
                    foreach (var item in set.Items)
                    {
                        ScanForPositionalRefs(item, ref maxPositional);
                    }
                    break;
                case ComparisonPatternSyntax comparisonPattern:
                    ScanForPositionalRefs(comparisonPattern.Operand, ref maxPositional);
                    break;
                case RecordLiteralArgumentSyntax record:
                    foreach (var field in record.Fields)
                    {
                        if (field is RecordFieldSyntax namedField)
                        {
                            ScanForPositionalRefs(namedField.Value, ref maxPositional);
                        }
                        else if (field is ComputedRecordFieldSyntax computedField)
                        {
                            ScanForPositionalRefs(computedField.NameExpression, ref maxPositional);
                            ScanForPositionalRefs(computedField.Value, ref maxPositional);
                        }
                        else if (field is SpreadRecordEntrySyntax spreadEntry)
                        {
                            ScanForPositionalRefs(spreadEntry.Value, ref maxPositional);
                        }
                    }
                    break;
                case BlockArgumentSyntax blockArgument:
                    foreach (var statement in blockArgument.Block.Statements)
                    {
                        ScanForPositionalRefs(statement, ref maxPositional);
                    }
                    break;
                case SubexpressionArgumentSyntax subExpr:
                    foreach (var stage in subExpr.Pipeline.Stages)
                    {
                        if (stage is CommandSyntax cmd)
                        {
                            foreach (var arg in cmd.Arguments)
                            {
                                ScanForPositionalRefs(arg, ref maxPositional);
                            }
                        }
                        else if (stage is ExpressionPipelineStageSyntax expr)
                        {
                            ScanForPositionalRefs(expr.Expression, ref maxPositional);
                        }
                    }
                    break;
                case CommandSubstitutionArgumentSyntax commandSubstitution:
                    foreach (var stage in commandSubstitution.Pipeline.Stages)
                    {
                        if (stage is CommandSyntax cmd)
                        {
                            foreach (var arg in cmd.Arguments)
                            {
                                ScanForPositionalRefs(arg, ref maxPositional);
                            }
                        }
                        else if (stage is ExpressionPipelineStageSyntax expr)
                        {
                            ScanForPositionalRefs(expr.Expression, ref maxPositional);
                        }
                    }
                    break;
                case InputProcessSubstitutionArgumentSyntax processSubstitution:
                    foreach (var stage in processSubstitution.Pipeline.Stages)
                    {
                        if (stage is CommandSyntax cmd)
                        {
                            foreach (var arg in cmd.Arguments)
                            {
                                ScanForPositionalRefs(arg, ref maxPositional);
                            }
                        }
                        else if (stage is ExpressionPipelineStageSyntax expr)
                        {
                            ScanForPositionalRefs(expr.Expression, ref maxPositional);
                        }
                    }
                    break;
                case OutputProcessSubstitutionArgumentSyntax outputProcessSubstitution:
                    foreach (var stage in outputProcessSubstitution.Pipeline.Stages)
                    {
                        if (stage is CommandSyntax cmd)
                        {
                            foreach (var arg in cmd.Arguments)
                            {
                                ScanForPositionalRefs(arg, ref maxPositional);
                            }
                        }
                        else if (stage is ExpressionPipelineStageSyntax expr)
                        {
                            ScanForPositionalRefs(expr.Expression, ref maxPositional);
                        }
                    }
                    break;
                case InterpolatedStringArgumentSyntax interpolated:
                    foreach (var part in interpolated.Parts)
                    {
                        if (part is InterpolatedStringExpressionPart exprPart)
                        {
                            // Scan the raw expression text for $N references
                            foreach (var match in System.Text.RegularExpressions.Regex.Matches(exprPart.Expression, @"\$(\d+)").Cast<System.Text.RegularExpressions.Match>())
                            {
                                if (int.TryParse(match.Groups[1].Value, out var idx) && idx > maxPositional)
                                {
                                    maxPositional = idx;
                                }
                            }
                        }
                    }
                    break;
            }
        }

        private static void ScanForPositionalRefs(StatementSyntax statement, ref int maxPositional)
        {
            switch (statement)
            {
                case PipelineStatementSyntax pipelineStatement:
                    foreach (var stage in pipelineStatement.Pipeline.Stages)
                    {
                        if (stage is CommandSyntax command)
                        {
                            foreach (var argument in command.Arguments)
                            {
                                ScanForPositionalRefs(argument, ref maxPositional);
                            }
                        }
                        else if (stage is ExpressionPipelineStageSyntax expression)
                        {
                            ScanForPositionalRefs(expression.Expression, ref maxPositional);
                        }
                    }
                    break;
                case ReturnStatementSyntax returnStatement when returnStatement.Value is not null:
                    foreach (var stage in returnStatement.Value.Stages)
                    {
                        if (stage is CommandSyntax command)
                        {
                            foreach (var argument in command.Arguments)
                            {
                                ScanForPositionalRefs(argument, ref maxPositional);
                            }
                        }
                        else if (stage is ExpressionPipelineStageSyntax expression)
                        {
                            ScanForPositionalRefs(expression.Expression, ref maxPositional);
                        }
                    }
                    break;
                case VariableDeclarationStatementSyntax variableDeclaration when variableDeclaration.Value is not null:
                    foreach (var stage in variableDeclaration.Value.Stages)
                    {
                        if (stage is CommandSyntax command)
                        {
                            foreach (var argument in command.Arguments)
                            {
                                ScanForPositionalRefs(argument, ref maxPositional);
                            }
                        }
                        else if (stage is ExpressionPipelineStageSyntax expression)
                        {
                            ScanForPositionalRefs(expression.Expression, ref maxPositional);
                        }
                    }
                    break;
                case VariableAssignmentStatementSyntax assignment:
                    foreach (var stage in assignment.Value.Stages)
                    {
                        if (stage is CommandSyntax command)
                        {
                            foreach (var argument in command.Arguments)
                            {
                                ScanForPositionalRefs(argument, ref maxPositional);
                            }
                        }
                        else if (stage is ExpressionPipelineStageSyntax expression)
                        {
                            ScanForPositionalRefs(expression.Expression, ref maxPositional);
                        }
                    }
                    break;
                case MemberAssignmentStatementSyntax assignment:
                    ScanForPositionalRefs(assignment.Target, ref maxPositional);
                    foreach (var stage in assignment.Value.Stages)
                    {
                        if (stage is CommandSyntax command)
                        {
                            foreach (var argument in command.Arguments)
                            {
                                ScanForPositionalRefs(argument, ref maxPositional);
                            }
                        }
                        else if (stage is ExpressionPipelineStageSyntax expression)
                        {
                            ScanForPositionalRefs(expression.Expression, ref maxPositional);
                        }
                    }
                    break;
                case IfStatementSyntax ifStatement:
                    ScanForPositionalRefs(ifStatement.Condition, ref maxPositional);
                    foreach (var child in ifStatement.ThenBlock.Statements)
                    {
                        ScanForPositionalRefs(child, ref maxPositional);
                    }
                    if (ifStatement.ElseBlock is not null)
                    {
                        foreach (var child in ifStatement.ElseBlock.Statements)
                        {
                            ScanForPositionalRefs(child, ref maxPositional);
                        }
                    }
                    break;
                case ForStatementSyntax forStatement:
                    foreach (var stage in forStatement.Source.Stages)
                    {
                        if (stage is CommandSyntax command)
                        {
                            foreach (var argument in command.Arguments)
                            {
                                ScanForPositionalRefs(argument, ref maxPositional);
                            }
                        }
                        else if (stage is ExpressionPipelineStageSyntax expression)
                        {
                            ScanForPositionalRefs(expression.Expression, ref maxPositional);
                        }
                    }
                    foreach (var child in forStatement.Body.Statements)
                    {
                        ScanForPositionalRefs(child, ref maxPositional);
                    }
                    break;
            }
        }

        private BlockSyntax ParseFunctionArrowBody(string functionName, bool allowExpressionStart = false)
        {
            var arrowStart = Current.Span.Start;
            ConsumeFatArrow();

            // Fat-arrow bodies are a single pipeline expression. They
            // terminate at ';', a newline-followed-by-statement-start
            // (handled inside ParsePipeline), or a closing '}' that
            // belongs to the enclosing block (class body, if/while
            // body, anonymous block, …). Without `untilCloseBrace`,
            // `class B { func d(x) => $x * 2 }` would consume the
            // class's closing brace as part of the expression.
            var pipeline = ParsePipeline(
                untilCloseParen: false,
                untilCloseBrace: true,
                untilSemicolon: true,
                allowExpressionStart: allowExpressionStart);
            var span = GetPipelineSpan(pipeline, new TextSpan(arrowStart, 0));
            var statement = new PipelineStatementSyntax(pipeline, span);
            return new BlockSyntax([statement], span);
        }

        private BlockSyntax ParseAnonymousFunctionArrowBody()
        {
            var arrowStart = Current.Span.Start;
            ConsumeFatArrow();

            // The body is a pipeline, exactly as it is for a named
            // arrow function. Reading a single argument instead meant
            // `func (x) => $x + 1` bound only `$x`, leaving `+ 1` to the
            // enclosing expression — so the same body text meant two
            // different things depending on whether the function had a
            // name (TS-P2-26).
            //
            // It stops at a top-level `|` though, because this form only ever
            // appears as an argument, where the pipe separates the enclosing
            // pipeline's stages. Parenthesise the body to give it a pipeline of
            // its own: `func(x) => (ls | count)`.
            var pipeline = ParsePipeline(
                untilCloseParen: true,
                untilCloseBrace: true,
                untilSemicolon: true,
                allowExpressionStart: true,
                singleExpressionBody: true);

            if (pipeline.Stages.Count == 0)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_anonymous_function_expression",
                    Title: "Anonymous `=>` functions require an expression body.",
                    Span: Current.Span,
                    Label: "write an expression after `=>`"));

                return new BlockSyntax(Array.Empty<StatementSyntax>(), new TextSpan(arrowStart, 0));
            }

            // The body span starts at the `=>`, not at the body expression.
            // GetPipelineSpan alone returns the stages' extent, which excludes the
            // arrow — and the formatter identifies this form by checking that the
            // body begins with `=>`, so without the arrow in the span it
            // round-trips `func(x) => ($x + 1)` as a block body instead.
            var pipelineSpan = GetPipelineSpan(pipeline, new TextSpan(arrowStart, 0));
            var span = TextSpan.FromBounds(arrowStart, pipelineSpan.End);
            var statement = new PipelineStatementSyntax(pipeline, span);
            return new BlockSyntax([statement], span);
        }

        private ArgumentSyntax? TryParseRefinementClause()
        {
            if (!MatchesKeyword(Current, "where"))
            {
                return null;
            }

            var whereToken = NextToken();

            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.refinement_requires_expression",
                    Title: "Refinement predicates use expression syntax.",
                    Span: Current.Span,
                    Label: "write the predicate directly after 'where'",
                    Help: "try 'where _ > 0' instead of 'where { _ > 0 }'."));
            }

            var previous = _stopRefinementAtEquals;
            _stopRefinementAtEquals = true;
            try
            {
                var expression = ParseOperatorExpression(whereToken.Span.End, implicitCurrentItem: false);

                if (expression is null)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_refinement_predicate",
                        Title: "Refinements require a predicate after 'where'.",
                        Span: whereToken.Span,
                        Label: "write a boolean expression using '_' for the value"));
                }

                ArgumentSyntax? coercer = null;
                if (MatchesKeyword(Current, "coerce"))
                {
                    var coerceToken = NextToken();
                    coercer = ParseOperatorExpression(coerceToken.Span.End, implicitCurrentItem: false);

                    if (coercer is null)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.expected_refinement_coercer",
                            Title: "Refinement coercers require an expression after 'coerce'.",
                            Span: coerceToken.Span,
                            Label: "write an expression that transforms '_' into a valid value"));
                    }
                }

                if (expression is null)
                {
                    return null;
                }

                var clauses = new List<RefinementDefinitionClauseSyntax>
                {
                    new RefinementWhereClauseSyntax(
                        expression,
                        TextSpan.FromBounds(whereToken.Span.Start, expression.Span.End)),
                };

                if (coercer is not null)
                {
                    clauses.Add(new RefinementCoerceClauseSyntax(
                        Guard: null,
                        Coercer: coercer,
                        Span: TextSpan.FromBounds(whereToken.Span.Start, coercer.Span.End)));
                }

                return new RefinementClauseArgumentSyntax(
                    clauses,
                    TextSpan.FromBounds(whereToken.Span.Start, (coercer ?? expression).Span.End));
            }
            finally
            {
                _stopRefinementAtEquals = previous;
            }
        }

        /// <summary>
        /// A native return annotation, including the width-carrying contract forms
        /// <c>-&gt; int count</c> and <c>-&gt; long count</c>.
        /// </summary>
        /// <remarks>
        /// Bare <c>-&gt; count</c> marshals the return as <c>IntPtr</c>, which is right
        /// for <c>ssize_t</c> and wrong for the many C APIs that return an <c>int</c>
        /// count: a -1 failure came back as 4294967295, positive, so the contract's own
        /// <c>&gt;= 0</c> check passed and no error was raised (<c>TS-P2-124</c>). The
        /// width is knowable at the declaration site and nowhere else, so that is where
        /// it is now said.
        /// </remarks>
        private string? ParseNativeReturnAnnotation()
        {
            var returnTypeName = TryParseReturnTypeAnnotation();

            if (returnTypeName is null ||
                Current.Kind != SyntaxTokenKind.Bareword ||
                !string.Equals(Current.Text, "count", StringComparison.OrdinalIgnoreCase))
            {
                return returnTypeName;
            }

            NextToken();
            return returnTypeName + " count";
        }

        /// <summary>
        /// Parses one <c>where T: Constraint[, Constraint...]</c> clause.
        /// Caller has already verified that <see cref="Current"/> is the
        /// <c>where</c> bareword. Returns false if a parse error occurred
        /// (the caller should stop the where-loop).
        /// </summary>
        private bool TryParseWhereClause(out TypeParameterConstraintSyntax clause)
        {
            clause = default!;
            var whereStart = Current.Span.Start;
            NextToken(); // consume 'where'
            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_type_parameter",
                    Title: "Expected a type-parameter name after 'where'.",
                    Span: Current.Span,
                    Label: "name a type parameter declared earlier"));
                return false;
            }

            var headToken = NextToken();
            ParseTypedIdentifierToken(headToken.Text, out var paramName, out var inlineConstraint, out var expectsFollowingConstraint);
            var constraints = new List<string>();
            if (!string.IsNullOrEmpty(inlineConstraint))
            {
                // Inline constraint may be followed by `<...>` for
                // recursive / parameterized constraints like
                // `where T: IComparable<T>`.
                var suffixed = ParseTypeNameSuffix(inlineConstraint);
                constraints.Add(suffixed ?? inlineConstraint);
            }
            if (expectsFollowingConstraint && Current.Kind == SyntaxTokenKind.Bareword)
            {
                var name = NextToken().Text;
                var suffixed = ParseTypeNameSuffix(name);
                constraints.Add(suffixed ?? name);
            }
            while (Current.Kind == SyntaxTokenKind.Comma)
            {
                NextToken();
                if (Current.Kind != SyntaxTokenKind.Bareword) break;
                var name = NextToken().Text;
                var suffixed = ParseTypeNameSuffix(name);
                constraints.Add(suffixed ?? name);
            }
            var whereEnd = Current.Span.Start;
            clause = new TypeParameterConstraintSyntax(paramName, constraints, TextSpan.FromBounds(whereStart, whereEnd));
            return true;
        }

        private SyntaxToken ExpectVariableName()
        {
            if (Current.Kind == SyntaxTokenKind.Bareword && IsValidIdentifier(Current.Text))
            {
                return NextToken();
            }

            var token = Current;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_variable_name",
                Title: "Expected a variable name.",
                Span: token.Span,
                Label: "variables need a C#-style identifier like 'answer' or 'fileList'"));

            if (Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                NextToken();
            }

            return new SyntaxToken(SyntaxTokenKind.Bareword, token.Span.Start, string.Empty, string.Empty);
        }

        private SyntaxToken ExpectEqualsToken(string title)
        {
            if (IsEqualsToken(Current))
            {
                return NextToken();
            }

            var token = Current;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_assignment_operator",
                Title: title,
                Span: token.Span,
                Label: "insert '=' here"));

            return new SyntaxToken(SyntaxTokenKind.Bareword, token.Span.Start, "=", "=");
        }

        private SyntaxToken ExpectRecordFieldSeparator(string title)
        {
            if (IsEqualsToken(Current) || IsColonToken(Current))
            {
                return NextToken();
            }

            var token = Current;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_record_field_separator",
                Title: title,
                Span: token.Span,
                Label: "insert '=' or ':' here"));

            return new SyntaxToken(SyntaxTokenKind.Bareword, token.Span.Start, "=", "=");
        }

        private void ConsumeFatArrow()
        {
            if (Current.Kind == SyntaxTokenKind.FatArrow)
            {
                NextToken();
            }
        }

        private void ValidateLiteralRangeOperands(RangeArgumentSyntax range)
        {
            ValidateLiteralRangeOperand(range.Start, "start");
            ValidateLiteralRangeOperand(range.Step, "step");
            ValidateLiteralRangeOperand(range.End, "end");
        }

        private void ValidateLiteralRangeOperand(ArgumentSyntax? operand, string role)
        {
            if (operand is not LiteralArgumentSyntax literal ||
                IsValidRangeIntegerLiteral(literal.Value))
            {
                return;
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.range_requires_integer",
                Title: "Range bounds and steps must be 32-bit integers.",
                Span: operand.Span,
                Label: $"range {role} is not an integer",
                Help: "use an integer from -2147483648 through 2147483647"));
        }

        private static bool IsValidRangeIntegerLiteral(object? value)
        {
            return value switch
            {
                int => true,
                long number => number is >= int.MinValue and <= int.MaxValue,
                double number => double.IsFinite(number) &&
                                 number == Math.Floor(number) &&
                                 number is >= int.MinValue and <= int.MaxValue,
                _ => false,
            };
        }

        /// <summary>
        /// Reduces a <c>nameof</c> operand to the name it asks for — <c>TS-P2-20</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The whole chain arrives as one bareword token, dots and all, and both spellings of
        /// <c>nameof</c> reduce it the same way — so the rule lives here once. It previously kept
        /// the *first* segment, which made <c>nameof($foo.Bar)</c> answer <c>"foo"</c>: a name the
        /// operand does mention, so nothing looked wrong, and the wrong string was returned
        /// silently. C# answers with the last segment and so does this.
        /// </para>
        /// </remarks>
        private bool TryReduceNameOfOperand(
            SyntaxToken identifierToken,
            out string identifier,
            out bool isVariableReference,
            out bool isMemberChain)
        {
            identifier = identifierToken.Text;
            isVariableReference = identifier.StartsWith('$');
            if (isVariableReference)
            {
                identifier = identifier[1..];
            }

            var lastDot = identifier.LastIndexOf('.');
            isMemberChain = lastDot >= 0;
            if (isMemberChain)
            {
                identifier = identifier[(lastDot + 1)..];
            }

            if (identifier.Length > 0)
            {
                return true;
            }

            // A trailing dot, or a bare `$`. There is no name to report, and answering with the
            // root segment would be the same silent wrong answer in a new spelling.
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.nameof_expects_a_name",
                Title: $"'{identifierToken.Text}' does not name anything.",
                Span: identifierToken.Span,
                Label: "nameof takes a variable, member, or type name"));
            return false;
        }

        private SpreadElementArgumentSyntax ParseSpreadElement()
        {
            var splatToken = NextToken();
            var innerText = splatToken.Text[3..];
            var innerSpan = new TextSpan(splatToken.Span.Start + 3, innerText.Length);

            var innerToken = new SyntaxToken(SyntaxTokenKind.Bareword, innerSpan.Start, innerText, innerText);

            if (!IsVariableReferenceLikeToken(innerToken))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.invalid_spread_target",
                    Title: "Spread requires a variable reference.",
                    Span: splatToken.Span,
                    Label: "use a target like '...$myVar'"));
                return new SpreadElementArgumentSyntax(new BarewordArgumentSyntax(innerText, innerSpan), splatToken.Span);
            }

            ParseVariableReferenceToken(innerToken, out var name, out var memberPath);

            ArgumentSyntax expression = new VariableReferenceArgumentSyntax(name, GetVariableReferenceSpan(innerToken, name));

            if (!string.IsNullOrEmpty(memberPath))
            {
                expression = ApplyQualifiedMemberChain(expression, memberPath, innerSpan);
            }

            return new SpreadElementArgumentSyntax(expression, splatToken.Span);
        }

        private BlockSyntax ParseRequiredBlock(string owner)
        {
            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                return ParseBlock();
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_block",
                Title: $"The '{owner}' statement requires a block.",
                Span: Current.Span,
                Label: $"write '{{ ... }}' after '{owner}'"));
            return new BlockSyntax(Array.Empty<StatementSyntax>(), Current.Span);
        }

        /// <summary>
        /// True when the parenthesised group starting at the current token runs to the end
        /// of the source — that is, nothing follows its matching <c>)</c> except the block
        /// (<c>TS-P2-77</c>).
        /// </summary>
        /// <remarks>
        /// Scans for the matching parenthesis rather than guessing from the opener, so a
        /// group that is only the *first operand* of a larger expression falls through to
        /// the bare-source path and is parsed whole.
        /// </remarks>
        private bool ParenthesizedGroupIsWholeSource()
        {
            var depth = 0;

            for (var offset = 0; ; offset++)
            {
                var token = Peek(offset);

                switch (token.Kind)
                {
                    case SyntaxTokenKind.OpenParen:
                        depth++;
                        break;

                    case SyntaxTokenKind.CloseParen:
                        depth--;

                        if (depth == 0)
                        {
                            var next = Peek(offset + 1).Kind;
                            return next is SyntaxTokenKind.OpenBrace or SyntaxTokenKind.EndOfFile;
                        }

                        break;

                    case SyntaxTokenKind.EndOfFile:
                        // Unbalanced: let the parenthesised branch run so it reports the
                        // missing `)` rather than this returning a misleading answer.
                        return true;
                }
            }
        }

        // ── Comprehension clause parsing ──
        // Parses: for [$x | (a,b)] in <source> [where <cond>] [let $y=<expr>]
        //         [for ...  |  , name in ...  |  || name in ...]
        //
        // requireFor = false is used after ',' or '||' where the 'for' keyword is optional.
        private ComprehensionClauseSyntax ParseComprehensionClause(bool requireFor = true)
        {
            var clauseStart = Current.Span.Start;

            // Consume optional/required 'for' keyword
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "for", StringComparison.OrdinalIgnoreCase))
            {
                NextToken(); // consume 'for'
            }
            else if (requireFor)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_comprehension_for",
                    Title: "Comprehensions require a 'for' clause after '<|'.",
                    Span: Current.Span,
                    Label: "expected 'for $variable in source'"));
                return new ComprehensionClauseSyntax("_", new BarewordArgumentSyntax("_", Current.Span), Array.Empty<ComprehensionModifierSyntax>(), null, Current.Span);
            }
            // else: 'for' absent after ',' or '||' — that's allowed

            // Parse variable binding: either $name or (a, b, ...) destructure pattern
            string variableName;
            IReadOnlyList<string>? destructureNames = null;

            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                // Destructuring pattern: (a, b, ...)
                NextToken(); // consume '('
                var names = new List<string>();
                while (Current.Kind != SyntaxTokenKind.CloseParen &&
                       Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    if (names.Count > 0)
                    {
                        if (Current.Kind == SyntaxTokenKind.Comma)
                            NextToken(); // consume ','
                        else
                            break;
                    }

                    var nameTok = ExpectVariableName();
                    names.Add(nameTok.Text);
                }

                if (Current.Kind == SyntaxTokenKind.CloseParen)
                    NextToken(); // consume ')'
                else
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_closing_paren",
                        Title: "Destructuring pattern requires a closing ')'.",
                        Span: Current.Span,
                        Label: "expected ')'"));

                destructureNames = names;
                variableName = $"__destr_{string.Join("_", names)}";
            }
            else
            {
                var nameToken = ExpectVariableName();
                variableName = nameToken.Text;
            }

            if (!(Current.Kind == SyntaxTokenKind.Bareword &&
                  string.Equals(Current.Text, "in", StringComparison.OrdinalIgnoreCase)))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_comprehension_in",
                    Title: "Comprehensions require 'in' after the variable name.",
                    Span: Current.Span,
                    Label: "expected 'in' here"));
            }
            else
            {
                NextToken(); // consume 'in'
            }

            var source = ParseArgument(implicitCurrentItem: false)
                         ?? new BarewordArgumentSyntax("_", Current.Span);

            // Parse any number of `where` / `let` modifiers in declared order
            var modifiers = new List<ComprehensionModifierSyntax>();

            while (true)
            {
                if (Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, "where", StringComparison.OrdinalIgnoreCase))
                {
                    var whereToken = NextToken();
                    var condition = HasTopLevelOperatorBeforeComprehensionKeywordOrClose()
                        ? ParseOperatorExpression(Current.Span.Start, implicitCurrentItem: false)
                        : ParseArgument(implicitCurrentItem: false)
                          ?? new BarewordArgumentSyntax("true", whereToken.Span);

                    modifiers.Add(new ComprehensionWhereSyntax(
                        condition,
                        TextSpan.FromBounds(whereToken.Span.Start, condition.Span.End)));
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, "let", StringComparison.OrdinalIgnoreCase))
                {
                    var letToken = NextToken();
                    var letName = ExpectVariableName();

                    if (IsEqualsToken(Current))
                    {
                        NextToken(); // consume '='
                    }
                    else
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.expected_let_equals",
                            Title: "Let bindings require '=' between name and value.",
                            Span: Current.Span,
                            Label: "expected '=' here"));
                    }

                    var letValue = HasTopLevelOperatorBeforeComprehensionKeywordOrClose()
                                   ? ParseOperatorExpression(Current.Span.Start, implicitCurrentItem: false)
                                   : ParseArgument(implicitCurrentItem: false)
                                     ?? new BarewordArgumentSyntax("_", Current.Span);

                    modifiers.Add(new ComprehensionLetSyntax(
                        letName.Text,
                        letValue,
                        TextSpan.FromBounds(letToken.Span.Start, letValue.Span.End)));
                    continue;
                }

                break;
            }

            // Parse optional inner clause — three forms:
            //   , [for] name in ...   → Cartesian product (sugar for nested for)
            //   || [for] name in ...  → Parallel / zip
            //   for name in ...       → Explicit nested for
            ComprehensionClauseSyntax? innerClause = null;
            var innerIsParallel = false;

            if (Current.Kind == SyntaxTokenKind.Comma)
            {
                NextToken(); // consume ','
                innerClause = ParseComprehensionClause(requireFor: false);
            }
            else if (Current.Kind == SyntaxTokenKind.DoublePipe)
            {
                NextToken(); // consume '||'
                innerClause = ParseComprehensionClause(requireFor: false);
                innerIsParallel = true;
            }
            else if (Current.Kind == SyntaxTokenKind.Bareword &&
                     string.Equals(Current.Text, "for", StringComparison.OrdinalIgnoreCase))
            {
                innerClause = ParseComprehensionClause(requireFor: true);
            }

            var endPos = innerClause?.Span.End
                         ?? (modifiers.Count > 0 ? modifiers[^1].Span.End : source.Span.End);

            return new ComprehensionClauseSyntax(
                variableName,
                source,
                modifiers,
                innerClause,
                TextSpan.FromBounds(clauseStart, endPos),
                destructureNames,
                innerIsParallel);
        }

        private bool HasTopLevelComprehensionBeforeCloseParen()
        {
            return HasTopLevelComprehensionBeforeClose(SyntaxTokenKind.CloseParen);
        }

        private bool HasTopLevelComprehensionBeforeClose(SyntaxTokenKind closeKind)
        {
            var depth = 0;

            for (var index = _position; index < _tokens.Count; index++)
            {
                var token = _tokens[index];

                switch (token.Kind)
                {
                    case SyntaxTokenKind.OpenParen:
                    case SyntaxTokenKind.OpenBrace:
                    case SyntaxTokenKind.OpenBracket:
                    case SyntaxTokenKind.OpenBraceColon:
                    case SyntaxTokenKind.OpenBracePipe:
                    case SyntaxTokenKind.OpenBracePercent:
                        depth++;
                        break;

                    case SyntaxTokenKind.CloseParen:
                    case SyntaxTokenKind.CloseBrace:
                    case SyntaxTokenKind.CloseBracket:
                    case SyntaxTokenKind.ColonCloseBrace:
                    case SyntaxTokenKind.PipeCloseBrace:
                    case SyntaxTokenKind.PercentCloseBrace:
                        if (token.Kind == closeKind && depth == 0) return false;
                        if (depth > 0) depth--;
                        else return false;
                        break;

                    case SyntaxTokenKind.LessThanPipe:
                        if (depth == 0) return true;
                        break;
                }
            }

            return false;
        }

        private ArgumentSyntax ParseGeneratorComprehension(SyntaxToken openParen)
        {
            var body = ParseComprehensionBody();

            if (!IsComprehensionOperator())
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_comprehension_operator",
                    Title: "Expected '<|' in generator comprehension.",
                    Span: Current.Span,
                    Label: "expected '<|' here"));
                return body;
            }

            NextToken(); // consume '<|'
            var clause = ParseComprehensionClause();

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required after generator comprehension.",
                    Span: openParen.Span,
                    Label: "this generator comprehension never closes"));
                return new GeneratorComprehensionArgumentSyntax(body, clause, openParen.Span);
            }

            var closeParen = NextToken();
            return new GeneratorComprehensionArgumentSyntax(
                body,
                clause,
                TextSpan.FromBounds(openParen.Span.Start, closeParen.Span.End));
        }

        private ArgumentSyntax ParseListComprehension(SyntaxToken openBracket)
        {
            var body = ParseComprehensionBody();

            if (!IsComprehensionOperator())
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_comprehension_operator",
                    Title: "Expected '<|' in list comprehension.",
                    Span: Current.Span,
                    Label: "expected '<|' here"));
                return new ArrayLiteralArgumentSyntax([body], openBracket.Span);
            }

            NextToken(); // consume '<|'
            var clause = ParseComprehensionClause();

            if (Current.Kind != SyntaxTokenKind.CloseBracket)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_bracket",
                    Title: "A closing ']' is required after list comprehension.",
                    Span: openBracket.Span,
                    Label: "this list comprehension never closes"));
                return new ListComprehensionArgumentSyntax(body, clause, openBracket.Span);
            }

            var closeBracket = NextToken();
            return new ListComprehensionArgumentSyntax(
                body,
                clause,
                TextSpan.FromBounds(openBracket.Span.Start, closeBracket.Span.End));
        }

        private ArgumentSyntax ParseComprehensionBody()
        {
            // Parse the body expression before '<|'.
            // Check if there are operators before the '<|' to decide whether to parse as operator expression.
            if (HasTopLevelOperatorBeforeComprehension())
            {
                return ParseOperatorExpression(Current.Span.Start, implicitCurrentItem: false);
            }

            return ParseArgument(implicitCurrentItem: false)
                   ?? new BarewordArgumentSyntax("_", Current.Span);
        }

        private ArgumentSyntax ParseSetComprehension(SyntaxToken openBrace)
        {
            var body = ParseComprehensionBody();

            if (!IsComprehensionOperator())
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_comprehension_operator",
                    Title: "Expected '<|' in set comprehension.",
                    Span: Current.Span,
                    Label: "expected '<|' here"));
                return new SetLiteralArgumentSyntax([body], openBrace.Span);
            }

            NextToken(); // consume '<|'
            var clause = ParseComprehensionClause();

            if (Current.Kind == SyntaxTokenKind.ColonCloseBrace)
            {
                var closeBrace = NextToken();
                return new SetComprehensionArgumentSyntax(
                    body,
                    clause,
                    TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
            }

            if (TryReportSpacedLiteralCloser(":}", out var spacedCloseSpan))
            {
                return new SetComprehensionArgumentSyntax(
                    body,
                    clause,
                    TextSpan.FromBounds(openBrace.Span.Start, spacedCloseSpan.End));
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.missing_set_closing_delimiter",
                Title: "A closing ':}' is required after set comprehension.",
                Span: Current.Kind == SyntaxTokenKind.CloseBrace ? Current.Span : openBrace.Span,
                Label: "this set comprehension never closes"));

            if (Current.Kind == SyntaxTokenKind.CloseBrace)
            {
                var recoveryClose = NextToken();
                return new SetComprehensionArgumentSyntax(
                    body,
                    clause,
                    TextSpan.FromBounds(openBrace.Span.Start, recoveryClose.Span.End));
            }

            return new SetComprehensionArgumentSyntax(body, clause, openBrace.Span);
        }

        private ArgumentSyntax ParseDictComprehension(SyntaxToken openBrace, bool implicitCurrentItem)
        {
            // `TS-P2-17`. The key takes a full expression, as the value already did:
            // `{% $x % 2 => $x <| for x in 1..4 %}` reported `expected_fat_arrow`, because
            // `ParseArgument` stopped after `$x` and the parser then met `%` where it
            // wanted `=>`. Same shape as `TS-P2-72` and `TS-P2-77` — a position parsing a
            // primary where an expression belongs.
            var key = (HasTopLevelOperatorBefore(SyntaxTokenKind.FatArrow)
                          ? ParseOperatorExpression(Current.Span.Start, implicitCurrentItem)
                          : ParseArgument(implicitCurrentItem: implicitCurrentItem))
                      ?? new BarewordArgumentSyntax("_", Current.Span);

            if (!IsFatArrow(Current))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_fat_arrow",
                    Title: "Dict comprehension requires '=>' between key and value.",
                    Span: Current.Span,
                    Label: "write '=>' after the key expression"));
                return new DictLiteralArgumentSyntax([], openBrace.Span);
            }

            ConsumeFatArrow();

            var value = ParseComprehensionBody();

            if (!IsComprehensionOperator())
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_comprehension_operator",
                    Title: "Expected '<|' in dict comprehension.",
                    Span: Current.Span,
                    Label: "expected '<|' here"));
                return new DictLiteralArgumentSyntax(
                    [new DictEntrySyntax(key, value, TextSpan.FromBounds(key.Span.Start, value.Span.End))],
                    openBrace.Span);
            }

            NextToken(); // consume '<|'
            var clause = ParseComprehensionClause();

            if (Current.Kind == SyntaxTokenKind.PercentCloseBrace)
            {
                var closeBrace = NextToken();
                return new DictComprehensionArgumentSyntax(
                    key,
                    value,
                    clause,
                    TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
            }

            if (TryReportSpacedLiteralCloser("%}", out var spacedCloseSpan))
            {
                return new DictComprehensionArgumentSyntax(
                    key,
                    value,
                    clause,
                    TextSpan.FromBounds(openBrace.Span.Start, spacedCloseSpan.End));
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.missing_dict_closing_delimiter",
                Title: "A closing '%}' is required after dict comprehension.",
                Span: Current.Kind == SyntaxTokenKind.CloseBrace ? Current.Span : openBrace.Span,
                Label: "this dict comprehension never closes"));

            if (Current.Kind == SyntaxTokenKind.CloseBrace)
            {
                var recoveryClose = NextToken();
                return new DictComprehensionArgumentSyntax(
                    key,
                    value,
                    clause,
                    TextSpan.FromBounds(openBrace.Span.Start, recoveryClose.Span.End));
            }

            return new DictComprehensionArgumentSyntax(key, value, clause, openBrace.Span);
        }

        private static bool IsLiteralOpenDelimiter(SyntaxToken token)
            => token.Kind is SyntaxTokenKind.OpenBraceColon
                or SyntaxTokenKind.OpenBracePipe
                or SyntaxTokenKind.OpenBracePercent;

        /// <summary>
        /// A collection-literal delimiter is a single token, so the spaced form
        /// (<c>: }</c> rather than <c>:}</c>) is not a delimiter at all. Left
        /// unhandled it surfaces as a confusing generic error, so it gets a
        /// diagnostic naming the delimiter and consumes both tokens to keep
        /// recovery clean (<c>TS-P2-25</c>).
        /// </summary>
        private bool TryReportSpacedLiteralCloser(string delimiter, out TextSpan closeSpan)
        {
            closeSpan = default;

            var isSpacedForm = delimiter switch
            {
                ":}" => Current.Kind == SyntaxTokenKind.Bareword && string.Equals(Current.Text, ":", StringComparison.Ordinal),
                "|}" => Current.Kind == SyntaxTokenKind.Pipe,
                "%}" => Current.Kind == SyntaxTokenKind.Bareword && string.Equals(Current.Text, "%", StringComparison.Ordinal),
                _ => false,
            };

            if (!isSpacedForm || Peek(1).Kind != SyntaxTokenKind.CloseBrace)
            {
                return false;
            }

            closeSpan = TextSpan.FromBounds(Current.Span.Start, Peek(1).Span.End);

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.spaced_literal_delimiter",
                Title: $"'{delimiter}' must be written without a space.",
                Span: closeSpan,
                Label: $"write '{delimiter}' here",
                Help: "collection literal delimiters are single tokens; remove the space between them."));

            NextToken();
            NextToken();
            return true;
        }

        private ArgumentSyntax ParsePostfixChain(ArgumentSyntax expression, bool implicitCurrentItem = false)
        {
            while (true)
            {
                if (TryConsumePostfixToken(expression.Span.End, out var postfixToken, out var postfixText, out var nullSafe))
                {
                    expression = ApplyQualifiedMemberChain(expression, postfixText, postfixToken.Span, implicitCurrentItem, nullSafe: nullSafe);
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.OpenBracket &&
                    Current.Span.Start == expression.Span.End)
                {
                    expression = ParseIndexAccess(expression, implicitCurrentItem);
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.OpenParen &&
                    Current.Span.Start == expression.Span.End)
                {
                    var arguments = ParseInvocationArguments(implicitCurrentItem);
                    var end = arguments.closeParenEnd ?? expression.Span.End;
                    expression = new CallableInvocationArgumentSyntax(
                        expression,
                        arguments.arguments,
                        TextSpan.FromBounds(expression.Span.Start, end));
                    continue;
                }

                break;
            }

            return expression;
        }

        /// <summary>
        /// The expression inside <c>[</c> and <c>]</c> (<c>TS-P2-72</c>).
        /// </summary>
        /// <remarks>
        /// This used to call <c>ParseArgument</c>, which stops before a binary operator,
        /// so <c>$p[$i - 1]</c> — the obvious way to take the last element — failed with
        /// "A closing ']' is required here", a message about the bracket rather than
        /// about the expression. <c>$p[($i - 1)]</c> worked, so the workaround was a
        /// paren the diagnostic never mentioned.
        /// <para>
        /// <c>ParseOperatorExpression</c> stops at <c>,</c> and <c>]</c> because neither
        /// is an operator, so the <c>[key,]</c> and <c>[,value]</c> lookup forms are
        /// unaffected. An empty slot still returns null so the caller can raise
        /// <c>expected_index_expression</c> rather than silently indexing by nothing.
        /// </para>
        /// </remarks>
        /// <summary>
        /// A value inside a collection literal — an array item, a dict value, a record
        /// field (<c>TS-P2-72</c>).
        /// </summary>
        /// <remarks>
        /// Same defect as the index slot and found the same way: these called
        /// <c>ParseArgument</c>, which stops before a binary operator, so
        /// <c>{% "k" => "a" + $x %}</c> and <c>["a" + $x]</c> failed with a message about
        /// a missing separator rather than about the expression. Porting the VS Code
        /// grammar generator needed nineteen such values parenthesised before it would
        /// parse at all.
        /// <para>
        /// The closing delimiters are single tokens — <c>%}</c> is <c>PercentCloseBrace</c>
        /// and <c>|}</c> is <c>PipeCloseBrace</c> — so an operator parser cannot mistake
        /// them for <c>%</c> or <c>|</c> and run past the end of the literal.
        /// </para>
        /// </remarks>
        /// <summary>
        /// True for the <c>%</c> of a mis-spaced <c>%}</c> while parsing a dict value.
        /// </summary>
        /// <remarks>
        /// Letting <see cref="ParseCollectionValue"/> use the operator parser meant
        /// <c>{% "key" =&gt; 7 % }</c> consumed the <c>%</c> as modulo, so the targeted
        /// <c>spaced_literal_delimiter</c> diagnostic from <c>TS-P2-25</c> never fired and
        /// the user got a worse message for the same mistake. <c>|</c> and <c>:</c> need no
        /// such guard — neither is a binary operator here, which is why only the dict case
        /// regressed.
        /// </remarks>
        private bool IsSpacedDictCloser(SyntaxToken token) =>
            _collectionValueDepth > 0
            && token.Kind == SyntaxTokenKind.Bareword
            && string.Equals(token.Text, "%", StringComparison.Ordinal)
            && Peek(1).Kind == SyntaxTokenKind.CloseBrace;

        private int _collectionValueDepth;

        private ArgumentSyntax? ParseCollectionValue(bool implicitCurrentItem)
        {
            if (Current.Kind is SyntaxTokenKind.Comma
                or SyntaxTokenKind.CloseBracket
                or SyntaxTokenKind.PercentCloseBrace
                or SyntaxTokenKind.PipeCloseBrace
                or SyntaxTokenKind.CloseBrace
                or SyntaxTokenKind.EndOfFile)
            {
                return null;
            }

            _collectionValueDepth++;

            try
            {
                return ParseOperatorExpression(Current.Span.Start, implicitCurrentItem);
            }
            finally
            {
                _collectionValueDepth--;
            }
        }

        private ArgumentSyntax? ParseIndexOperand(bool implicitCurrentItem)
        {
            if (Current.Kind is SyntaxTokenKind.CloseBracket or SyntaxTokenKind.Comma)
            {
                return null;
            }

            return ParseOperatorExpression(Current.Span.Start, implicitCurrentItem);
        }

        private ArgumentSyntax ParseIndexAccess(ArgumentSyntax expression, bool implicitCurrentItem = false)
        {
            var openBracket = NextToken();
            ArgumentSyntax? index = null;
            var lookupKind = IndexLookupKind.Default;

            if (Current.Kind == SyntaxTokenKind.Comma)
            {
                lookupKind = IndexLookupKind.ByValue;
                NextToken();
                index = ParseIndexOperand(implicitCurrentItem);
            }
            else
            {
                index = ParseIndexOperand(implicitCurrentItem);

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    lookupKind = IndexLookupKind.ByKey;
                    NextToken();

                    if (Current.Kind != SyntaxTokenKind.CloseBracket)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.unsupported_double_index_lookup",
                            Title: "Index access supports '[value]', '[key,]', or '[,value]'.",
                            Span: Current.Span,
                            Label: "remove this extra expression"));

                        while (Current.Kind != SyntaxTokenKind.EndOfFile &&
                               Current.Kind != SyntaxTokenKind.CloseBracket)
                        {
                            NextToken();
                        }
                    }
                }
            }

            if (index is null)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_index_expression",
                    Title: "Index access requires an expression inside '[' and ']'.",
                    Span: openBracket.Span,
                    Label: "write an index, key, or value here"));

                index = new LiteralArgumentSyntax(null, openBracket.Span);
            }

            if (Current.Kind != SyntaxTokenKind.CloseBracket)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_bracket",
                    Title: "A closing ']' is required here.",
                    Span: openBracket.Span,
                    Label: "this index access never closes",
                    Help: "close the index with ']' after the lookup expression."));

                return new IndexAccessArgumentSyntax(
                    expression,
                    index,
                    lookupKind,
                    TextSpan.FromBounds(expression.Span.Start, index.Span.End));
            }

            var closeBracket = NextToken();
            return new IndexAccessArgumentSyntax(
                expression,
                index,
                lookupKind,
                TextSpan.FromBounds(expression.Span.Start, closeBracket.Span.End));
        }

        // A ternary branch accepts either a normal expression or a `throw <expr>`
        // expression-form (like C# 7+ throw-expressions).
        private ArgumentSyntax? ParseTernaryBranch(int startPosition, bool implicitCurrentItem)
        {
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "throw", StringComparison.Ordinal))
            {
                var throwToken = NextToken();
                // A throw-expression's argument is parsed like any other primary expression
                // (ternaries can nest arbitrarily inside). Stop before the `:` so the parent
                // ternary still gets its colon.
                ArgumentSyntax? value = null;
                if (!IsTernaryColonToken(Current) && Current.Kind != SyntaxTokenKind.CloseParen &&
                    Current.Kind != SyntaxTokenKind.CloseBrace && Current.Kind != SyntaxTokenKind.CloseBracket &&
                    Current.Kind != SyntaxTokenKind.ColonCloseBrace &&
                    Current.Kind != SyntaxTokenKind.PipeCloseBrace &&
                    Current.Kind != SyntaxTokenKind.PercentCloseBrace &&
                    Current.Kind != SyntaxTokenKind.Semicolon && Current.Kind != SyntaxTokenKind.EndOfFile &&
                    Current.Kind != SyntaxTokenKind.Pipe)
                {
                    value = ParseTernaryExpression(throwToken.Span.End, implicitCurrentItem);
                }
                var end = value?.Span.End ?? throwToken.Span.End;
                return new ThrowArgumentSyntax(value, TextSpan.FromBounds(startPosition, end));
            }

            return ParseTernaryExpression(startPosition, implicitCurrentItem);
        }

        private void SkipToStageBoundary(bool untilCloseParen, bool untilCloseBrace, bool untilSemicolon)
        {
            while (!IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon) &&
                   Current.Kind != SyntaxTokenKind.Pipe &&
                   Current.Kind != SyntaxTokenKind.Ampersand)
            {
                NextToken();
            }
        }

        private void SkipToBlockBoundary()
        {
            while (Current.Kind != SyntaxTokenKind.EndOfFile &&
                   Current.Kind != SyntaxTokenKind.Semicolon &&
                   Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                NextToken();
            }
        }

        private bool IsCurrentPromotedElementBoundary()
        {
            return _elementBoundaryOwnerTokenIndices.TryPeek(out var blockOpenTokenIndex) &&
                   _liteBoundariesByTokenIndex.TryGetValue(_position, out var boundary) &&
                   LiteParser.IsBoundaryOwnedBy(boundary, blockOpenTokenIndex);
        }

        private bool TryGetCurrentTopLevelLiteSeparator(
            out LiteSeparatorKind separator)
        {
            // Assigned up front because the `&&` short-circuits: when this is not
            // a top-level statement, TryGetValue never runs. `EndOfInput` is a
            // real member rather than a sentinel, so it must not be read unless
            // this returns true — both call sites guard on the result.
            separator = default;

            return _isParsingTopLevelStatement &&
                   _liteTopLevelSeparatorsByEndTokenIndex.TryGetValue(
                       _position,
                       out separator);
        }

        private bool IsCurrentPipeForwardSeparator()
        {
            if (Current.Kind != SyntaxTokenKind.Pipe)
            {
                return false;
            }

            if (TryGetCurrentTopLevelLiteSeparator(out var separator))
            {
                return separator == LiteSeparatorKind.PipeForward;
            }

            return Peek(1).Kind == SyntaxTokenKind.GreaterThan &&
                   Current.Span.End == Peek(1).Span.Start;
        }

        private bool IsAtElementBoundary()
        {
            // Top-level structural ranges are candidates rather than a
            // one-to-one semantic parse. Only consult them while the
            // recursive parser is actively consuming a top-level
            // statement; grammar-owned continuations that it has already
            // consumed are necessarily behind Current.
            if (_isParsingTopLevelStatement &&
                (IsCurrentTopLevelLiteStatementStart() ||
                 (TryGetCurrentTopLevelLiteSeparator(out var separator) &&
                  IsLiteStatementEndingSeparator(separator))))
            {
                return true;
            }

            // Every other position is owned by an enclosing construct that
            // registered itself: a block, class body, match arm list, bind
            // block, accessor list, or record/dict literal. The structural pass
            // answers the question outright, so the line-break re-derivation
            // that used to sit here is gone rather than merely unused
            // (TS-P2-24).
            return IsCurrentPromotedElementBoundary();
        }

        private bool HasLineBreakBetween(int start, int end)
        {
            if (end <= start || start < 0 || end > _sourceText.Length)
            {
                return false;
            }

            for (var index = start; index < end; index++)
            {
                var character = _sourceText[index];

                if (character is '\n' or '\r')
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// If <c>_tokens[startIndex]</c> is a bareword adjacent (no
        /// whitespace) to a <c>&lt;</c> followed by what looks like a
        /// generic type-argument list closed by <c>&gt;</c>, returns
        /// the index of the token after the closing <c>&gt;</c>.
        /// Otherwise returns <paramref name="startIndex"/>.
        /// Used by lookahead helpers so command-call syntax
        /// <c>name&lt;T&gt;</c> isn't mis-parsed as a comparison
        /// expression. Conservative: only Bareword / Comma /
        /// LessThan / GreaterThan / GreaterThanGreaterThan / Dot
        /// tokens are accepted inside the angle bracket span.
        /// </summary>
        /// <summary>
        /// Keywords that begin a type declaration and may therefore appear as a class member.
        /// </summary>
        /// <remarks>
        /// <c>enum</c> is the one reached for first; the rest are included because a class that
        /// can nest one kind of type and not another is a rule nobody can remember.
        /// </remarks>
        private static readonly HashSet<string> NestedTypeKeywords = new(StringComparer.Ordinal)
        {
            "enum", "class", "struct", "record", "union", "interface", "trait",
        };

        private bool HasTopLevelCommaBeforeCloseParen()
        {
            var depth = 0;

            for (var index = _position; index < _tokens.Count; index++)
            {
                // A type argument list is stepped over rather than scanned into, exactly as the
                // sibling scanners do. Without this, the comma in `(new P<int, int>(3, 4))` read
                // as a top-level separator and the parenthesised expression was parsed as a
                // tuple — so the member access that followed reported `Member 'A' was not found
                // on type 'ToshTuple'`. One type argument parsed, because there was no comma to
                // misread; two did not.
                if (depth == 0)
                {
                    var skipped = SkipAdjacentGenericTypeArguments(index);
                    if (skipped > index)
                    {
                        index = skipped - 1;
                        continue;
                    }
                }

                var token = _tokens[index];

                switch (token.Kind)
                {
                    case SyntaxTokenKind.OpenParen:
                    case SyntaxTokenKind.OpenBrace:
                    case SyntaxTokenKind.OpenBracket:
                    case SyntaxTokenKind.OpenBraceColon:
                    case SyntaxTokenKind.OpenBracePipe:
                    case SyntaxTokenKind.OpenBracePercent:
                        depth++;
                        break;

                    case SyntaxTokenKind.CloseParen:
                        if (depth == 0)
                        {
                            return false;
                        }

                        depth--;
                        break;

                    case SyntaxTokenKind.CloseBrace:
                    case SyntaxTokenKind.CloseBracket:
                    case SyntaxTokenKind.ColonCloseBrace:
                    case SyntaxTokenKind.PipeCloseBrace:
                    case SyntaxTokenKind.PercentCloseBrace:
                        if (depth > 0)
                        {
                            depth--;
                        }
                        break;

                    case SyntaxTokenKind.Comma:
                        if (depth == 0)
                        {
                            return true;
                        }
                        break;
                }
            }

            return false;
        }

        /// <summary>
        /// Does the group opened by <paramref name="openToken"/> contain a <c>|</c>
        /// it owns? Answered from the structural pass rather than by re-scanning
        /// the token stream with a private depth counter (<c>TS-P2-24</c>).
        /// </summary>
        /// <remarks>
        /// Ownership is by innermost frame, so a pipe inside a nested block or
        /// literal belongs to that construct and not to this group — which the
        /// hand-rolled scan also achieved, and <c>LiteStageDivisionTests</c> pins.
        /// </remarks>
        private bool GroupOwnsStageDivision(SyntaxToken openToken)
        {
            return _tokenIndexBySpanStart.TryGetValue(openToken.Span.Start, out var tokenIndex) &&
                   _stageDivisionOwnerTokenIndices.Contains(tokenIndex);
        }


        private bool IsVariableDeclarationTailTerminator(int previousOffset, int currentOffset)
        {
            var current = Peek(currentOffset);
            return IsEqualsToken(current) ||
                   current.Kind is SyntaxTokenKind.EndOfFile or SyntaxTokenKind.Semicolon or SyntaxTokenKind.CloseBrace or SyntaxTokenKind.CloseParen ||
                   HasLineBreakBetween(Peek(previousOffset).Span.End, current.Span.Start);
        }

        /// <summary>
        /// The offset of the `enum` keyword, stepping over a `flags` modifier.
        /// </summary>
        /// <remarks>
        /// `TS-P3-14`. `flags` sits between the declaration modifier and `enum`
        /// (`export flags enum Colour`), so it is a modifier of the *declaration
        /// kind* rather than of visibility — which is why it is read here rather
        /// than added to `ParseDeclarationModifier`.
        /// </remarks>
        private int GetEnumKeywordOffset()
        {
            var offset = GetDeclarationModifierOffset();

            return MatchesKeywordAtOffset(offset, "flags") ? offset + 1 : offset;
        }

        private DeclarationModifier ParseDeclarationModifier()
        {
            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                return DeclarationModifier.Default;
            }

            return Current.Text switch
            {
                "shy" when IsModifierFollowedByDeclarationKeyword() => (NextToken(), DeclarationModifier.Shy).Item2,
                "global" when IsModifierFollowedByDeclarationKeyword() => (NextToken(), DeclarationModifier.Global).Item2,
                "export" when IsModifierFollowedByDeclarationKeyword() => (NextToken(), DeclarationModifier.Export).Item2,
                _ => DeclarationModifier.Default,
            };
        }

        private int GetDeclarationModifierOffset()
        {
            return IsModifierFollowedByDeclarationKeyword() ? 1 : 0;
        }

        private bool IsModifierFollowedByDeclarationKeyword()
        {
            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            if (!string.Equals(Current.Text, "shy", StringComparison.Ordinal) &&
                !string.Equals(Current.Text, "global", StringComparison.Ordinal) &&
                !string.Equals(Current.Text, "export", StringComparison.Ordinal))
            {
                return false;
            }

            return Peek(1).Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Peek(1).Text, "var", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "const", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "type", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "using", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "require", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "func", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "class", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "module", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "enum", StringComparison.Ordinal) ||

                    // `TS-P3-14`. `flags` precedes `enum` the way `hermit`
                    // precedes `class`, so `export flags enum` has to be seen
                    // here or the modifier scan stops at `export` and the whole
                    // declaration is read as something else.
                    string.Equals(Peek(1).Text, "flags", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "record", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "sealed", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "hollow", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "hermit", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "strict", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "partial", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "fluid", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "interface", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "union", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "struct", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "trait", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "rune", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "leaky", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "fixed", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "raw", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "lazy", StringComparison.Ordinal));
        }

        private static bool MatchesKeyword(SyntaxToken token, string keyword)
        {
            return token.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(token.Text, keyword, StringComparison.Ordinal);
        }

        private bool MatchesKeywordAtOffset(int offset, string keyword)
        {
            return Peek(offset).Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Peek(offset).Text, keyword, StringComparison.Ordinal);
        }

        private static bool IsTernaryQuestionToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(token.Text, "?", StringComparison.Ordinal);
        }

        private static bool IsTernaryColonToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(token.Text, ":", StringComparison.Ordinal);
        }

        private static bool IsEqualsToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && string.Equals(token.Text, "=", StringComparison.Ordinal);
        }

        private static bool IsColonToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && string.Equals(token.Text, ":", StringComparison.Ordinal);
        }

        private static bool IsFatArrow(SyntaxToken token) =>
            token.Kind == SyntaxTokenKind.FatArrow;

        private static bool IsFileImportTarget(SyntaxToken token)
        {
            if (token.Kind == SyntaxTokenKind.String)
            {
                return true;
            }

            if (token.Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            return token.Text.Contains('/', StringComparison.Ordinal) ||
                   token.Text.Contains('\\', StringComparison.Ordinal) ||
                   token.Text.StartsWith(".", StringComparison.Ordinal) ||
                   token.Text.StartsWith("~", StringComparison.Ordinal) ||
                   token.Text.EndsWith(".tosh", StringComparison.OrdinalIgnoreCase);
        }

        private RedirectionSyntax? TryParseRedirection()
        {
            if (!LooksLikeRedirectionOperator())
            {
                return null;
            }

            var start = Current.Span.Start;
            var streamToken = NextToken();
            var stream = streamToken.Text switch
            {
                "o" or "out" => RedirectionStream.Output,
                "e" or "err" => RedirectionStream.Error,
                "o+e" or "out+err" => RedirectionStream.OutputThenError,
                _ => RedirectionStream.ErrorThenOutput, // e+o, err+out
            };
            var modeToken = NextToken();
            var mode = modeToken.Kind == SyntaxTokenKind.GreaterThanGreaterThan
                ? RedirectionMode.Append
                : RedirectionMode.Truncate;

            if (Current.Kind is SyntaxTokenKind.EndOfFile or SyntaxTokenKind.Semicolon or SyntaxTokenKind.Pipe)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_redirection_target",
                    Title: "A file path is required after a redirection operator.",
                    Span: modeToken.Span,
                    Label: "expected a file path here"));
                return null;
            }

            var target = ParsePrimaryArgument(implicitCurrentItem: false);
            if (target is null)
            {
                return null;
            }

            return new RedirectionSyntax(stream, mode, target,
                TextSpan.FromBounds(start, target.Span.End));
        }

        private InputRedirectionSyntax? TryParseInputRedirection()
        {
            if (!LooksLikeInputRedirection())
            {
                return null;
            }

            var start = Current.Span.Start;
            NextToken(); // consume in/i
            NextToken(); // consume <

            if (Current.Kind is SyntaxTokenKind.EndOfFile or SyntaxTokenKind.Semicolon or SyntaxTokenKind.Pipe)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_input_redirection_source",
                    Title: "A file path is required after an input redirection operator.",
                    Span: TextSpan.FromBounds(start, Current.Span.Start),
                    Label: "expected a file path here"));
                return null;
            }

            var source = ParsePrimaryArgument(implicitCurrentItem: false);
            if (source is null)
            {
                return null;
            }

            return new InputRedirectionSyntax(source, TextSpan.FromBounds(start, source.Span.End));
        }

        private bool IsDeclarationBoundary(bool untilCloseParen, bool untilCloseBrace, bool untilSemicolon)
        {
            return IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon) ||
                   IsAtElementBoundary();
        }

        private bool TryConsumePostfixToken(int previousExpressionEnd, out SyntaxToken token, out string postfixText, out bool nullSafe)
        {
            if (!HasLineBreakBetween(previousExpressionEnd, Current.Span.Start) && IsPostfixToken(Current))
            {
                token = NextToken();
                postfixText = token.Text[1..];
                nullSafe = false;
                return true;
            }

            if (!HasLineBreakBetween(previousExpressionEnd, Current.Span.Start) &&
                Current.Kind == SyntaxTokenKind.QuestionDot &&
                Peek(1).Kind == SyntaxTokenKind.Bareword)
            {
                token = NextToken(); // consume ?.
                var memberToken = NextToken(); // consume member name
                postfixText = memberToken.Text;
                nullSafe = true;
                return true;
            }

            token = Current;
            postfixText = string.Empty;
            nullSafe = false;
            return false;
        }

        private static bool IsVariableReferenceLikeToken(SyntaxToken token)
        {
            if (token.Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            // Bare _ or _.Member — current pipeline item
            if (token.Text == "_" || token.Text.StartsWith("_.", StringComparison.Ordinal))
            {
                return true;
            }

            if (token.Text.Length <= 1 || token.Text[0] != '$')
            {
                return false;
            }

            ParseVariableReferenceToken(token, out var name, out _);
            return IsValidIdentifier(name) || (name.Length > 0 && name[0] != '0' && name.All(char.IsDigit));
        }

        private static bool IsAssignableVariableToken(SyntaxToken token)
        {
            if (token.Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            if (IsValidIdentifier(token.Text))
            {
                return true;
            }

            if (!IsVariableReferenceLikeToken(token))
            {
                return false;
            }

            ParseVariableReferenceToken(token, out var name, out var memberPath);
            return IsValidIdentifier(name) && string.IsNullOrWhiteSpace(memberPath);
        }

        private string ParseAssignableVariableName()
        {
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                IsValidIdentifier(Current.Text) &&
                !IsVariableReferenceLikeToken(Current))
            {
                var token = NextToken();
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.variable_references_require_dollar",
                    Title: "Variable assignments must use '$' after declaration.",
                    Span: token.Span,
                    Label: $"write '${token.Text} = ...' here",
                    Help: "declare variables with 'var name', then refer to them everywhere else as '$name'."));
                return token.Text;
            }

            if (IsVariableReferenceLikeToken(Current))
            {
                var token = NextToken();
                ParseVariableReferenceToken(token, out var name, out var memberPath);

                if (string.IsNullOrWhiteSpace(memberPath))
                {
                    return name;
                }
            }

            var invalidToken = Current;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_variable_name",
                Title: "Expected a variable name.",
                Span: invalidToken.Span,
                Label: "write a variable name like '$answer'"));

            if (Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                NextToken();
            }

            return string.Empty;
        }

        private static void ParseVariableReferenceToken(SyntaxToken token, out string name, out string? memberPath)
        {
            // Handle bare _ and _.Member as references to the "_" variable
            if (token.Text == "_")
            {
                name = "_";
                memberPath = null;
                return;
            }

            if (token.Text.StartsWith("_.", StringComparison.Ordinal))
            {
                name = "_";
                memberPath = token.Text[2..];
                return;
            }

            var raw = token.Text[1..];
            var separatorIndex = raw.IndexOf('.');

            if (separatorIndex < 0)
            {
                name = raw;
                memberPath = null;
                return;
            }

            name = raw[..separatorIndex];
            memberPath = raw[(separatorIndex + 1)..];
        }

        private static TextSpan GetVariableReferenceSpan(SyntaxToken token, string name)
        {
            if (token.Text == "_")
            {
                return new TextSpan(token.Span.Start, 1);
            }

            if (token.Text.StartsWith("_.", StringComparison.Ordinal))
            {
                return new TextSpan(token.Span.Start, 1);
            }

            if (token.Text.StartsWith("$", StringComparison.Ordinal))
            {
                return new TextSpan(token.Span.Start, name.Length + 1);
            }

            return token.Span;
        }

        /// <summary>
        /// Table-first form of <see cref="LooksLikeQualifiedDotNetAccess"/>
        /// (TS-P2-23). When this source declares a module with the leading
        /// segment's name, the dotted name is module dispatch regardless of
        /// capitalization, so `Geo.area` and `geo.area` behave alike. Only
        /// names the source never declares fall back to the spelling
        /// heuristic, which remains the best available answer for a CLR type
        /// the parser has no table for.
        /// </summary>
        private bool IsQualifiedDotNetAccess(string text)
        {
            // Deliberately does NOT exclude declared modules: module
            // member access (`Lib.greeting`) travels the same qualified
            // path as CLR static access, and the engine resolves both.
            // The module table is consulted only where the decision is
            // genuinely command-versus-expression.
            return LooksLikeQualifiedDotNetAccess(text);
        }

        private bool DeclaresModuleNamed(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (_context.IsKnownModuleQualifier(text))
            {
                return true;
            }

            if (_declaredModuleNames.Count == 0)
            {
                return false;
            }

            var firstSegment = text.Split('.', 2, StringSplitOptions.None)[0];
            return firstSegment.Length > 0 && _declaredModuleNames.Contains(firstSegment);
        }

        private static bool ShouldPreferStaticDotNetAccessInPredicateContext(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (text.StartsWith("System.", StringComparison.Ordinal) ||
                text.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                text.StartsWith("Tosh.", StringComparison.Ordinal))
            {
                return true;
            }

            var firstSegment = text.Split('.', 2, StringSplitOptions.None)[0];

            return firstSegment is
                "String" or
                "DateTime" or
                "DateTimeOffset" or
                "TimeSpan" or
                "Environment" or
                "Path" or
                "Math" or
                "Guid" or
                "File" or
                "Directory" or
                "Uri";
        }

        private static bool IsValidIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (!(char.IsLetter(text[0]) || text[0] == '_'))
            {
                return false;
            }

            for (var index = 1; index < text.Length; index++)
            {
                var character = text[index];

                if (character == '-')
                {
                    // No trailing hyphen, no consecutive hyphens
                    if (index == text.Length - 1 || text[index + 1] == '-')
                    {
                        return false;
                    }

                    continue;
                }

                if (!(char.IsLetterOrDigit(character) || character == '_'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidQualifiedIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            // A qualified identifier is a dot-separated list of valid identifiers,
            // e.g. 'Foo', 'Foo.Bar', 'Foo.Bar.Baz'. Used by `module Foo.Bar { ... }`.
            foreach (var segment in text.Split('.'))
            {
                if (!IsValidIdentifier(segment))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidCommandName(string text) =>
            ToshParser.IsValidCommandName(text);

        private static bool IsPostfixToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword &&
                   token.Text.Length > 1 &&
                   token.Text[0] == '.' &&
                   // Reject `..` (range) and `...` (spread/splat) — they are
                   // never member access, even when whitespace-adjacent to a
                   // preceding variable reference.
                   token.Text[1] != '.';
        }

        private SyntaxToken Peek(int offset)
        {
            var index = Math.Clamp(_position + offset, 0, _tokens.Count - 1);
            return _tokens[index];
        }

        private SyntaxToken Current => _tokens[_position];

        private SyntaxToken NextToken()
        {
            var current = Current;

            if (_position < _tokens.Count - 1)
            {
                _position++;
            }

            return current;
        }

        private IReadOnlyList<SyntaxToken> ConsumeDocCommentTokens()
        {
            if (Current.Kind != SyntaxTokenKind.DocComment)
                return Array.Empty<SyntaxToken>();

            var tokens = new List<SyntaxToken>();
            while (Current.Kind == SyntaxTokenKind.DocComment)
                tokens.Add(NextToken());
            return tokens;
        }

        private int CountNewlinesBetween(int start, int end)
        {
            if (start >= end || end > _sourceText.Length) return 0;
            var count = 0;
            for (var i = start; i < end; i++)
            {
                if (_sourceText[i] == '\n')
                {
                    count++;
                    if (count >= 2) return count;
                }
            }
            return count;
        }
    }
}
