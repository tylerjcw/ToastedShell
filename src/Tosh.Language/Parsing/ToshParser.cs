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

        private IReadOnlyList<NativeFunctionParameterSyntax> ParseNativeBindingParameters()
        {
            var openParen = Current;

            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                openParen = NextToken();
            }
            else
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A native function binding requires a parameter list.",
                    Span: Current.Span,
                    Label: "write '(...)' after the bound function name"));
            }
            var parameters = new List<NativeFunctionParameterSyntax>();
            var parameterIndex = 0;

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseParen)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_function_parameter_separator",
                        Title: "A function parameter is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add a parameter here"));
                    NextToken();
                    continue;
                }

                parameters.Add(ParseNativeBindingParameter(parameterIndex++));

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseParen and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_function_parameter_separator",
                        Title: "Function parameters must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between function parameters"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this parameter list never closes",
                    Help: "close the parameter list with ')' after the last parameter."));
                return parameters;
            }

            NextToken();
            return parameters;
        }

        private NativeFunctionParameterSyntax ParseNativeBindingParameter(int parameterIndex)
        {
            var passingMode = NativeParameterPassingMode.In;

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "out", StringComparison.OrdinalIgnoreCase))
            {
                passingMode = NativeParameterPassingMode.Out;
                NextToken();
            }
            else if (Current.Kind == SyntaxTokenKind.Bareword &&
                     string.Equals(Current.Text, "ref", StringComparison.OrdinalIgnoreCase))
            {
                passingMode = NativeParameterPassingMode.Ref;
                NextToken();
            }

            var token = Current;

            if (token.Kind != SyntaxTokenKind.Bareword)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_type_name",
                    Title: "Expected a native parameter type.",
                    Span: token.Span,
                    Label: "write a CLR type name like 'int' or 'double'"));

                if (Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    NextToken();
                }

                return new NativeFunctionParameterSyntax($"arg{parameterIndex + 1}", null, passingMode, token.Span);
            }

            var firstToken = NextToken();
            ParseTypedIdentifierToken(firstToken.Text, out var nameOrType, out var inlineTypeName, out var expectsFollowingTypeName);

            if (inlineTypeName is not null || expectsFollowingTypeName || (Current.Kind == SyntaxTokenKind.Bareword && (Current.Text == ":" || Current.Text.StartsWith(":", StringComparison.Ordinal))))
            {
                var name = nameOrType;
                var typeName = inlineTypeName;

                if (expectsFollowingTypeName)
                {
                    typeName = ParseTypeName("parameter type");
                }
                else if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
                {
                    NextToken();
                    typeName = ParseTypeName("parameter type");
                }
                else if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text.StartsWith(":", StringComparison.Ordinal))
                {
                    typeName = NextToken().Text[1..];
                }
                else
                {
                    typeName = ParseTypeNameSuffix(typeName);
                }

                typeName = ParseNativeBufferSuffix(typeName);
                return new NativeFunctionParameterSyntax(name, string.IsNullOrWhiteSpace(typeName) ? null : typeName, passingMode, firstToken.Span);
            }

            var generatedName = $"arg{parameterIndex + 1}";
            var typeOnlyName = ParseNativeBufferSuffix(ParseTypeNameSuffix(nameOrType));
            return new NativeFunctionParameterSyntax(generatedName, string.IsNullOrWhiteSpace(typeOnlyName) ? null : typeOnlyName, passingMode, firstToken.Span);
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

        private ArgumentSyntax ParseMatchArgument(bool implicitCurrentItem = false)
        {
            var matchToken = NextToken();

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_match_value",
                    Title: "Match expressions require a parenthesized value.",
                    Span: matchToken.Span,
                    Label: "write a value in parentheses after 'match'",
                    Help: "try `match (<value>) { pattern => value; default => fallback }`."));
                return new MatchArgumentSyntax(
                    new BarewordArgumentSyntax(string.Empty, matchToken.Span),
                    Array.Empty<MatchArmSyntax>(),
                    matchToken.Span);
            }

            var openParen = NextToken();
            var value = ParseConditionalExpression(openParen);

            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_match_block",
                    Title: "Match expressions require an arm block.",
                    Span: Current.Span,
                    Label: "write '{ pattern => result }' after the match value"));
                return new MatchArgumentSyntax(
                    value,
                    Array.Empty<MatchArmSyntax>(),
                    TextSpan.FromBounds(matchToken.Span.Start, value.Span.End));
            }

            var openBraceTokenIndex = _position;
            var openBrace = NextToken();
            using var boundaryOwner = PushBoundaryOwner(openBraceTokenIndex);
            var arms = new List<MatchArmSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                var arm = ParseMatchArm(implicitCurrentItem);
                arms.Add(arm);

                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
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
                        Code: "tosh.parser.missing_match_arm_separator",
                        Title: "Match arms must be separated by a newline, ';', or ','.",
                        Span: Current.Span,
                        Label: "insert a separator between match arms"));
                    SkipToBlockBoundary();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this match expression never closes",
                    Help: "close the match arm block with '}' after the last arm."));
                return new MatchArgumentSyntax(
                    value,
                    arms,
                    TextSpan.FromBounds(matchToken.Span.Start, openBrace.Span.End));
            }

            var closeBrace = NextToken();
            return new MatchArgumentSyntax(
                value,
                arms,
                TextSpan.FromBounds(matchToken.Span.Start, closeBrace.Span.End));
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

        private IReadOnlyList<PipelineSyntax> ParseBaseConstructorArguments()
        {
            NextToken(); // consume '('
            var args = new List<PipelineSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseParen)
            {
                if (args.Count > 0)
                {
                    if (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken(); // consume ','
                    }
                    else
                    {
                        break;
                    }
                }

                // Read one argument, stopping at the separating comma, the way every other
                // parenthesised argument list is read.
                //
                // Reading each one as a *pipeline running until the close paren* was why a base
                // constructor could only ever be given one argument: the first comma was neither
                // a terminator the pipeline recognised nor a valid continuation of it, so
                // `extends Base($a, $b)` failed with "missing pipeline separator" while
                // `extends Base($a)` parsed. Nothing about generics was involved — the same
                // failure appeared with no type arguments anywhere in sight.
                var expression = HasTopLevelOperatorBeforeCommaOrCloseParen()
                    ? ParseOperatorExpression(Current.Span.Start)
                    : ParseArgument();

                if (expression is null)
                {
                    break;
                }

                args.Add(new PipelineSyntax([new ExpressionPipelineStageSyntax(expression, expression.Span)]));
            }

            if (Current.Kind == SyntaxTokenKind.CloseParen)
            {
                NextToken(); // consume ')'
            }

            return args;
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

        private ClassMemberSyntax ParseClassMember(string className)
        {
            var docTokens = ConsumeDocCommentTokens();
            var memberStart = Current.Span.Start;
            var isShy = false;
            var isStatic = false;
            var isAbstract = false;
            var isFixed = false;
            var isVital = false;
            var isOverride = false;
            var isGuarded = false;
            var isLazy = false;
            var isFading = false;
            var isLocal = false;
            var isRaw = false;
            var isProud = false;

            // Membership and aliasing both come from LanguageSurface (TS-P2-10).
            // This replaced 22 `string.Equals` calls, each alias spelled out twice —
            // once to enter the loop and once to set its flag — which is how the CLI
            // highlighter came to know none of the nine aliases while the Tome
            // colorizer knew all of them.
            while (Current.Kind == SyntaxTokenKind.Bareword &&
                   LanguageSurface.TryResolveMemberModifier(Current.Text, out var modifier))
            {
                switch (modifier)
                {
                    case "shy": isShy = true; break;
                    case "static": isStatic = true; break;
                    case "hollow": isAbstract = true; break;
                    case "fixed": isFixed = true; break;
                    case "vital": isVital = true; break;
                    case "overrule": isOverride = true; break;
                    case "guarded": isGuarded = true; break;
                    case "lazy": isLazy = true; break;
                    case "fading": isFading = true; break;
                    case "local": isLocal = true; break;
                    case "raw": isRaw = true; break;

                    // `proud` (and its alias `public`) is the default for every
                    // member kind except a native `bind` block, which defaults to
                    // shy — so it has to be recorded rather than merely consumed.
                    case "proud": isProud = true; break;

                    default:
                        // A member modifier the registry knows and this switch does
                        // not. Failing loudly beats consuming it silently, which is
                        // what the old chain would have done by simply not matching.
                        throw new InvalidOperationException(
                            $"Unhandled member modifier '{modifier}' — add it to ParseClassMember.");
                }

                NextToken();
            }

            // `bind native "lib" { ... }` as a class member: the library path is
            // written once and the bound functions become static members of the
            // wrapping type. Native members default to `shy` — hidden mechanism,
            // typed public surface — which is the reverse of `func`'s default.
            if (LooksLikeBindStatement())
            {
                var bindStatement = ParseBindStatement() as BindStatementSyntax
                                    ?? throw new InvalidOperationException("Expected a bind statement while parsing a class member.");

                return new ClassBindMemberSyntax(
                    bindStatement,
                    IsShy: !isProud,
                    TextSpan.FromBounds(memberStart, bindStatement.Span.End));
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "prop", StringComparison.Ordinal))
            {
                return ParseClassPropertyMember(isShy, isStatic, isFixed, isVital, isGuarded, isLazy, isFading, isLocal, isAbstract, memberStart, docTokens);
            }

            // `raw func name(...) -> ret from "lib"` — a single binding written
            // inline, for the case that does not justify a whole bind block.
            if (isRaw && LooksLikeRawNativeFunction())
            {
                var binding = ParseNativeBindingFunction();

                return new ClassBindMemberSyntax(
                    SynthesizeBindStatement(binding),
                    IsShy: !isProud,
                    TextSpan.FromBounds(memberStart, binding.Span.End));
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "func", StringComparison.Ordinal))
            {
                var method = ParseFunctionDefinitionStatement(docTokens, allowOperatorName: true) as FunctionDefinitionStatementSyntax
                             ?? throw new InvalidOperationException("Expected a function definition while parsing a class method.");
                return new ClassMethodMemberSyntax(method, isStatic, isShy, isAbstract, isOverride, isGuarded, isFading, isLocal, isRaw, TextSpan.FromBounds(memberStart, method.Span.End));
            }

            // A type declared inside the class body. Parsed by the same statement parsers the
            // top level uses, so nested and outer declarations cannot diverge in what they
            // accept; only where the result is registered differs.
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                NestedTypeKeywords.Contains(Current.Text))
            {
                var declaration = ParseStatement();
                var nestedName = declaration switch
                {
                    ClassDefinitionStatementSyntax c => c.Name,
                    InterfaceDefinitionStatementSyntax i => i.Name,
                    UnionDefinitionStatementSyntax u => u.Name,
                    RecordDefinitionStatementSyntax r => r.Name,
                    StructDefinitionStatementSyntax st => st.Name,
                    TraitDefinitionStatementSyntax t => t.Name,
                    EnumDefinitionStatementSyntax e => e.Name,
                    _ => null,
                };

                if (nestedName is null)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_nested_type",
                        Title: $"Expected a type declaration inside class '{className}'.",
                        Span: declaration.Span,
                        Label: "write an enum, class, struct, record, union, interface, or trait"));

                    return new ClassPropertyMemberSyntax(
                        string.Empty, null, null, null, null, isShy,
                        false, false, false, false, false, false, false, false, declaration.Span);
                }

                return new ClassNestedTypeMemberSyntax(
                    declaration,
                    nestedName,
                    isShy,
                    TextSpan.FromBounds(memberStart, declaration.Span.End));
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "event", StringComparison.Ordinal))
            {
                NextToken(); // consume 'event'
                var eventNameToken = Current.Kind == SyntaxTokenKind.Bareword ? NextToken() : Current;
                ParseTypedIdentifierToken(eventNameToken.Text, out var eventName, out var inlinePayloadType, out var expectsFollowingPayloadType);
                string? payloadTypeName = inlinePayloadType;
                var eventEnd = eventNameToken.Span.End;
                if (expectsFollowingPayloadType)
                {
                    payloadTypeName = ParseTypeName("event payload type");
                    eventEnd = Current.Span.Start;
                }
                return new ClassEventMemberSyntax(eventName, payloadTypeName, isShy, TextSpan.FromBounds(memberStart, eventEnd));
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, className, StringComparison.Ordinal) &&
                (Peek(1).Kind == SyntaxTokenKind.OpenParen || Peek(1).Kind == SyntaxTokenKind.LessThan))
            {
                return ParseClassConstructorMember(className, memberStart);
            }

            var token = Current;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_class_member",
                Title: $"Expected a member inside class '{className}'.",
                Span: token.Span,
                Label: "write 'prop', 'func', or a constructor here"));

            if (Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                NextToken();
            }

            return new ClassPropertyMemberSyntax(string.Empty, null, null, null, null, isShy, false, false, false, false, false, false, false, false, token.Span);
        }

        private ClassMemberSyntax ParseClassPropertyMember(bool isShy, bool isStatic, bool isFixed, bool isVital, bool isGuarded, bool isLazy, bool isFading, bool isLocal, bool isAbstract, int memberStart, IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var propToken = NextToken();
            SyntaxToken nameToken;

            if (Current.Kind == SyntaxTokenKind.Bareword)
            {
                nameToken = NextToken();
            }
            else
            {
                nameToken = ExpectVariableName();
            }

            ParseTypedIdentifierToken(nameToken.Text, out var name, out var inlineTypeName, out var expectsFollowingTypeName);

            if (!IsValidIdentifier(name))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_property_name",
                    Title: "Expected a property name.",
                    Span: nameToken.Span,
                    Label: "properties need an identifier like 'Name' or 'itemCount'"));
            }

            var typeName = inlineTypeName;

            if (expectsFollowingTypeName)
            {
                typeName = ParseTypeName("property type");
            }
            else if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
            {
                NextToken();
                typeName = ParseTypeName("property type");
            }
            else if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text.StartsWith(":", StringComparison.Ordinal))
            {
                typeName = NextToken().Text[1..];
            }

            typeName = ParseTypeNameSuffix(typeName);
            var refinement = typeName is not null ? TryParseRefinementClause() : null;

            PipelineSyntax? initializer = null;
            BlockSyntax? getter = null;
            BlockSyntax? setter = null;
            var end = refinement?.Span.End ?? nameToken.Span.End;

            if (IsFatArrow(Current))
            {
                getter = ParseArrowStatementBlock("property getter");
                end = getter.Span.End;
            }
            else if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == "=")
            {
                NextToken();
                initializer = ParsePipeline(
                    untilCloseParen: false,
                    untilCloseBrace: true,
                    untilSemicolon: true,
                    allowExpressionStart: true);
                end = GetPipelineEnd(initializer, propToken.Span.End);
            }
            else if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                var accessors = ParsePropertyAccessorBlock();
                getter = accessors.Getter;
                setter = accessors.Setter;
                end = accessors.End;
            }

            return new ClassPropertyMemberSyntax(
                name,
                string.IsNullOrWhiteSpace(typeName) ? null : typeName,
                initializer,
                getter,
                setter,
                isShy,
                isStatic,
                isFixed,
                isVital,
                isGuarded,
                isLazy,
                isFading,
                isLocal,
                isAbstract,
                TextSpan.FromBounds(memberStart, end),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()),
                Refinement: refinement);
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

        private ClassMemberSyntax ParseClassConstructorMember(string className, int memberStart)
        {
            var nameToken = NextToken();
            ParseTypeParameterList(); // consume optional <T, U, ...> — type params are on the class, not the constructor
            var parameters = ParseFunctionParameters();
            var body = ParseRequiredBlock(className);

            return new ClassConstructorMemberSyntax(
                parameters,
                body,
                TextSpan.FromBounds(memberStart, body.Span.End));
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

        private static IReadOnlyList<FunctionParameterSyntax> DetectPositionalParameters(BlockSyntax body)
        {
            var maxPositional = 0;

            foreach (var statement in body.Statements)
            {
                if (statement is PipelineStatementSyntax pipelineStatement)
                {
                    foreach (var stage in pipelineStatement.Pipeline.Stages)
                    {
                        if (stage is CommandSyntax command)
                        {
                            foreach (var arg in command.Arguments)
                            {
                                ScanForPositionalRefs(arg, ref maxPositional);
                            }
                        }
                        else if (stage is ExpressionPipelineStageSyntax expression)
                        {
                            ScanForPositionalRefs(expression.Expression, ref maxPositional);
                        }
                    }
                }
            }

            if (maxPositional == 0)
            {
                return Array.Empty<FunctionParameterSyntax>();
            }

            var parameters = new List<FunctionParameterSyntax>();
            for (var i = 1; i <= maxPositional; i++)
            {
                parameters.Add(new FunctionParameterSyntax(i.ToString(), null, IsOptional: false, IsRest: false, DefaultValue: null, body.Span));
            }

            return parameters;
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

        private ArgumentSyntax ParseAnonymousFunctionArgument()
        {
            var funcToken = NextToken();
            var parameters = Current.Kind == SyntaxTokenKind.OpenParen
                ? ParseFunctionParameters()
                : Array.Empty<FunctionParameterSyntax>();

            var returnTypeName = TryParseReturnTypeAnnotation();

            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                var body = ParseBlock();
                return new AnonymousFunctionArgumentSyntax(
                    parameters,
                    body,
                    TextSpan.FromBounds(funcToken.Span.Start, body.Span.End),
                    returnTypeName);
            }

            if (IsFatArrow(Current))
            {
                var body = ParseAnonymousFunctionArrowBody();
                return new AnonymousFunctionArgumentSyntax(
                    parameters,
                    body,
                    TextSpan.FromBounds(funcToken.Span.Start, body.Span.End),
                    returnTypeName);
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_anonymous_function_body",
                Title: "Anonymous functions require `=>` or a block body.",
                Span: Current.Span,
                Label: "write `=> <expression>` or `{ ... }` after the parameter list"));

            return new AnonymousFunctionArgumentSyntax(
                parameters,
                new BlockSyntax(Array.Empty<StatementSyntax>(), funcToken.Span),
                funcToken.Span,
                returnTypeName);
        }

        private IReadOnlyList<FunctionParameterSyntax> ParseFunctionParameters(bool skipOpenParen = false)
        {
            var openParenSpan = Current.Span;
            if (!skipOpenParen)
            {
                openParenSpan = NextToken().Span;
            }
            var parameters = new List<FunctionParameterSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseParen)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_function_parameter_separator",
                        Title: "A function parameter is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add a parameter here"));
                    NextToken();
                    continue;
                }

                parameters.Add(ParseFunctionParameter());

                // Validate: rest parameter must be last
                if (parameters.Count >= 2 && parameters[^2].IsRest)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.rest_parameter_must_be_last",
                        Title: "A rest parameter must be the last parameter.",
                        Span: parameters[^2].Span,
                        Label: "move this rest parameter to the end of the parameter list"));
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseParen and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_function_parameter_separator",
                        Title: "Function parameters must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between function parameters"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParenSpan,
                    Label: "this parameter list never closes",
                    Help: "close the parameter list with ')' after the last parameter."));
                return parameters;
            }

            NextToken();
            return parameters;
        }

        // Parses an optional `: Type` suffix. Returns null when no annotation is present.
        // Used by rest parameters, which previously had no way to declare their element type.
        private string? TryParseParameterTypeAnnotation()
        {
            if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
            {
                NextToken();
                return ParseTypeName("parameter type");
            }
            if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text.StartsWith(":", StringComparison.Ordinal))
            {
                var tokenText = NextToken().Text;
                return tokenText[1..];
            }
            return null;
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

        private RefinementClauseArgumentSyntax? TryParseTypeAliasRefinementBlock()
        {
            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                return null;
            }

            var openBrace = NextToken();
            var clauses = new List<RefinementDefinitionClauseSyntax>();
            var attemptedAnyClause = false;

            while (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                attemptedAnyClause = true;
                var clause = ParseTypeAliasRefinementBlockClause();
                if (clause is not null)
                {
                    clauses.Add(clause);
                }

                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                }
            }

            if (Current.Kind == SyntaxTokenKind.CloseBrace)
            {
                var closeBrace = NextToken();

                if (clauses.Count == 0 && !attemptedAnyClause)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.empty_type_refinement_block",
                        Title: "Type refinement blocks require at least one clause.",
                        Span: openBrace.Span,
                        Label: "add at least one 'where', 'coerce', or 'if ... coerce' clause"));
                }

                return new RefinementClauseArgumentSyntax(
                    clauses,
                    TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.missing_closing_brace",
                Title: "A closing '}' is required here.",
                Span: openBrace.Span,
                Label: "this refinement block never closes",
                Help: "close the refinement block with '}' after the last clause."));

            return new RefinementClauseArgumentSyntax(
                clauses,
                TextSpan.FromBounds(openBrace.Span.Start, Peek(-1).Span.End));
        }

        private RefinementDefinitionClauseSyntax? ParseTypeAliasRefinementBlockClause()
        {
            if (MatchesKeyword(Current, "where"))
            {
                var whereToken = NextToken();
                var expression = ParseOperatorExpression(whereToken.Span.End, implicitCurrentItem: false);

                if (expression is null)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_refinement_predicate",
                        Title: "Refinements require a predicate after 'where'.",
                        Span: whereToken.Span,
                        Label: "write a boolean expression using '_' for the value"));
                    return null;
                }

                return new RefinementWhereClauseSyntax(
                    expression,
                    TextSpan.FromBounds(whereToken.Span.Start, expression.Span.End));
            }

            if (MatchesKeyword(Current, "coerce"))
            {
                var coerceToken = NextToken();
                var coercer = ParseOperatorExpression(coerceToken.Span.End, implicitCurrentItem: false);

                if (coercer is null)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_refinement_coercer",
                        Title: "Refinement coercers require an expression after 'coerce'.",
                        Span: coerceToken.Span,
                        Label: "write an expression that transforms '_' into a valid value"));
                    return null;
                }

                return new RefinementCoerceClauseSyntax(
                    Guard: null,
                    Coercer: coercer,
                    Span: TextSpan.FromBounds(coerceToken.Span.Start, coercer.Span.End));
            }

            if (MatchesKeyword(Current, "if"))
            {
                var ifToken = NextToken();
                var guard = ParseOperatorExpression(ifToken.Span.End, implicitCurrentItem: false);

                if (guard is null)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_refinement_guard",
                        Title: "Refinement coercion guards require an expression after 'if'.",
                        Span: ifToken.Span,
                        Label: "write a boolean expression that decides when coercion runs"));
                    return null;
                }

                if (!MatchesKeyword(Current, "coerce"))
                {
                    var help = Current.Kind == SyntaxTokenKind.OpenBrace
                        ? "refinement blocks only support expression-only coercion; use 'if <expr> coerce <expr>'"
                        : "write 'coerce <expr>' after the guard";
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_refinement_coerce_after_if",
                        Title: "Guarded refinement clauses require 'coerce' after the condition.",
                        Span: Current.Span,
                        Label: "write 'if <expr> coerce <expr>'",
                        Help: help));
                    return null;
                }

                var coerceToken = NextToken();
                var coercer = ParseOperatorExpression(coerceToken.Span.End, implicitCurrentItem: false);

                if (coercer is null)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_refinement_coercer",
                        Title: "Refinement coercers require an expression after 'coerce'.",
                        Span: coerceToken.Span,
                        Label: "write an expression that transforms '_' into a valid value"));
                    return null;
                }

                return new RefinementCoerceClauseSyntax(
                    Guard: guard,
                    Coercer: coercer,
                    Span: TextSpan.FromBounds(ifToken.Span.Start, coercer.Span.End));
            }

            var clauseStart = Current.Span;
            var clauseEnd = Current.Span;
            while (Current.Kind is not SyntaxTokenKind.CloseBrace
                   and not SyntaxTokenKind.Semicolon
                   and not SyntaxTokenKind.EndOfFile
                   && !MatchesKeyword(Current, "where")
                   && !MatchesKeyword(Current, "coerce")
                   && !MatchesKeyword(Current, "if"))
            {
                clauseEnd = Current.Span;
                NextToken();
            }

            var clauseSpan = TextSpan.FromBounds(clauseStart.Start, clauseEnd.End);
            string? clauseHelp = null;
            if (clauseSpan.Start >= 0 && clauseSpan.Start + clauseSpan.Length <= _sourceText.Length)
            {
                var snippet = _sourceText.Substring(clauseSpan.Start, clauseSpan.Length).Trim();
                if (snippet.Length > 0)
                {
                    clauseHelp = $"did you mean `where {snippet}`?";
                }
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.invalid_type_refinement_clause",
                Title: "Type refinement blocks only support 'where', 'coerce', and 'if ... coerce' clauses.",
                Span: clauseSpan,
                Label: "replace this with a supported refinement clause",
                Help: clauseHelp));

            return null;
        }

        private ArgumentSyntax? TryParseTypeAliasRangeRefinementClause()
        {
            if (!MatchesKeyword(Current, "in"))
            {
                return null;
            }

            var inToken = NextToken();
            var range = ParseArgument(implicitCurrentItem: false);

            if (range is not RangeArgumentSyntax { Step: null, End: { } upperBound } rangeSyntax)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_range_separator",
                    Title: "Type alias ranges use '..' between lower and upper bounds.",
                    Span: Current.Span,
                    Label: "write ranges like 'in 0..100'"));
                return null;
            }

            var span = rangeSyntax.Span;
            var currentValue = new VariableReferenceArgumentSyntax("_", span);
            var lowerBound = rangeSyntax.Start;
            var lowerCheck = new OperatorArgumentSyntax(currentValue, ">=", span, lowerBound, span);
            var upperCheck = new OperatorArgumentSyntax(currentValue, "<=", span, upperBound, span);

            return new OperatorArgumentSyntax(lowerCheck, "and", span, upperCheck, span);
        }

        private FunctionParameterSyntax ParseFunctionParameter()
        {
            var token = Current;

            if (token.Kind != SyntaxTokenKind.Bareword)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_function_parameter",
                    Title: "Expected a function parameter name.",
                    Span: token.Span,
                    Label: "parameters need an identifier like 'path' or 'days'"));

                if (Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    NextToken();
                }

                return new FunctionParameterSyntax(string.Empty, null, false, false, null, token.Span);
            }

            // Standalone '...' is shorthand for 'args...' (optionally followed by a type annotation)
            if (token.Text == "...")
            {
                var standaloneToken = NextToken();
                var standaloneRestType = TryParseParameterTypeAnnotation();
                var standaloneRestRefinement = standaloneRestType is not null ? TryParseRefinementClause() : null;
                return new FunctionParameterSyntax("args", standaloneRestType, false, true, null, standaloneToken.Span, standaloneRestRefinement);
            }

            // 'name...' or 'name...:Type' rest parameter — strip the suffix, then parse a type annotation
            // if present. Both `name...: Type` (separate tokens) and `name...:Type` (fused) are handled.
            var restIndex = token.Text.IndexOf("...", StringComparison.Ordinal);
            if (restIndex >= 0 && token.Kind == SyntaxTokenKind.Bareword)
            {
                var beforeRest = token.Text[..restIndex];
                var afterRest = token.Text[(restIndex + 3)..];
                if (string.IsNullOrEmpty(afterRest) || afterRest.StartsWith(":", StringComparison.Ordinal))
                {
                    var restToken = NextToken();
                    if (!IsValidIdentifier(beforeRest))
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.expected_function_parameter",
                            Title: "Expected a function parameter name.",
                            Span: restToken.Span,
                            Label: "parameters need an identifier like 'path' or 'days'"));
                    }

                    string? restTypeName = null;
                    if (afterRest.StartsWith(":", StringComparison.Ordinal))
                    {
                        // Fused form: `name...:Type` — the colon's suffix (if any) is the type.
                        // If suffix is empty (bare `:`), the next token is the type name.
                        var inlineType = afterRest[1..];
                        restTypeName = string.IsNullOrEmpty(inlineType)
                            ? ParseTypeName("parameter type")
                            : inlineType;
                    }
                    else
                    {
                        restTypeName = TryParseParameterTypeAnnotation();
                    }
                    var restRefinement = restTypeName is not null ? TryParseRefinementClause() : null;
                    return new FunctionParameterSyntax(beforeRest, restTypeName, false, true, null, restToken.Span, restRefinement);
                }
            }

            var nameToken = NextToken();
            ParseTypedIdentifierToken(nameToken.Text, out var name, out var inlineTypeName, out var expectsFollowingTypeName);

            // Check for optional parameter suffix: name? or name?:Type
            var isOptional = false;
            if (name.EndsWith('?'))
            {
                isOptional = true;
                name = name[..^1];
            }

            if (!IsValidIdentifier(name))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_function_parameter",
                    Title: "Expected a function parameter name.",
                    Span: nameToken.Span,
                    Label: "parameters need an identifier like 'path' or 'days'"));
            }

            var typeName = inlineTypeName;

            if (!expectsFollowingTypeName)
            {
                if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
                {
                    NextToken();
                    typeName = ParseTypeName("parameter type");
                }
                else if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text.StartsWith(":", StringComparison.Ordinal))
                {
                    var tokenText = NextToken().Text;
                    typeName = tokenText[1..];
                }
                else
                {
                    typeName = ParseTypeNameSuffix(typeName);
                }
            }
            else
            {
                typeName = ParseTypeName("parameter type");
            }

            var refinement = typeName is not null ? TryParseRefinementClause() : null;
            PipelineSyntax? defaultValue = null;

            if (IsEqualsToken(Current))
            {
                NextToken(); // consume '='
                var expression = ParseOperatorExpression(Current.Span.Start, implicitCurrentItem: false);

                if (expression is not null)
                {
                    var stage = new ExpressionPipelineStageSyntax(expression, expression.Span);
                    defaultValue = new PipelineSyntax([stage]);
                }
            }

            return new FunctionParameterSyntax(name, string.IsNullOrWhiteSpace(typeName) ? null : typeName, isOptional || defaultValue is not null, false, defaultValue, nameToken.Span, refinement);
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

        private string? TryParseReturnTypeAnnotation()
        {
            if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == "-" &&
                Peek(1).Kind == SyntaxTokenKind.GreaterThan)
            {
                NextToken(); // consume -
                NextToken(); // consume >
                return ParseTypeName("return type");
            }

            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                return null;
            }

            if (Current.Text == "->")
            {
                NextToken();
                return ParseTypeName("return type");
            }

            if (Current.Text.StartsWith("->", StringComparison.Ordinal))
            {
                var token = NextToken();
                var typeName = token.Text[2..];

                if (string.IsNullOrWhiteSpace(typeName))
                {
                    return ParseTypeName("return type");
                }

                return typeName;
            }

            return null;
        }

        private IReadOnlyList<string> ParseTypeParameterList()
        {
            return ParseTypeParameterList(out _);
        }

        /// <summary>
        /// Parses a generic type-parameter list <c>&lt;T, U, ...&gt;</c>,
        /// returning the parameter names. When <paramref name="variances"/>
        /// is requested, recognises an optional <c>out</c> / <c>in</c>
        /// prefix on each parameter and emits a parallel variance list:
        /// <c>out T</c> ⇒ covariant, <c>in T</c> ⇒ contravariant, no
        /// prefix ⇒ invariant. Variance annotations are syntactically
        /// accepted on every declaration form so the lists stay in sync;
        /// downstream passes only honor them on interfaces (matching C#).
        /// </summary>
        private IReadOnlyList<string> ParseTypeParameterList(out IReadOnlyList<TypeParameterVariance> variances)
        {
            if (Current.Kind != SyntaxTokenKind.LessThan)
            {
                variances = Array.Empty<TypeParameterVariance>();
                return Array.Empty<string>();
            }

            var open = NextToken();
            var parameters = new List<string>();
            var varianceList = new List<TypeParameterVariance>();

            while (Current.Kind is not SyntaxTokenKind.GreaterThan and not SyntaxTokenKind.EndOfFile)
            {
                var variance = TypeParameterVariance.Invariant;
                if (Current.Kind == SyntaxTokenKind.Bareword
                    && (Current.Text == "out" || Current.Text == "in")
                    && Peek(1).Kind == SyntaxTokenKind.Bareword
                    && IsValidIdentifier(Peek(1).Text))
                {
                    variance = Current.Text == "out"
                        ? TypeParameterVariance.Covariant
                        : TypeParameterVariance.Contravariant;
                    NextToken();
                }

                var nameToken = ExpectVariableName();

                if (!string.IsNullOrWhiteSpace(nameToken.Text))
                {
                    parameters.Add(nameToken.Text);
                    varianceList.Add(variance);
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind != SyntaxTokenKind.GreaterThan)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_type_parameter_separator",
                        Title: "Type parameters must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between type parameters"));
                }
            }

            if (Current.Kind == SyntaxTokenKind.GreaterThan)
            {
                NextToken();
            }
            else
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_angle",
                    Title: "A closing '>' is required here.",
                    Span: open.Span,
                    Label: "this type parameter list never closes",
                    Help: "close the type parameter list with '>' after the last parameter."));
            }

            variances = varianceList;
            return parameters;
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

        private string ParseTypeName(string label)
        {
            if (Current.Kind == SyntaxTokenKind.Bareword)
            {
                return ParseTypeNameSuffix(NextToken().Text) ?? string.Empty;
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_type_name",
                Title: $"Expected a {label}.",
                Span: Current.Span,
                Label: $"write a CLR type name for the {label}"));
            return string.Empty;
        }

        private string? ParseTypeNameSuffix(string? initialTypeName)
        {
            if (string.IsNullOrWhiteSpace(initialTypeName))
            {
                return null;
            }

            var builder = new StringBuilder(initialTypeName);

            if (Current.Kind == SyntaxTokenKind.LessThan)
            {
                builder.Append(ParseGenericTypeArguments());
            }

            // `TS-P2-69`. A postfix `[]` makes a CLR array type: `string[]` is
            // `System.String[]`, the typed form of the `array` alias the language already
            // has for `System.Object[]`. Repeats for jagged arrays (`string[][]`).
            //
            // Only an *empty* bracket pair is a type suffix. In native parameter position
            // a bracket holds a fixed inline capacity — `buffer[256]`, `double[3]` — and
            // `ParseNativeBufferSuffix` still claims those, because it runs after this and
            // sees a number rather than an immediate `]`. Requiring the brackets to be
            // adjacent to the name keeps an unrelated `[` on the next line out of it.
            while (Current.Kind == SyntaxTokenKind.OpenBracket &&
                   Current.Span.Start == Peek(-1).Span.End &&
                   Peek(1).Kind == SyntaxTokenKind.CloseBracket)
            {
                NextToken();
                NextToken();
                builder.Append("[]");
            }

            if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == "?")
            {
                builder.Append(NextToken().Text);
            }

            return builder.ToString();
        }

        private string ParseGenericTypeArguments()
        {
            var builder = new StringBuilder();
            var depth = 0;
            var expectArgument = true;

            while (Current.Kind is SyntaxTokenKind.LessThan or SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanGreaterThan or SyntaxTokenKind.Comma or SyntaxTokenKind.Bareword)
            {
                if (Current.Kind == SyntaxTokenKind.LessThan)
                {
                    depth++;
                    builder.Append('<');
                    NextToken();
                    expectArgument = true;
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    builder.Append(", ");
                    NextToken();
                    expectArgument = true;
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.GreaterThan)
                {
                    depth--;
                    builder.Append('>');
                    NextToken();

                    if (depth <= 0)
                    {
                        break;
                    }

                    expectArgument = false;
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.GreaterThanGreaterThan)
                {
                    if (depth <= 0)
                    {
                        break;
                    }

                    depth -= 2;
                    builder.Append(">>");
                    NextToken();

                    if (depth <= 0)
                    {
                        break;
                    }

                    expectArgument = false;
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Bareword)
                {
                    if (!expectArgument)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.missing_type_argument_separator",
                            Title: "Generic type arguments must be separated by ','.",
                            Span: Current.Span,
                            Label: "insert ',' between generic type arguments"));
                    }

                    builder.Append(ParseTypeNameSuffix(NextToken().Text));
                    expectArgument = false;
                    continue;
                }
            }

            if (depth > 0)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_angle",
                    Title: "A closing '>' is required here.",
                    Span: Current.Span,
                    Label: "this generic type argument list never closes",
                    Help: "close the generic argument list with '>' after the last type."));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Parses <c>&lt;A, B, C&gt;</c> into a structured list of top-level type-argument
        /// strings (with nested generics preserved as inner text). Returns
        /// <c>(displayString, args, hasAngles)</c> — <paramref name="hasAngles"/> is
        /// <c>true</c> even when the user wrote <c>&lt;&gt;</c> with no arguments,
        /// so callers can distinguish that from "no generic suffix at all".
        /// </summary>
        private (string Display, IReadOnlyList<string> Arguments, bool HasAngles) ParseGenericTypeArgumentsStructured()
        {
            if (Current.Kind != SyntaxTokenKind.LessThan)
            {
                return (string.Empty, Array.Empty<string>(), false);
            }

            var display = new StringBuilder();
            var args = new List<string>();
            var current = new StringBuilder();
            var depth = 0;
            var sawAny = false;

            while (Current.Kind is SyntaxTokenKind.LessThan or SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanGreaterThan or SyntaxTokenKind.Comma or SyntaxTokenKind.Bareword)
            {
                if (Current.Kind == SyntaxTokenKind.LessThan)
                {
                    if (depth > 0) current.Append('<');
                    depth++;
                    display.Append('<');
                    NextToken();
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    if (depth == 1)
                    {
                        if (current.Length > 0) { args.Add(current.ToString().Trim()); current.Clear(); sawAny = true; }
                    }
                    else
                    {
                        current.Append(',');
                    }
                    display.Append(", ");
                    NextToken();
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.GreaterThan)
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (current.Length > 0) { args.Add(current.ToString().Trim()); current.Clear(); sawAny = true; }
                        display.Append('>');
                        NextToken();
                        return (display.ToString(), args, true);
                    }
                    current.Append('>');
                    display.Append('>');
                    NextToken();
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.GreaterThanGreaterThan)
                {
                    // closes two depth levels
                    depth -= 2;
                    if (depth <= 0)
                    {
                        if (current.Length > 0) { args.Add(current.ToString().Trim()); current.Clear(); sawAny = true; }
                        display.Append(">>");
                        NextToken();
                        return (display.ToString(), args, true);
                    }
                    current.Append(">>");
                    display.Append(">>");
                    NextToken();
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Bareword)
                {
                    var inner = ParseTypeNameSuffix(NextToken().Text) ?? string.Empty;
                    current.Append(inner);
                    display.Append(inner);
                    continue;
                }
            }

            if (depth > 0)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_angle",
                    Title: "A closing '>' is required here.",
                    Span: Current.Span,
                    Label: "this generic type argument list never closes",
                    Help: "close the generic argument list with '>' after the last type."));
            }

            if (current.Length > 0) { args.Add(current.ToString().Trim()); }
            return (display.ToString(), args, sawAny || depth != 0);
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

        private SyntaxToken ExpectCommandName()
        {
            if (Current.Kind == SyntaxTokenKind.Bareword && IsValidCommandName(Current.Text))
            {
                return NextToken();
            }

            var token = Current;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_command_name",
                Title: "Expected a function name.",
                Span: token.Span,
                Label: "function names can use letters, digits, underscores, and hyphens (e.g. 'run-game')"));

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

        private PipelineStageSyntax? ParsePipelineStage(
            bool allowExpressionStage,
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            // Here-string: <<< "value" feeds a string into the pipeline
            if (Current.Kind == SyntaxTokenKind.LessThanLessThanLessThan)
            {
                var hereStringToken = NextToken();
                var argument = ParsePrimaryArgument(implicitCurrentItem: false);

                if (argument is null)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_here_string_value",
                        Title: "A value is required after '<<<'.",
                        Span: hereStringToken.Span,
                        Label: "expected a string or expression here"));
                    return null;
                }

                return new ExpressionPipelineStageSyntax(argument,
                    TextSpan.FromBounds(hereStringToken.Span.Start, argument.Span.End));
            }

            if (allowExpressionStage && LooksLikeExpressionStage())
            {
                var expression = HasTopLevelOperatorBeforeStageBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon)
                    ? ParseOperatorExpression(Current.Span.Start)
                    : ParseArgument();
                return expression is null ? null : new ExpressionPipelineStageSyntax(expression, expression.Span);
            }

            return ParseCommand(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
        }

        private CommandSyntax? ParseCommand(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_command_name",
                    Title: "Expected a command name.",
                    Span: Current.Span,
                    Label: "commands start with a bareword like 'ls' or 'where'"));
                NextToken();
                return null;
            }

            var nameToken = NextToken();
            List<ArgumentSyntax> arguments;

            // Explicit generic call-site type arguments: `name<T1, T2>`
            // with no whitespace between the name and the '<'. The
            // '<(' input-redirection form is its own lexer token
            // (LessThanOpenParen), so a plain LessThan immediately
            // following the name is unambiguously a generic argument
            // list — never input redirection. We still require
            // ParseGenericTypeArgumentsStructured to find a closing
            // '>'; if it doesn't, no args are consumed.
            IReadOnlyList<string>? explicitTypeArgs = null;
            if (Current.Kind == SyntaxTokenKind.LessThan &&
                Current.Span.Start == nameToken.Span.End)
            {
                var savedPosition = _position;
                var savedDiagnosticCount = _diagnostics.Count;
                var (_, parsedArgs, hasAngles) = ParseGenericTypeArgumentsStructured();
                if (hasAngles && parsedArgs.Count > 0)
                {
                    explicitTypeArgs = parsedArgs;
                }
                else
                {
                    // Roll back: this wasn't a generic argument list.
                    _position = savedPosition;
                    if (_diagnostics.Count > savedDiagnosticCount)
                    {
                        _diagnostics.RemoveRange(savedDiagnosticCount, _diagnostics.Count - savedDiagnosticCount);
                    }
                }
            }

            // Function-call syntax: command immediately followed by '(' with no space.
            // e.g. test_args(1, 2, 3) — parse the parenthesized list as individual arguments,
            // not as a tuple literal.
            if (Current.Kind == SyntaxTokenKind.OpenParen &&
                Current.Span.Start == nameToken.Span.End)
            {
                var invocationArgs = ParseInvocationArguments();
                arguments = new List<ArgumentSyntax>(invocationArgs.arguments);
            }
            else if (!_userFunctionNames.Contains(nameToken.Text) &&
                     TryGetCurrentItemExpressionArgumentIndex(nameToken.Text, out var expressionArgumentIndex))
            {
                arguments = ParseCurrentItemExpressionCommandArguments(
                    nameToken.Text,
                    expressionArgumentIndex,
                    nameToken.Span.End,
                    stopAtCloseParen,
                    stopAtCloseBrace,
                    stopAtSemicolon);
            }
            else if (!_userFunctionNames.Contains(nameToken.Text) &&
                     (string.Equals(nameToken.Text, "get", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(nameToken.Text, "select", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(nameToken.Text, "pick", StringComparison.OrdinalIgnoreCase)))
            {
                arguments = ParseGetArguments(nameToken.Span.End, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }
            else
            {
                arguments = new List<ArgumentSyntax>();
                var lastConsumedEnd = nameToken.Span.End;

                while (!IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                       Current.Kind != SyntaxTokenKind.Pipe &&
                       !(Current.Kind == SyntaxTokenKind.Ampersand &&
                         !(Peek(1).Kind == SyntaxTokenKind.Bareword && IsValidCommandName(Peek(1).Text) && Current.Span.End == Peek(1).Span.Start)) &&
                       !LooksLikeRedirectionOperator() &&
                       !LooksLikeInputRedirection())
                {
                    if (IsAtElementBoundary())
                    {
                        break;
                    }

                    if (TryParseCommaJoinedCommandArgument(out var joinedArgument))
                    {
                        arguments.Add(joinedArgument);
                        lastConsumedEnd = joinedArgument.Span.End;
                        continue;
                    }

                    var argument = ParseArgument(
                        nameToken.Text,
                        allowTypeNameArgument: arguments.Count == 0 ||
                                               !CommandExpectsTypeNameFirstArgumentOnly(nameToken.Text));

                    if (argument is not null)
                    {
                        arguments.Add(argument);
                        lastConsumedEnd = argument.Span.End;
                    }
                }
            }

            var end = arguments.Count > 0 ? arguments[^1].Span.End : nameToken.Span.End;
            return new CommandSyntax(nameToken.Text, nameToken.Span, arguments, TextSpan.FromBounds(nameToken.Span.Start, end))
            {
                ExplicitTypeArguments = explicitTypeArgs,
            };
        }

        private bool TryParseCommaJoinedCommandArgument(out ArgumentSyntax argument)
        {
            argument = null!;

            if (!IsSimpleCommandArgumentToken(Current.Kind) ||
                Peek(1).Kind != SyntaxTokenKind.Comma ||
                !IsSimpleCommandArgumentToken(Peek(2).Kind))
            {
                return false;
            }

            var start = Current.Span.Start;
            var builder = new StringBuilder();
            var end = Current.Span.End;

            while (true)
            {
                if (!TryReadSimpleCommandArgumentSegment(out var segment, out var segmentSpan))
                {
                    return false;
                }

                if (builder.Length > 0)
                {
                    builder.Append(',');
                }

                builder.Append(segment);
                end = segmentSpan.End;

                if (Current.Kind != SyntaxTokenKind.Comma || !IsSimpleCommandArgumentToken(Peek(1).Kind))
                {
                    argument = new BarewordArgumentSyntax(
                        builder.ToString(),
                        TextSpan.FromBounds(start, end));
                    return true;
                }

                end = NextToken().Span.End;
            }
        }

        private bool TryReadSimpleCommandArgumentSegment(out string text, out TextSpan span)
        {
            switch (Current.Kind)
            {
                case SyntaxTokenKind.Bareword:
                    {
                        var token = NextToken();
                        text = token.Value?.ToString() ?? token.Text;
                        span = token.Span;
                        return true;
                    }
                case SyntaxTokenKind.String:
                    {
                        var token = NextToken();
                        text = token.Value?.ToString() ?? token.Text;
                        span = token.Span;
                        return true;
                    }
                case SyntaxTokenKind.Number:
                case SyntaxTokenKind.UnitLiteral:
                    {
                        var token = NextToken();
                        text = token.Value switch
                        {
                            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                            not null => token.Value.ToString()!,
                            _ => token.Text,
                        };
                        span = token.Span;
                        return true;
                    }
                case SyntaxTokenKind.Boolean:
                    {
                        var token = NextToken();
                        text = token.Text.ToLowerInvariant();
                        span = token.Span;
                        return true;
                    }
                case SyntaxTokenKind.Null:
                    {
                        var token = NextToken();
                        text = "null";
                        span = token.Span;
                        return true;
                    }
                default:
                    text = string.Empty;
                    span = Current.Span;
                    return false;
            }
        }

        private static bool IsSimpleCommandArgumentToken(SyntaxTokenKind kind)
        {
            return kind is SyntaxTokenKind.Bareword or SyntaxTokenKind.String or SyntaxTokenKind.Number or SyntaxTokenKind.Boolean or SyntaxTokenKind.Null or SyntaxTokenKind.UnitLiteral;
        }

        private List<ArgumentSyntax> ParseGetArguments(
            int commandEnd,
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var arguments = new List<ArgumentSyntax>();
            var lastConsumedEnd = commandEnd;

            if (IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) ||
                Current.Kind == SyntaxTokenKind.Pipe ||
                Current.Kind == SyntaxTokenKind.Ampersand ||
                LooksLikeRedirectionOperator() ||
                LooksLikeInputRedirection())
            {
                return arguments;
            }

            if (IsLiteralOpenDelimiter(Current))
            {
                arguments.Add(WrapExpressionInBlockArgument(
                    ParseBraceLiteralArgument(implicitCurrentItem: true)));
                return arguments;
            }

            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                arguments.Add(ParseMemberProjectionArgument());
                return arguments;
            }

            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                var expressionArgument = ParseCurrentItemExpressionArgument();

                if (expressionArgument is not null)
                {
                    arguments.Add(expressionArgument);
                }

                if (!IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                    Current.Kind != SyntaxTokenKind.Pipe &&
                    Current.Kind != SyntaxTokenKind.Ampersand &&
                    !LooksLikeRedirectionOperator() &&
                    !LooksLikeInputRedirection() &&
                    !(expressionArgument is not null && IsAtElementBoundary()))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_get_expression_tokens",
                        Title: "This get expression has extra tokens after it.",
                        Span: Current.Span,
                        Label: "get expressions must be a single expression"));
                    SkipToStageBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
                }

                return arguments;
            }

            while (!IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                   Current.Kind != SyntaxTokenKind.Pipe &&
                   Current.Kind != SyntaxTokenKind.Ampersand &&
                   !LooksLikeRedirectionOperator() &&
                   !LooksLikeInputRedirection())
            {
                if (IsAtElementBoundary())
                {
                    break;
                }

                var argument = ParseArgument("get");

                if (argument is not null)
                {
                    arguments.Add(argument);
                    lastConsumedEnd = argument.Span.End;
                }
            }

            return arguments;
        }

        private static bool CommandOptionConsumesFollowingValue(string commandName, ArgumentSyntax? option)
        {
            if (!string.Equals(commandName, "parallel", StringComparison.OrdinalIgnoreCase) ||
                option is not BarewordArgumentSyntax bareword)
            {
                return false;
            }

            return string.Equals(bareword.Value, "--threads", StringComparison.Ordinal) ||
                   string.Equals(bareword.Value, "-t", StringComparison.Ordinal);
        }

        /// <param name="allowRange">
        /// Whether a following <c>..</c> is consumed here (<c>TS-P2-76</c>). True in
        /// *argument* position, where `echo 1..5` has no surrounding expression grammar to
        /// place the operator in. False when called from the operator chain, which now has
        /// its own range level — leaving it true there made `..` bind tighter than every
        /// arithmetic operator, so `1 + 2 .. 5` parsed as `1 + (2 .. 5)` and failed with a
        /// message about `Int32` and `ToshRange` rather than about grouping.
        /// </param>
        private ArgumentSyntax? ParseArgument(
            string? commandName = null,
            bool implicitCurrentItem = false,
            bool allowTypeNameArgument = true,
            bool allowRange = true)
        {
            var result = ParsePrimaryArgument(commandName, implicitCurrentItem, allowTypeNameArgument);

            // Check for range operator: <expr>..<expr> or <expr>..<expr>..<expr>
            if (allowRange && result is not null && Current.Kind == SyntaxTokenKind.DotDot)
            {
                if (result is VariableReferenceArgumentSyntax or MemberAccessArgumentSyntax
                    && result.Span.End == Current.Span.Start)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.accidental_double_dot",
                        Title: "Did you mean '.' (member access) instead of '..' (range)?",
                        Span: Current.Span,
                        Label: "this looks like an accidental double-dot"));
                }

                var range = ParseRangeArgument(result, implicitCurrentItem);
                ValidateLiteralRangeOperands(range);
                return range;
            }

            return result;
        }

        /// <param name="operandsAreExpressions">
        /// True when called from the range *precedence level*, where `1 .. 2 + 3` must read
        /// its bound as `2 + 3` rather than as the primary `2` (<c>TS-P2-76</c>).
        /// </param>
        private RangeArgumentSyntax ParseRangeArgument(
            ArgumentSyntax start,
            bool implicitCurrentItem,
            bool operandsAreExpressions = false)
        {
            ArgumentSyntax? ParseOperand() => operandsAreExpressions
                ? ParseAdditiveExpression(Current.Span.Start, implicitCurrentItem)
                : ParsePrimaryArgument(implicitCurrentItem: implicitCurrentItem);

            NextToken(); // consume first ..

            if (!CanStartPrimaryArgument())
            {
                // Open-ended range: start.. (infinite)
                var span = new TextSpan(start.Span.Start, Current.Span.Start - start.Span.Start);
                return new RangeArgumentSyntax(start, Step: null, End: null, span);
            }

            var second = ParseOperand();

            if (second is null)
            {
                // Fallback: treat as infinite if parsing failed
                var span = new TextSpan(start.Span.Start, Current.Span.Start - start.Span.Start);
                return new RangeArgumentSyntax(start, Step: null, End: null, span);
            }

            // Check for three-part range: start..step..end
            if (Current.Kind == SyntaxTokenKind.DotDot)
            {
                NextToken(); // consume second ..

                if (!CanStartPrimaryArgument())
                {
                    // Open-ended stepped range: start..step.. (infinite with step)
                    var stepSpan = new TextSpan(start.Span.Start, Current.Span.Start - start.Span.Start);
                    return new RangeArgumentSyntax(start, second, End: null, stepSpan);
                }

                var third = ParseOperand();

                if (third is null)
                {
                    // Fallback: treat as infinite stepped range
                    var stepSpan = new TextSpan(start.Span.Start, Current.Span.Start - start.Span.Start);
                    return new RangeArgumentSyntax(start, second, End: null, stepSpan);
                }

                // start..step..end
                var stepSpan2 = new TextSpan(start.Span.Start, third.Span.End - start.Span.Start);
                return new RangeArgumentSyntax(start, second, third, stepSpan2);
            }

            // start..end
            var span2 = new TextSpan(start.Span.Start, second.Span.End - start.Span.Start);
            return new RangeArgumentSyntax(start, Step: null, second, span2);
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
        /// Returns true if the current token can start a primary argument expression
        /// (numbers, strings, variables, parens, etc.). Returns false for tokens like
        /// |, ], ), }, newline, EOF, semicolons, and comprehension keywords (where, for, let)
        /// which indicate the range is open-ended.
        /// </summary>
        private bool CanStartPrimaryArgument()
        {
            if (Current.Kind == SyntaxTokenKind.Bareword)
            {
                // Comprehension keywords after '..' mean the range is open-ended
                var text = Current.Text;
                if (string.Equals(text, "where", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "for", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "let", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return true;
            }

            // Same shared set as above, plus `!`. See
            // CanStartCommandSubexpressionArgument for why this is not a list.
            return Current.Kind == SyntaxTokenKind.Bang ||
                   IsExpressionStartToken(Current.Kind);
        }

        private ArgumentSyntax? ParsePrimaryArgument(
            string? commandName = null,
            bool implicitCurrentItem = false,
            bool allowTypeNameArgument = true)
        {
            switch (Current.Kind)
            {
                case SyntaxTokenKind.Bareword:
                    {
                        if (commandName is not null && LooksLikeSplatArgument())
                        {
                            return ParseSplatArgument();
                        }

                        if (string.Equals(Current.Text, "nameof", StringComparison.Ordinal) &&
                            Peek(1).Kind == SyntaxTokenKind.OpenParen)
                        {
                            return ParseNameOfArgument();
                        }

                        if (string.Equals(Current.Text, "quote", StringComparison.Ordinal) &&
                            Peek(1).Kind == SyntaxTokenKind.OpenBrace)
                        {
                            return ParseQuoteArgument();
                        }

                        if (string.Equals(Current.Text, "func", StringComparison.Ordinal) &&
                            Peek(1).Kind == SyntaxTokenKind.OpenParen)
                        {
                            return ParseAnonymousFunctionArgument();
                        }

                        if (string.Equals(Current.Text, "name-of", StringComparison.OrdinalIgnoreCase) &&
                            Peek(1).Kind == SyntaxTokenKind.Bareword)
                        {
                            return ParseNameOfCommandStyle();
                        }

                        if (IsVariableReferenceLikeToken(Current))
                        {
                            return ParsePostfixChain(ParseVariableReferenceArgument(), implicitCurrentItem);
                        }

                        if (allowTypeNameArgument && commandName is not null && CommandExpectsTypeNameArguments(commandName))
                        {
                            return ParseTypeNameArgument();
                        }

                        if (LooksLikeNewObjectExpression())
                        {
                            return ParsePostfixChain(ParseNewObjectArgument(implicitCurrentItem), implicitCurrentItem);
                        }

                        if (LooksLikeMatchExpression())
                        {
                            return ParseMatchArgument(implicitCurrentItem);
                        }

                        if (LooksLikeIfExpression())
                        {
                            return ParseIfExpressionArgument();
                        }

                        if (LooksLikeStaticMethodCallExpression() &&
                            (!implicitCurrentItem || ShouldPreferStaticDotNetAccessInPredicateContext(Current.Text)))
                        {
                            return ParsePostfixChain(ParseStaticMethodCallArgument(implicitCurrentItem), implicitCurrentItem);
                        }

                        if (LooksLikeStaticMemberAccessExpression() &&
                            (!implicitCurrentItem || ShouldPreferStaticDotNetAccessInPredicateContext(Current.Text)))
                        {
                            // Through ParsePostfixChain like every other primary,
                            // so `A.b.c[0]` and `A.b.c(...)` work. Returning the
                            // bare node meant a trailing `[0]` was left for the
                            // command parser, which read the whole thing —
                            // brackets included — as a command name.
                            return ParsePostfixChain(ParseStaticMemberAccessArgument(), implicitCurrentItem);
                        }

                        if (implicitCurrentItem && !string.IsNullOrEmpty(Current.Text) && (char.IsLetter(Current.Text[0]) || Current.Text[0] == '_'))
                        {
                            return ParsePostfixChain(ParseImplicitCurrentItemArgument(), implicitCurrentItem);
                        }

                        if (commandName is null &&
                            IntrinsicLiteralParser.TryParseExpressionLiteral(Current.Text, out var intrinsicLiteral))
                        {
                            var literalToken = NextToken();
                            return ParsePostfixChain(new LiteralArgumentSyntax(intrinsicLiteral, literalToken.Span), implicitCurrentItem);
                        }

                        var token = NextToken();
                        var bareword = new BarewordArgumentSyntax(token.Text, token.Span);

                        // `TS-P2-01`. A bareword was the one primary that skipped
                        // `ParsePostfixChain`, so a call could not compose: `(f() + 1)`
                        // parsed `f` as a word, left `()` for nobody, and reported "this
                        // operator expression never closes" against the outer paren.
                        // `(f())` worked only because it has no top-level operator and so
                        // took the command-subexpression path instead — which is why the
                        // symptom looked like it was about operators rather than calls.
                        //
                        // Same fix the static-member-access case above already carries,
                        // and scoped the same way: only a *glued* `(` makes this a call,
                        // and only outside command-argument position, where `f (x)` is an
                        // argument list rather than an invocation.
                        if (commandName is null &&
                            Current.Kind == SyntaxTokenKind.OpenParen &&
                            Current.Span.Start == token.Span.End)
                        {
                            return ParsePostfixChain(bareword, implicitCurrentItem);
                        }

                        return bareword;
                    }

                case SyntaxTokenKind.String:
                case SyntaxTokenKind.Number:
                case SyntaxTokenKind.Boolean:
                case SyntaxTokenKind.Null:
                case SyntaxTokenKind.UnitLiteral:
                    {
                        var token = NextToken();
                        return ParsePostfixChain(new LiteralArgumentSyntax(token.Value, token.Span), implicitCurrentItem);
                    }

                case SyntaxTokenKind.InterpolatedString:
                    {
                        var token = NextToken();
                        var parts = token.Value as IReadOnlyList<InterpolatedStringPart>
                                    ?? Array.Empty<InterpolatedStringPart>();
                        return new InterpolatedStringArgumentSyntax(parts, token.Span);
                    }

                case SyntaxTokenKind.LessThanOpenParen:
                    return ParsePostfixChain(ParseInputProcessSubstitutionArgument(), implicitCurrentItem);

                // >( output process substitution: detected as two tokens GreaterThan + OpenParen
                // to avoid conflicting with generic type syntax like Type<T>(args).
                case SyntaxTokenKind.GreaterThan when Peek(1).Kind == SyntaxTokenKind.OpenParen:
                    return ParsePostfixChain(ParseOutputProcessSubstitutionArgument(), implicitCurrentItem);

                case SyntaxTokenKind.DollarOpenParen:
                    return ParsePostfixChain(ParseCommandSubstitutionArgument(), implicitCurrentItem);

                case SyntaxTokenKind.OpenParen:
                    return ParsePostfixChain(ParseParenthesizedArgument(implicitCurrentItem), implicitCurrentItem);

                case SyntaxTokenKind.OpenBracket:
                    return ParsePostfixChain(ParseArrayLiteralArgument(implicitCurrentItem), implicitCurrentItem);

                case SyntaxTokenKind.OpenBraceColon:
                case SyntaxTokenKind.OpenBracePipe:
                case SyntaxTokenKind.OpenBracePercent:
                    return ParsePostfixChain(
                        ParseBraceLiteralArgument(implicitCurrentItem),
                        implicitCurrentItem);

                case SyntaxTokenKind.OpenBrace:
                    return ParseBlockArgument();

                case SyntaxTokenKind.Ampersand when Peek(1).Kind == SyntaxTokenKind.Bareword && IsValidFunctionReferenceName(Peek(1).Text) && Current.Span.End == Peek(1).Span.Start:
                    {
                        var ampToken = NextToken();
                        var nameToken = NextToken();
                        return new FunctionReferenceArgumentSyntax(
                            nameToken.Text,
                            TextSpan.FromBounds(ampToken.Span.Start, nameToken.Span.End));
                    }

                default:
                    if (IsWhereComparisonOperator(Current))
                    {
                        var token = NextToken();
                        return new BarewordArgumentSyntax(token.Text, token.Span);
                    }

                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_token",
                        Title: $"Unexpected token '{Current.Text}'.",
                        Span: Current.Span,
                        Label: "this token does not fit here"));
                    NextToken();
                    return null;
            }
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

        private ArgumentSyntax ParseNameOfArgument()
        {
            var start = Current.Span.Start;
            NextToken(); // consume "nameof"
            NextToken(); // consume "("

            var identifierToken = NextToken();
            TryReduceNameOfOperand(identifierToken, out var identifier, out var isVariableReference, out var isMemberChain);

            if (Current.Kind == SyntaxTokenKind.CloseParen)
            {
                var end = Current.Span.End;
                NextToken(); // consume ")"
                return new NameOfArgumentSyntax(identifier, isVariableReference, TextSpan.FromBounds(start, end), isMemberChain);
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.nameof_missing_close_paren",
                Title: "Expected ')' after nameof identifier.",
                Span: Current.Span,
                Label: "expected ')'"));

            return new NameOfArgumentSyntax(identifier, isVariableReference, TextSpan.FromBounds(start, identifierToken.Span.End), isMemberChain);
        }

        private ArgumentSyntax ParseQuoteArgument()
        {
            var start = Current.Span.Start;
            NextToken(); // consume "quote"

            // Parse the block — it contains a single expression to capture
            var block = ParseRequiredBlock("quote");

            // Extract the single expression from the block
            if (block.Statements.Count == 1 &&
                block.Statements[0] is PipelineStatementSyntax { Pipeline.Stages: [CommandSyntax { Arguments: [var singleArg] }] })
            {
                return new QuoteArgumentSyntax(singleArg, TextSpan.FromBounds(start, block.Span.End));
            }

            if (block.Statements.Count == 1 &&
                block.Statements[0] is PipelineStatementSyntax { Pipeline.Stages: [ExpressionPipelineStageSyntax exprStage] })
            {
                return new QuoteArgumentSyntax(exprStage.Expression, TextSpan.FromBounds(start, block.Span.End));
            }

            // For more complex blocks, wrap the whole block as a BlockArgumentSyntax
            var blockArg = new BlockArgumentSyntax(block, block.Span);
            return new QuoteArgumentSyntax(blockArg, TextSpan.FromBounds(start, block.Span.End));
        }

        private ArgumentSyntax ParseTypeNameArgument()
        {
            var start = Current.Span.Start;
            var typeName = ParseTypeName("type name");
            var end = _tokens[Math.Max(0, _position - 1)].Span.End;
            return new BarewordArgumentSyntax(typeName, TextSpan.FromBounds(start, end));
        }

        private ArgumentSyntax ParseNameOfCommandStyle()
        {
            var start = Current.Span.Start;
            NextToken(); // consume "name-of"

            var identifierToken = NextToken();
            TryReduceNameOfOperand(identifierToken, out var identifier, out var isVariableReference, out var isMemberChain);

            return new NameOfArgumentSyntax(identifier, isVariableReference, TextSpan.FromBounds(start, identifierToken.Span.End), isMemberChain);
        }

        private ArgumentSyntax ParseVariableReferenceArgument()
        {
            var variableToken = NextToken();
            ParseVariableReferenceToken(variableToken, out var name, out var memberPath);

            ArgumentSyntax expression = new VariableReferenceArgumentSyntax(name, GetVariableReferenceSpan(variableToken, name));

            if (!string.IsNullOrEmpty(memberPath))
            {
                expression = ApplyQualifiedMemberChain(expression, memberPath, variableToken.Span);
            }

            return expression;
        }

        private ArgumentSyntax ParseSplatArgument()
        {
            var splatToken = NextToken();
            var innerText = splatToken.Text[3..];
            var innerSpan = new TextSpan(splatToken.Span.Start + 3, innerText.Length);

            if (string.IsNullOrWhiteSpace(innerText))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_splat_target",
                    Title: "Argument splatting requires a variable or collection reference.",
                    Span: splatToken.Span,
                    Label: "write something like '...$tosh.Script.Args' here"));
                return new SplatArgumentSyntax(new BarewordArgumentSyntax(string.Empty, innerSpan), splatToken.Span);
            }

            var innerToken = new SyntaxToken(SyntaxTokenKind.Bareword, innerSpan.Start, innerText, innerText);

            if (!IsVariableReferenceLikeToken(innerToken))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.invalid_splat_target",
                    Title: "Argument splatting currently requires a variable-style reference.",
                    Span: splatToken.Span,
                    Label: "use a target like '...$tosh.Script.Args' or '..._'"));
                return new SplatArgumentSyntax(new BarewordArgumentSyntax(innerText, innerSpan), splatToken.Span);
            }

            ParseVariableReferenceToken(innerToken, out var name, out var memberPath);

            ArgumentSyntax expression = new VariableReferenceArgumentSyntax(name, GetVariableReferenceSpan(innerToken, name));

            if (!string.IsNullOrEmpty(memberPath))
            {
                expression = ApplyQualifiedMemberChain(expression, memberPath, innerSpan);
            }

            return new SplatArgumentSyntax(expression, splatToken.Span);
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

        private ArgumentSyntax ParseMemberAssignmentTarget()
        {
            ArgumentSyntax expression;

            if (IsVariableReferenceLikeToken(Current))
            {
                expression = ParseVariableReferenceArgument();
            }
            else if (Current.Kind == SyntaxTokenKind.Bareword)
            {
                // `TS-P2-51`. A dotted bareword left of `=` is a *static* member path — the
                // same spelling that already reads one (`Math.PI`, `Reactor.Fuel.Mox`). The
                // parser has no symbol table, so it cannot tell `B.S = 5` (a static
                // assignment) from `person.Name = "x"` (a forgotten `$`); rejecting the shape
                // outright made every static effectively read-only after its initializer.
                //
                // Both go to the engine, which knows the difference and raises the missing-`$`
                // hint itself when the head names no type. That is where the *read* of the
                // same spelling already answers — `person.Name` gives
                // `tosh.runtime.variable_reference_requires_dollar` — so this makes a write
                // diagnose where its read does, rather than one phase earlier.
                var token = NextToken();
                expression = new StaticMemberAccessArgumentSyntax(token.Text, token.Span);
            }
            else
            {
                var token = NextToken();
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_assignment_target",
                    Title: "Assignments require a variable or member path target.",
                    Span: token.Span,
                    Label: "write something like '$name = ...' or '$person.Name = ...'"));
                return new VariableReferenceArgumentSyntax(string.Empty, token.Span);
            }

            while (TryConsumePostfixToken(expression.Span.End, out var postfixToken, out var postfixText, out _))
            {
                expression = ApplyQualifiedMemberChain(expression, postfixText, postfixToken.Span, allowMethodCall: false);
            }

            // Allow trailing `[index]` segments — e.g. `$x.config["key"] = …`
            // or the simpler `$x["key"] = …`. Multiple bracket segments are
            // supported by looping.
            while (Current.Kind == SyntaxTokenKind.OpenBracket)
            {
                expression = ParseIndexAccess(expression);
            }

            // A static target carries its own path inside one token (`B.S`), so it satisfies the
            // "must be a member path" rule without a wrapper node. A dotless one does not:
            // `foo = 1` is a variable assignment and belongs to the other statement form.
            var isDottedStaticTarget = expression is StaticMemberAccessArgumentSyntax staticTarget &&
                                       staticTarget.Path.Contains('.', StringComparison.Ordinal);

            if (!isDottedStaticTarget && expression is not MemberAccessArgumentSyntax and not IndexAccessArgumentSyntax)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_member_assignment_target",
                    Title: "This assignment needs a member path like '$person.Name'.",
                    Span: expression.Span,
                    Label: "assign directly to a member path here"));
            }

            return expression;
        }

        private ArgumentSyntax ParseStaticMethodCallArgument(bool implicitCurrentItem = false)
        {
            var methodToken = NextToken();

            // `TS-P2-82`. Read on the same terms as the instance path: a list is one only when
            // `(` follows it, and the position is restored otherwise so `A < b` still compares.
            IReadOnlyList<string>? explicitTypeArguments = null;

            if (Current.Kind == SyntaxTokenKind.LessThan && Current.Span.Start == methodToken.Span.End)
            {
                var savedPosition = _position;
                var savedDiagnosticCount = _diagnostics.Count;
                var (_, parsedArgs, hasAngles) = ParseGenericTypeArgumentsStructured();

                if (hasAngles && parsedArgs.Count > 0 && Current.Kind == SyntaxTokenKind.OpenParen)
                {
                    explicitTypeArguments = parsedArgs;
                }
                else
                {
                    _position = savedPosition;
                    if (_diagnostics.Count > savedDiagnosticCount)
                    {
                        _diagnostics.RemoveRange(savedDiagnosticCount, _diagnostics.Count - savedDiagnosticCount);
                    }
                }
            }

            var arguments = ParseInvocationArguments(implicitCurrentItem);
            var end = arguments.closeParenEnd ?? methodToken.Span.End;
            return new StaticMethodCallArgumentSyntax(
                methodToken.Text,
                arguments.arguments,
                TextSpan.FromBounds(methodToken.Span.Start, end),
                explicitTypeArguments);
        }

        /// <summary>
        /// True when the current bareword is followed by a type-argument list and then a call —
        /// <c>TS-P2-82</c>.
        /// </summary>
        /// <remarks>
        /// Scans without consuming, so a lone <c>A &lt; b</c> is untouched. The list must be glued
        /// to the name and closed before the parenthesis, which is what tells it apart from a
        /// comparison.
        /// </remarks>
        private bool FollowedByTypeArgumentsThenCall()
        {
            if (Peek(1).Kind != SyntaxTokenKind.LessThan ||
                Peek(1).Span.Start != Current.Span.End)
            {
                return false;
            }

            var depth = 0;

            for (var offset = 1; offset < 64; offset++)
            {
                switch (Peek(offset).Kind)
                {
                    case SyntaxTokenKind.LessThan:
                        depth++;
                        break;
                    case SyntaxTokenKind.GreaterThan:
                        depth--;
                        if (depth == 0) return Peek(offset + 1).Kind == SyntaxTokenKind.OpenParen;
                        break;
                    case SyntaxTokenKind.EndOfFile:
                    case SyntaxTokenKind.Pipe:
                    case SyntaxTokenKind.Semicolon:
                    case SyntaxTokenKind.OpenBrace:
                        return false;
                }
            }

            return false;
        }

        private ArgumentSyntax ParseStaticMemberAccessArgument()
        {
            var token = NextToken();
            return new StaticMemberAccessArgumentSyntax(token.Text, token.Span);
        }

        private ArgumentSyntax ParseImplicitCurrentItemArgument()
        {
            var memberToken = NextToken();
            ArgumentSyntax expression = new VariableReferenceArgumentSyntax("_", memberToken.Span);
            return ApplyQualifiedMemberChain(expression, memberToken.Text, memberToken.Span, implicitCurrentItem: true);
        }

        private ArgumentSyntax ParseBlockArgument()
        {
            var block = ParseBlock();
            return new BlockArgumentSyntax(block, block.Span);
        }

        private ArgumentSyntax ParseMemberProjectionArgument()
        {
            var openBrace = NextToken();
            var memberPaths = new List<string>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_projection_separator",
                        Title: "A projected member path is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add a member path here"));
                    NextToken();
                    continue;
                }

                if (Current.Kind != SyntaxTokenKind.Bareword)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_projection_member_path",
                        Title: "Projected fields must be member paths.",
                        Span: Current.Span,
                        Label: "write a member path like 'Name' or 'Parent.Name'"));
                    NextToken();
                    continue;
                }

                memberPaths.Add(NextToken().Text);

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_projection_separator",
                        Title: "Projected member paths must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between projected member paths"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_projection_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this projection never closes",
                    Help: "close the projection with '}' after the last member path."));
                return new MemberProjectionArgumentSyntax(memberPaths, openBrace.Span);
            }

            var closeBrace = NextToken();
            return new MemberProjectionArgumentSyntax(
                memberPaths,
                TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
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

        private PipelineSyntax ParseParenthesizedPipeline(string owner)
        {
            // Accept both parenthesized `(source)` and bare `source` (stops before `{`).
            //
            // `TS-P2-77`. The parenthesized branch is taken only when the group *is* the
            // whole source. It used to be taken whenever the source began with `(`, so it
            // consumed `(1)` out of `for i in (1) .. 3 { … }`, returned, and left the
            // caller expecting `{` where `..` stood — reported as `expected_block`, a
            // message about the body for a defect in the source. A parenthesised left
            // operand is ordinary (`for i in ($n - 1) .. $n`), and wrapping the whole
            // range already worked, so this only ever rejected the shorter spelling.
            if (Current.Kind == SyntaxTokenKind.OpenParen && ParenthesizedGroupIsWholeSource())
            {
                var openParen = NextToken();
                var pipeline = ParsePipeline(
                    untilCloseParen: true,
                    untilCloseBrace: false,
                    untilSemicolon: false,
                    allowExpressionStart: true);

                if (Current.Kind != SyntaxTokenKind.CloseParen)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_closing_parenthesis",
                        Title: "A closing ')' is required here.",
                        Span: openParen.Span,
                        Label: "this parenthesized source never closes",
                        Help: "close the source pipeline with ')' before the block."));
                    return pipeline;
                }

                NextToken();
                return pipeline;
            }

            // Bare source: parse until `{` (the loop body) or end of input.
            if (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                return ParsePipeline(
                    untilCloseParen: false,
                    untilCloseBrace: false,
                    untilSemicolon: false,
                    allowExpressionStart: true,
                    untilOpenBrace: true);
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_parenthesized_source",
                Title: $"The '{owner}' statement requires a source.",
                Span: Current.Span,
                Label: $"write a pipeline source after '{owner}'"));
            return new PipelineSyntax(Array.Empty<PipelineStageSyntax>());
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

        private ArgumentSyntax ParseArrayLiteralArgument(bool implicitCurrentItem = false)
        {
            var openBracket = NextToken();

            // Check for list comprehension: [body <| for $x in source ...]
            if (HasTopLevelComprehensionBeforeClose(SyntaxTokenKind.CloseBracket))
            {
                return ParseListComprehension(openBracket);
            }

            var items = new List<ArgumentSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBracket)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_list_separator",
                        Title: "An array item is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add an array item here"));
                    NextToken();
                    continue;
                }

                if (LooksLikeSpreadElement())
                {
                    items.Add(ParseSpreadElement());

                    if (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken();
                    }

                    continue;
                }

                var item = ParseCollectionValue(implicitCurrentItem);

                if (item is not null)
                {
                    items.Add(item);
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseBracket and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_list_separator",
                        Title: "Array items must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between array items"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBracket)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_bracket",
                    Title: "A closing ']' is required here.",
                    Span: openBracket.Span,
                    Label: "this array literal never closes",
                    Help: "close the array literal with ']' after the last item."));
                return new ArrayLiteralArgumentSyntax(items, openBracket.Span);
            }

            var closeBracket = NextToken();
            return new ArrayLiteralArgumentSyntax(items, TextSpan.FromBounds(openBracket.Span.Start, closeBracket.Span.End));
        }

        /// <summary>
        /// Dispatches on the opening delimiter alone (<c>TS-P2-25</c>). The
        /// <c>LooksLikeSetLiteral</c>/<c>LooksLikeDictLiteral</c>/<c>LooksLikeRecordLiteral</c>
        /// trio this replaced inspected up to two tokens past a bare <c>{</c> to
        /// guess which construct was opening; each literal now says so itself.
        /// </summary>
        private ArgumentSyntax ParseBraceLiteralArgument(bool implicitCurrentItem = false)
        {
            return Current.Kind switch
            {
                SyntaxTokenKind.OpenBraceColon => ParseSetLiteralArgument(implicitCurrentItem),
                SyntaxTokenKind.OpenBracePipe => ParseRecordLiteralArgument(implicitCurrentItem),
                SyntaxTokenKind.OpenBracePercent => ParseDictLiteralArgument(implicitCurrentItem),
                _ => ParseUnexpectedBraceLiteralArgument(),
            };
        }

        private ArgumentSyntax ParseUnexpectedBraceLiteralArgument()
        {
            var token = NextToken();
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.unexpected_token",
                Title: $"Unexpected token '{token.Text}'.",
                Span: token.Span,
                Label: "expected a collection-literal opener: '{:', '{|', or '{%'"));
            return new BarewordArgumentSyntax(token.Text, token.Span);
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

        private ArgumentSyntax ParseSetLiteralArgument(bool implicitCurrentItem)
        {
            var openBrace = NextToken();

            // Check for set comprehension: {: body <| for $x in source ... :}
            if (HasTopLevelComprehensionBeforeClose(SyntaxTokenKind.ColonCloseBrace))
            {
                return ParseSetComprehension(openBrace);
            }

            var items = new List<ArgumentSyntax>();

            while (Current.Kind is not SyntaxTokenKind.EndOfFile
                   and not SyntaxTokenKind.ColonCloseBrace
                   and not SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, ":", StringComparison.Ordinal) &&
                    Peek(1).Kind == SyntaxTokenKind.CloseBrace)
                {
                    break;
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                var item = ParseArgument(implicitCurrentItem: implicitCurrentItem);

                if (item is not null)
                {
                    items.Add(item);
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.ColonCloseBrace
                    and not SyntaxTokenKind.CloseBrace
                    and not SyntaxTokenKind.EndOfFile
                    && !(Current.Kind == SyntaxTokenKind.Bareword
                         && string.Equals(Current.Text, ":", StringComparison.Ordinal)))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_set_separator",
                        Title: "Set items must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between set items"));
                }
            }

            if (Current.Kind == SyntaxTokenKind.ColonCloseBrace)
            {
                var closeBrace = NextToken();
                return new SetLiteralArgumentSyntax(
                    items,
                    TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
            }

            if (TryReportSpacedLiteralCloser(":}", out var spacedCloseSpan))
            {
                return new SetLiteralArgumentSyntax(
                    items,
                    TextSpan.FromBounds(openBrace.Span.Start, spacedCloseSpan.End));
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.missing_set_closing_delimiter",
                Title: "A closing ':}' is required here.",
                Span: Current.Kind == SyntaxTokenKind.CloseBrace ? Current.Span : openBrace.Span,
                Label: "this set literal never closes",
                Help: "close the set literal with ':}' after the last item."));

            if (Current.Kind == SyntaxTokenKind.CloseBrace)
            {
                var recoveryClose = NextToken();
                return new SetLiteralArgumentSyntax(
                    items,
                    TextSpan.FromBounds(openBrace.Span.Start, recoveryClose.Span.End));
            }

            return new SetLiteralArgumentSyntax(items, openBrace.Span);
        }

        private ArgumentSyntax ParseDictLiteralArgument(bool implicitCurrentItem = false)
        {
            // The literal owns the positions between its fields, so it registers
            // as a boundary owner exactly as a block or class body does (TS-P2-24).
            var openBraceTokenIndex = _position;
            var openBrace = NextToken(); // {%
            using var boundaryOwner = PushBoundaryOwner(openBraceTokenIndex);

            // Check for dict comprehension: {% key => value <| for $x in source ... %}
            if (HasTopLevelComprehensionBeforeClose(SyntaxTokenKind.PercentCloseBrace))
            {
                return ParseDictComprehension(openBrace, implicitCurrentItem);
            }

            var entries = new List<DictEntrySyntax>();

            while (Current.Kind is not SyntaxTokenKind.EndOfFile
                   and not SyntaxTokenKind.PercentCloseBrace
                   and not SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, "%", StringComparison.Ordinal) &&
                    Peek(1).Kind == SyntaxTokenKind.CloseBrace)
                {
                    break;
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                var key = ParseArgument(implicitCurrentItem: implicitCurrentItem);

                if (key is null)
                {
                    NextToken();
                    continue;
                }

                if (!IsFatArrow(Current))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_fat_arrow",
                        Title: "Dict entries require '=>' between key and value.",
                        Span: Current.Span,
                        Label: "write '=>' after the key expression"));
                    break;
                }

                ConsumeFatArrow();

                var value = ParseCollectionValue(implicitCurrentItem);

                if (value is not null)
                {
                    entries.Add(new DictEntrySyntax(
                        key,
                        value,
                        TextSpan.FromBounds(key.Span.Start, value.Span.End)));
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (value is not null && IsAtElementBoundary())
                {
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.PercentCloseBrace
                    and not SyntaxTokenKind.CloseBrace
                    and not SyntaxTokenKind.EndOfFile
                    && !(Current.Kind == SyntaxTokenKind.Bareword
                         && string.Equals(Current.Text, "%", StringComparison.Ordinal)))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_dict_entry_separator",
                        Title: "Dict entries must be separated by ',' or a newline.",
                        Span: Current.Span,
                        Label: "insert ',' or a newline between dict entries"));
                }
            }

            if (Current.Kind == SyntaxTokenKind.PercentCloseBrace)
            {
                var closeBrace = NextToken();
                return new DictLiteralArgumentSyntax(
                    entries,
                    TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
            }

            if (TryReportSpacedLiteralCloser("%}", out var spacedCloseSpan))
            {
                return new DictLiteralArgumentSyntax(
                    entries,
                    TextSpan.FromBounds(openBrace.Span.Start, spacedCloseSpan.End));
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.missing_dict_closing_delimiter",
                Title: "A closing '%}' is required here.",
                Span: Current.Kind == SyntaxTokenKind.CloseBrace ? Current.Span : openBrace.Span,
                Label: "this dict literal never closes",
                Help: "close the dict literal with '%}' after the last entry."));

            if (Current.Kind == SyntaxTokenKind.CloseBrace)
            {
                var recoveryClose = NextToken();
                return new DictLiteralArgumentSyntax(
                    entries,
                    TextSpan.FromBounds(openBrace.Span.Start, recoveryClose.Span.End));
            }

            return new DictLiteralArgumentSyntax(entries, openBrace.Span);
        }

        private ArgumentSyntax ParseRecordLiteralArgument(bool implicitCurrentItem = false)
        {
            // The literal owns the positions between its fields, so it registers
            // as a boundary owner exactly as a block or class body does (TS-P2-24).
            var openBraceTokenIndex = _position;
            var openBrace = NextToken(); // {|
            using var boundaryOwner = PushBoundaryOwner(openBraceTokenIndex);
            var fields = new List<RecordEntrySyntax>();

            while (Current.Kind is not SyntaxTokenKind.EndOfFile
                   and not SyntaxTokenKind.PipeCloseBrace
                   and not SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Pipe &&
                    Peek(1).Kind == SyntaxTokenKind.CloseBrace)
                {
                    break;
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                // Spread entry: {| ...$a, ...$b |}
                if (LooksLikeSpreadElement())
                {
                    var spread = ParseSpreadElement();
                    fields.Add(new SpreadRecordEntrySyntax(spread.Value, spread.Span));

                    if (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken();
                    }
                    else if (IsAtElementBoundary())
                    {
                        // newline separator
                    }

                    continue;
                }

                // Computed property: {| ($expr) = value |}
                if (Current.Kind == SyntaxTokenKind.OpenParen)
                {
                    var openParen = NextToken();
                    var nameExpr = ParseArgument(implicitCurrentItem: implicitCurrentItem);

                    if (Current.Kind == SyntaxTokenKind.CloseParen)
                    {
                        NextToken();
                    }
                    else
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.expected_closing_paren",
                            Title: "A closing ')' is required after the computed property name.",
                            Span: Current.Span,
                            Label: "close the parenthesized expression"));
                    }

                    ExpectRecordFieldSeparator("Computed record fields use '=' or ':' between the key expression and value.");
                    var compValue = ParseArgument(implicitCurrentItem: implicitCurrentItem);

                    if (nameExpr is not null && compValue is not null)
                    {
                        fields.Add(new ComputedRecordFieldSyntax(
                            nameExpr,
                            compValue,
                            TextSpan.FromBounds(openParen.Span.Start, compValue.Span.End)));
                    }

                    if (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken();
                    }
                    else if (compValue is not null && IsAtElementBoundary())
                    {
                        // newline separator
                    }

                    continue;
                }

                string fieldName;
                TextSpan fieldStart;
                bool fieldNameHasTrailingColon = false;

                if (Current.Kind == SyntaxTokenKind.Bareword)
                {
                    var nameToken = NextToken();
                    fieldName = nameToken.Text;
                    fieldStart = nameToken.Span;

                    // `name:` shorthand — the lexer didn't break on ':' so
                    // it ended up glued to the field name. Strip it and
                    // treat as if a separate ':' separator was present.
                    if (fieldName.Length > 1 && fieldName.EndsWith(':') && !fieldName.EndsWith("::"))
                    {
                        fieldName = fieldName[..^1];
                        fieldNameHasTrailingColon = true;
                    }
                }
                else if (Current.Kind == SyntaxTokenKind.String)
                {
                    var nameToken = NextToken();
                    fieldName = nameToken.Value?.ToString() ?? string.Empty;
                    fieldStart = nameToken.Span;
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_record_field_name",
                        Title: "Record literals require a field name before '='.",
                        Span: Current.Span,
                        Label: "write a field name like 'Name = value'"));
                    NextToken();
                    continue;
                }

                if (!fieldNameHasTrailingColon)
                {
                    ExpectRecordFieldSeparator("Record fields use '=' or ':' between the field name and value.");
                }
                var value = ParseCollectionValue(implicitCurrentItem);

                if (value is not null)
                {
                    fields.Add(new RecordFieldSyntax(
                        fieldName,
                        value,
                        TextSpan.FromBounds(fieldStart.Start, value.Span.End)));
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (value is not null && IsAtElementBoundary())
                {
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.PipeCloseBrace
                    and not SyntaxTokenKind.CloseBrace
                    and not SyntaxTokenKind.EndOfFile
                    && !(Current.Kind == SyntaxTokenKind.Pipe
                         && Peek(1).Kind == SyntaxTokenKind.CloseBrace))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_record_field_separator",
                        Title: "Record fields must be separated by ',' or a newline.",
                        Span: Current.Span,
                        Label: "insert ',' or a newline between record fields"));
                }
            }

            if (Current.Kind == SyntaxTokenKind.PipeCloseBrace)
            {
                var closeBrace = NextToken();
                return new RecordLiteralArgumentSyntax(
                    fields,
                    TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
            }

            if (TryReportSpacedLiteralCloser("|}", out var spacedCloseSpan))
            {
                return new RecordLiteralArgumentSyntax(
                    fields,
                    TextSpan.FromBounds(openBrace.Span.Start, spacedCloseSpan.End));
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.missing_record_closing_delimiter",
                Title: "A closing '|}' is required here.",
                Span: Current.Kind == SyntaxTokenKind.CloseBrace ? Current.Span : openBrace.Span,
                Label: "this record literal never closes",
                Help: "close the record literal with '|}' after the last field."));

            if (Current.Kind == SyntaxTokenKind.CloseBrace)
            {
                var recoveryClose = NextToken();
                return new RecordLiteralArgumentSyntax(
                    fields,
                    TextSpan.FromBounds(openBrace.Span.Start, recoveryClose.Span.End));
            }

            return new RecordLiteralArgumentSyntax(fields, openBrace.Span);
        }

        private ArgumentSyntax ParseNewObjectArgument(bool implicitCurrentItem = false)
        {
            var newToken = NextToken();

            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_type_name",
                    Title: "Object construction requires a CLR type name.",
                    Span: Current.Span,
                    Label: "write a type after 'new', like 'new string(\"hello\")'"));
                return new NewObjectArgumentSyntax(string.Empty, Array.Empty<ArgumentSyntax>(), newToken.Span);
            }

            var typeToken = NextToken();
            var bareTypeName = typeToken.Text;
            string typeName = bareTypeName;
            IReadOnlyList<string>? typeArguments = null;

            if (Current.Kind == SyntaxTokenKind.LessThan)
            {
                var (display, args, hasAngles) = ParseGenericTypeArgumentsStructured();
                typeName = bareTypeName + display;
                if (hasAngles) typeArguments = args;
            }

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_constructor_parenthesis",
                    Title: "Object construction uses C#-style parentheses.",
                    Span: typeToken.Span,
                    Label: "add '(' after the type name",
                    Help: "try 'new SomeType(...)' instead of command-style construction."));
                return new NewObjectArgumentSyntax(typeName, Array.Empty<ArgumentSyntax>(), TextSpan.FromBounds(newToken.Span.Start, typeToken.Span.End), bareTypeName, typeArguments);
            }

            var openParen = NextToken();
            var arguments = new List<ArgumentSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseParen)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_constructor_separator",
                        Title: "A constructor argument is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add an argument here"));
                    NextToken();
                    continue;
                }

                // `TS-P2-21`. A constructor takes named arguments exactly as a method does.
                if (TryParseNamedArgument(implicitCurrentItem, out var namedConstructorArgument))
                {
                    if (namedConstructorArgument is not null)
                    {
                        arguments.Add(namedConstructorArgument);
                    }
                }
                else
                {
                    var argument = HasTopLevelOperatorBeforeCommaOrCloseParen()
                        ? ParseOperatorExpression(Current.Span.Start, implicitCurrentItem)
                        : ParseArgument(implicitCurrentItem: implicitCurrentItem);

                    if (argument is not null)
                    {
                        arguments.Add(argument);
                    }
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseParen and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_constructor_separator",
                        Title: "Constructor arguments must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between constructor arguments"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this constructor call never closes",
                    Help: "close the argument list with ')' after the last constructor argument."));
                return new NewObjectArgumentSyntax(
                    typeName,
                    arguments,
                    TextSpan.FromBounds(newToken.Span.Start, arguments.Count > 0 ? arguments[^1].Span.End : typeToken.Span.End),
                    bareTypeName,
                    typeArguments);
            }

            var constructorCloseParen = NextToken();
            return new NewObjectArgumentSyntax(
                typeName,
                arguments,
                TextSpan.FromBounds(newToken.Span.Start, constructorCloseParen.Span.End),
                bareTypeName,
                typeArguments);
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

        private ArgumentSyntax ApplyQualifiedMemberChain(
            ArgumentSyntax expression,
            string qualifiedText,
            TextSpan qualifiedSpan,
            bool implicitCurrentItem = false,
            bool allowMethodCall = true,
            bool nullSafe = false)
        {
            var segments = qualifiedText
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (segments.Length == 0)
            {
                return expression;
            }

            for (var index = 0; index < segments.Length; index++)
            {
                expression = ApplyMemberOrMethodPostfix(
                    expression,
                    segments[index],
                    qualifiedSpan,
                    implicitCurrentItem,
                    allowMethodCall: allowMethodCall && index == segments.Length - 1,
                    nullSafe: nullSafe && index == 0);
            }

            return expression;
        }

        private ArgumentSyntax ApplyMemberOrMethodPostfix(
            ArgumentSyntax expression,
            string postfixText,
            TextSpan postfixSpan,
            bool implicitCurrentItem = false,
            bool allowMethodCall = true,
            bool nullSafe = false)
        {
            // Explicit type arguments, written between the member name and its argument list:
            // `$a.m<int>(11)`. Read speculatively and rolled back if no closing `>` is found,
            // the same way a command name reads them — `a < b` must keep parsing as a
            // comparison.
            IReadOnlyList<string>? explicitTypeArguments = null;
            if (allowMethodCall &&
                Current.Kind == SyntaxTokenKind.LessThan &&
                Current.Span.Start == postfixSpan.End)
            {
                var savedPosition = _position;
                var savedDiagnosticCount = _diagnostics.Count;
                var (_, parsedArgs, hasAngles) = ParseGenericTypeArgumentsStructured();

                // Only a type argument list *immediately* followed by `(` is one: `$a.m<int>`
                // with nothing after it is a comparison against a member.
                if (hasAngles && parsedArgs.Count > 0 && Current.Kind == SyntaxTokenKind.OpenParen)
                {
                    explicitTypeArguments = parsedArgs;
                }
                else
                {
                    _position = savedPosition;
                    if (_diagnostics.Count > savedDiagnosticCount)
                    {
                        _diagnostics.RemoveRange(savedDiagnosticCount, _diagnostics.Count - savedDiagnosticCount);
                    }
                }
            }

            if (allowMethodCall &&
                Current.Kind == SyntaxTokenKind.OpenParen &&
                (Current.Span.Start == postfixSpan.End || explicitTypeArguments is not null))
            {
                if (!IsValidIdentifier(postfixText))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.invalid_method_name",
                        Title: "Method calls need a single method name after '.'.",
                        Span: postfixSpan,
                        Label: $"'{postfixText}' is not a valid method name"));
                }

                var arguments = ParseInvocationArguments(implicitCurrentItem);
                var end = arguments.closeParenEnd ?? postfixSpan.End;
                return new MethodCallArgumentSyntax(
                    expression,
                    postfixText,
                    arguments.arguments,
                    TextSpan.FromBounds(expression.Span.Start, end),
                    NullSafe: nullSafe,
                    ExplicitTypeArguments: explicitTypeArguments);
            }

            return new MemberAccessArgumentSyntax(
                expression,
                postfixText,
                TextSpan.FromBounds(expression.Span.Start, postfixSpan.End),
                NullSafe: nullSafe);
        }

        private (IReadOnlyList<ArgumentSyntax> arguments, int? closeParenEnd) ParseInvocationArguments(bool implicitCurrentItem = false)
        {
            var openParen = NextToken();
            var arguments = new List<ArgumentSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseParen)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_argument_separator",
                        Title: "An argument is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add an argument here"));
                    NextToken();
                    continue;
                }

                if (LooksLikeSplatArgument())
                {
                    arguments.Add(ParseSplatArgument());
                }
                // Named argument: identifier = value
                else if (TryParseNamedArgument(implicitCurrentItem, out var namedArgument))
                {
                    if (namedArgument is not null)
                    {
                        arguments.Add(namedArgument);
                    }
                }
                else
                {
                    var argument = HasTopLevelOperatorBeforeCommaOrCloseParen()
                        ? ParseOperatorExpression(Current.Span.Start, implicitCurrentItem)
                        : ParseArgument(implicitCurrentItem: implicitCurrentItem);

                    if (argument is not null)
                    {
                        arguments.Add(argument);
                    }
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseParen and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_argument_separator",
                        Title: "Arguments must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between arguments"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this call never closes",
                    Help: "close the argument list with ')' after the last argument."));
                return (arguments, null);
            }

            var closeParen = NextToken();
            return (arguments, closeParen.Span.End);
        }

        private ArgumentSyntax ParseParenthesizedArgument(bool implicitCurrentItem = false)
        {
            var openParen = NextToken();

            if (HasTopLevelCommaBeforeCloseParen())
            {
                return ParseTupleLiteralArgument(openParen, implicitCurrentItem);
            }

            // Single named argument: (name = value)
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                IsValidIdentifier(Current.Text) &&
                !Current.Text.StartsWith("$", StringComparison.Ordinal) &&
                Peek(1).Kind == SyntaxTokenKind.Bareword && Peek(1).Text == "=")
            {
                return ParseTupleLiteralArgument(openParen, implicitCurrentItem);
            }

            // Generator comprehension: (body <| for $x in source ...)
            if (HasTopLevelComprehensionBeforeCloseParen())
            {
                return ParseGeneratorComprehension(openParen);
            }

            if (HasTopLevelOperatorBeforeCloseParen())
            {
                var expression = ParseOperatorExpression(openParen.Span.Start, implicitCurrentItem);

                if (Current.Kind == SyntaxTokenKind.CloseParen)
                {
                    var operatorCloseParen = NextToken();
                    return expression with { Span = TextSpan.FromBounds(openParen.Span.Start, operatorCloseParen.Span.End) };
                }

                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this operator expression never closes",
                    Help: "close the expression with ')' after the right-hand operand."));
                return expression;
            }

            if (implicitCurrentItem &&
                !GroupOwnsStageDivision(openParen) &&
                !LooksLikeParenthesizedCommandSubexpression())
            {
                var predicateExpression = ParseWherePredicateExpression();

                if (Current.Kind == SyntaxTokenKind.CloseParen)
                {
                    var predicateCloseParen = NextToken();
                    return predicateExpression with { Span = TextSpan.FromBounds(openParen.Span.Start, predicateCloseParen.Span.End) };
                }

                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this subexpression never closes",
                    Help: "close the subexpression with ')' after the inner expression."));
                return predicateExpression;
            }

            // Allow (quote { ... }) to parse as a quoted expression instead of a command.
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "quote", StringComparison.Ordinal) &&
                Peek(1).Kind == SyntaxTokenKind.OpenBrace)
            {
                var quoted = ParseQuoteArgument();
                if (Current.Kind == SyntaxTokenKind.CloseParen)
                {
                    var quoteCloseParen = NextToken();
                    return quoted with { Span = TextSpan.FromBounds(openParen.Span.Start, quoteCloseParen.Span.End) };
                }

                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this subexpression never closes",
                    Help: "close the subexpression with ')' after quote expression."));
                return quoted;
            }

            var pipeline = ParsePipeline(
                untilCloseParen: true,
                untilCloseBrace: false,
                untilSemicolon: false,
                allowExpressionStart: true);

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this subexpression never closes",
                    Help: "close the subexpression with ')' after the inner pipeline."));
                return new SubexpressionArgumentSyntax(pipeline, openParen.Span);
            }

            var subexpressionCloseParen = NextToken();
            return new SubexpressionArgumentSyntax(
                pipeline,
                TextSpan.FromBounds(openParen.Span.Start, subexpressionCloseParen.Span.End));
        }

        /// <summary>
        /// Argument position admits everything that can begin an expression, plus
        /// <c>!</c>, which cannot begin a statement. Expressed in terms of the
        /// shared predicate rather than as a second list, so the two cannot drift
        /// (<c>TS-P2-06</c>) — and so <c>TS-P3-09</c> has one place to change when
        /// <c>!</c> becomes a prefix operator.
        /// </summary>
        private static bool CanStartCommandSubexpressionArgument(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bang ||
                   IsExpressionStartToken(token.Kind);
        }

        private ArgumentSyntax ParseTupleLiteralArgument(SyntaxToken openParen, bool implicitCurrentItem)
        {
            var items = new List<ArgumentSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseParen)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                // Named argument: identifier = value
                if (TryParseNamedArgument(implicitCurrentItem, out var namedItem))
                {
                    if (namedItem is not null)
                    {
                        items.Add(namedItem);
                    }
                }
                else
                {
                    var item = HasTopLevelOperatorBeforeCommaOrCloseParen()
                        ? ParseOperatorExpression(Current.Span.Start, implicitCurrentItem)
                        : ParseArgument(implicitCurrentItem: implicitCurrentItem);

                    if (item is not null)
                    {
                        items.Add(item);
                    }
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseParen and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_tuple_separator",
                        Title: "Tuple elements must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between tuple elements"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this tuple literal never closes",
                    Help: "close the tuple with ')' after the last element."));
                return new TupleLiteralArgumentSyntax(items, openParen.Span);
            }

            var closeParen = NextToken();
            return new TupleLiteralArgumentSyntax(
                items,
                TextSpan.FromBounds(openParen.Span.Start, closeParen.Span.End));
        }

        private ArgumentSyntax ParseCommandSubstitutionArgument()
        {
            var openParen = NextToken();

            var pipeline = ParsePipeline(
                untilCloseParen: true,
                untilCloseBrace: false,
                untilSemicolon: false,
                allowExpressionStart: true);

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this command substitution never closes",
                    Help: "close the command substitution with ')' after the inner pipeline."));
                return new CommandSubstitutionArgumentSyntax(pipeline, openParen.Span);
            }

            var closeParen = NextToken();
            return new CommandSubstitutionArgumentSyntax(
                pipeline,
                TextSpan.FromBounds(openParen.Span.Start, closeParen.Span.End));
        }

        private ArgumentSyntax ParseInputProcessSubstitutionArgument()
        {
            var openParen = NextToken();

            var pipeline = ParsePipeline(
                untilCloseParen: true,
                untilCloseBrace: false,
                untilSemicolon: false,
                allowExpressionStart: true);

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this process substitution never closes",
                    Help: "close the process substitution with ')' after the inner pipeline."));
                return new InputProcessSubstitutionArgumentSyntax(pipeline, openParen.Span);
            }

            var closeParen = NextToken();
            return new InputProcessSubstitutionArgumentSyntax(
                pipeline,
                TextSpan.FromBounds(openParen.Span.Start, closeParen.Span.End));
        }

        private ArgumentSyntax ParseOutputProcessSubstitutionArgument()
        {
            // >( is parsed as two tokens: GreaterThan + OpenParen
            var greaterThan = NextToken(); // consume >
            NextToken(); // consume (

            var pipeline = ParsePipeline(
                untilCloseParen: true,
                untilCloseBrace: false,
                untilSemicolon: false,
                allowExpressionStart: true);

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: greaterThan.Span,
                    Label: "this process substitution never closes",
                    Help: "close the process substitution with ')' after the inner pipeline."));
                return new OutputProcessSubstitutionArgumentSyntax(pipeline, greaterThan.Span);
            }

            var closeParenToken = NextToken();
            return new OutputProcessSubstitutionArgumentSyntax(
                pipeline,
                TextSpan.FromBounds(greaterThan.Span.Start, closeParenToken.Span.End));
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

        private ArgumentSyntax? ParseArgumentOperand(bool implicitCurrentItem = false)
        {
            if (Current.Kind is SyntaxTokenKind.CloseParen
                or SyntaxTokenKind.CloseBrace
                or SyntaxTokenKind.CloseBracket
                or SyntaxTokenKind.ColonCloseBrace
                or SyntaxTokenKind.PipeCloseBrace
                or SyntaxTokenKind.PercentCloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_operand",
                    Title: "Expected an operand in this expression.",
                    Span: Current.Span,
                    Label: "operators need a value on both sides"));
                return null;
            }

            return ParseArgument(implicitCurrentItem: implicitCurrentItem, allowRange: false);
        }

        private BlockArgumentSyntax ParsePredicateBlockArgument()
        {
            var openBrace = NextToken();
            var statements = new List<StatementSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                var cursorBefore = _position;
                var expression = ParseWherePredicateExpression();

                if (expression is not null)
                {
                    var stage = new ExpressionPipelineStageSyntax(expression, expression.Span);
                    var pipeline = new PipelineSyntax([stage]);
                    statements.Add(new PipelineStatementSyntax(pipeline, expression.Span));
                }

                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (expression is not null && IsAtElementBoundary())
                {
                    continue;
                }

                if (Current.Kind != SyntaxTokenKind.CloseBrace && Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_predicate_separator",
                        Title: "Predicate expressions must be separated by ';' or a newline.",
                        Span: Current.Span,
                        Label: "insert ';' or a newline between predicate expressions"));

                    // Guarantee forward progress: if the predicate parse consumed nothing,
                    // skip the offending token so we don't loop forever.
                    if (_position == cursorBefore)
                    {
                        NextToken();
                    }
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this predicate block never closes",
                    Help: "close the predicate block with '}' after the last clause."));
                var openBlock = new BlockSyntax(statements, openBrace.Span);
                return new BlockArgumentSyntax(openBlock, openBrace.Span);
            }

            var closeBrace = NextToken();
            var block = new BlockSyntax(statements, TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
            return new BlockArgumentSyntax(block, block.Span);
        }

        /// <param name="singleExpressionBody">
        /// Parses exactly one stage and stops, rather than continuing across
        /// stage separators. Used by an argument-position <c>=&gt;</c> body, where
        /// everything after the body's expression — a <c>|</c> or a following
        /// argument — belongs to the enclosing command, not to the body
        /// (<c>TS-P2-26</c>).
        /// </param>
        private PipelineSyntax ParsePipeline(
            bool untilCloseParen,
            bool untilCloseBrace,
            bool untilSemicolon,
            bool allowExpressionStart,
            bool untilOpenBrace = false,
            bool singleExpressionBody = false)
        {
            var stages = new List<PipelineStageSyntax>();
            List<RedirectionSyntax>? redirections = null;
            InputRedirectionSyntax? inputRedirection = null;
            var isBackground = false;
            var pipelineStartPosition = _position;
            var pendingSeparator = PendingPipelineSeparator.None;
            SyntaxToken? pendingSeparatorToken = null;

            while (!IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon, untilOpenBrace))
            {
                // A LiteScript boundary is a candidate until the recursive
                // grammar has made progress. This guard therefore cannot
                // split a required operand or parser-owned continuation at
                // the pipeline's first token, but it does stop a malformed
                // stage from consuming the next proven top-level statement.
                if (_isParsingTopLevelStatement &&
                    _position != pipelineStartPosition &&
                    IsCurrentTopLevelLiteStatementStart())
                {
                    break;
                }

                // A separator that reaches the top of the loop has no stage
                // before it (leading or consecutive). Consume `|>` as one
                // structural unit and keep at most the newest separator as
                // recovery state for the following valid command.
                if (Current.Kind == SyntaxTokenKind.Pipe)
                {
                    var isPipeForward = IsCurrentPipeForwardSeparator();
                    var separatorToken = NextToken();
                    if (isPipeForward)
                    {
                        NextToken(); // consume the adjacent >
                    }

                    if (pendingSeparator != PendingPipelineSeparator.None)
                    {
                        AddMissingPipelineStageDiagnostic(
                            pendingSeparator,
                            pendingSeparatorToken!);
                    }
                    else
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.unexpected_pipeline_separator",
                            Title: "Unexpected pipeline separator.",
                            Span: separatorToken.Span,
                            Label: "remove this separator or put a stage before it"));
                    }

                    if (stages.Count > 0)
                    {
                        pendingSeparator = isPipeForward
                            ? PendingPipelineSeparator.PipeForward
                            : PendingPipelineSeparator.Pipe;
                        pendingSeparatorToken = separatorToken;
                    }
                    else
                    {
                        pendingSeparator = PendingPipelineSeparator.None;
                        pendingSeparatorToken = null;
                    }

                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Ampersand)
                {
                    if (pendingSeparator != PendingPipelineSeparator.None)
                    {
                        AddMissingPipelineStageDiagnostic(
                            pendingSeparator,
                            pendingSeparatorToken!);
                        pendingSeparator = PendingPipelineSeparator.None;
                        pendingSeparatorToken = null;
                    }

                    // &name at the start of a pipeline is a function reference expression, not background.
                    if (stages.Count == 0 &&
                        allowExpressionStart &&
                        LooksLikeFunctionReferenceArgument())
                    {
                        // Fall through to let ParsePipelineStage handle it as an expression.
                    }
                    else if (stages.Count == 0)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.unexpected_background_operator",
                            Title: "Unexpected background operator.",
                            Span: Current.Span,
                            Label: "remove this '&' or put a pipeline before it"));
                        NextToken();
                        isBackground = true;
                        break;
                    }
                    else
                    {
                        NextToken();
                        isBackground = true;
                        break;
                    }
                }

                var stage = ParsePipelineStage(
                    allowExpressionStage: allowExpressionStart && stages.Count == 0,
                    stopAtCloseParen: untilCloseParen,
                    stopAtCloseBrace: untilCloseBrace,
                    stopAtSemicolon: untilSemicolon);

                if (stage is not null)
                {
                    stages.Add(
                        pendingSeparator == PendingPipelineSeparator.PipeForward &&
                        stage is CommandSyntax command
                            ? new PipeForwardStageSyntax(command, command.Span)
                            : stage);
                    pendingSeparator = PendingPipelineSeparator.None;
                    pendingSeparatorToken = null;
                }

                // Check for redirection operators after a pipeline stage
                while (LooksLikeRedirectionOperator() || LooksLikeInputRedirection())
                {
                    if (LooksLikeInputRedirection())
                    {
                        var input = TryParseInputRedirection();
                        if (input is not null)
                        {
                            if (inputRedirection is not null)
                            {
                                _diagnostics.Add(new SyntaxDiagnostic(
                                    Code: "tosh.parser.duplicate_input_redirection",
                                    Title: "Only one input redirection is allowed per pipeline.",
                                    Span: input.Span,
                                    Label: "remove this input redirection"));
                            }

                            inputRedirection = input;
                        }

                        continue;
                    }

                    var redirection = TryParseRedirection();
                    if (redirection is not null)
                    {
                        redirections ??= new List<RedirectionSyntax>();
                        redirections.Add(redirection);
                    }
                }

                if (Current.Kind == SyntaxTokenKind.Ampersand)
                {
                    if (pendingSeparator != PendingPipelineSeparator.None)
                    {
                        AddMissingPipelineStageDiagnostic(
                            pendingSeparator,
                            pendingSeparatorToken!);
                        pendingSeparator = PendingPipelineSeparator.None;
                        pendingSeparatorToken = null;
                    }

                    NextToken();
                    isBackground = true;
                    break;
                }

                if (Current.Kind == SyntaxTokenKind.Pipe)
                {
                    // An argument-position `=>` body ends here: the pipe is the
                    // enclosing pipeline's separator, not part of the body. Without
                    // this, `map func(x) => ($x * 2) | count` parsed `| count` into
                    // the lambda, so each invocation counted its own single value
                    // and the outer stage never ran — which left `iterate`/`recur`
                    // unbounded and exhausted memory (TS-P2-26).
                    if (singleExpressionBody)
                    {
                        break;
                    }

                    if (pendingSeparator != PendingPipelineSeparator.None)
                    {
                        AddMissingPipelineStageDiagnostic(
                            pendingSeparator,
                            pendingSeparatorToken!);
                    }

                    var isPipeForward = IsCurrentPipeForwardSeparator();
                    pendingSeparatorToken = NextToken();
                    if (isPipeForward)
                    {
                        NextToken(); // consume the adjacent >
                    }

                    pendingSeparator = isPipeForward
                        ? PendingPipelineSeparator.PipeForward
                        : PendingPipelineSeparator.Pipe;
                    continue;
                }

                if (stage is not null && IsAtElementBoundary())
                {
                    break;
                }

                if (IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon, untilOpenBrace))
                {
                    break;
                }

                if (stage is ExpressionPipelineStageSyntax)
                {
                    // An argument-position `=>` body is one expression. Whatever
                    // follows belongs to the enclosing command — in
                    // `invoke func(x) => ($x * 2) 21` the `21` is `invoke`'s second
                    // argument, not a second stage of the body — so break rather
                    // than reporting a missing separator.
                    if (singleExpressionBody)
                    {
                        break;
                    }

                    // Allow `<expr> if <cond>` / `<expr> unless <cond>` postfix
                    // conditionals to fall back to the outer statement parser.
                    if (Current.Kind == SyntaxTokenKind.Bareword &&
                        (string.Equals(Current.Text, "if", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(Current.Text, "unless", StringComparison.OrdinalIgnoreCase)))
                    {
                        break;
                    }

                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_pipeline_separator",
                        Title: "Expression pipeline stages must be separated by '|'.",
                        Span: Current.Span,
                        Label: "insert '|' before the next command"));
                    SkipToStageBoundary(untilCloseParen, untilCloseBrace, untilSemicolon);
                    continue;
                }
            }

            if (pendingSeparator != PendingPipelineSeparator.None)
            {
                AddMissingPipelineStageDiagnostic(
                    pendingSeparator,
                    pendingSeparatorToken!);
            }

            return new PipelineSyntax(stages, redirections, inputRedirection, isBackground);
        }

        private void AddMissingPipelineStageDiagnostic(
            PendingPipelineSeparator separator,
            SyntaxToken separatorToken)
        {
            var isPipeForward = separator == PendingPipelineSeparator.PipeForward;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: isPipeForward
                    ? "tosh.parser.missing_command_after_pipe_forward"
                    : "tosh.parser.missing_command_after_pipe",
                Title: isPipeForward
                    ? "A command is required after '|>'."
                    : "A command is required after '|'.",
                Span: separatorToken.Span,
                Label: "a pipeline cannot end here",
                Help: isPipeForward
                    ? "add a command after '|>'."
                    : "add another command after the pipe."));
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

        private int SkipAdjacentGenericTypeArguments(int startIndex)
        {
            if (startIndex < 0 || startIndex + 1 >= _tokens.Count) return startIndex;
            var name = _tokens[startIndex];
            var lt = _tokens[startIndex + 1];
            if (name.Kind != SyntaxTokenKind.Bareword) return startIndex;
            if (lt.Kind != SyntaxTokenKind.LessThan) return startIndex;
            if (name.Span.End != lt.Span.Start) return startIndex;

            var depth = 1;
            var index = startIndex + 2;
            while (index < _tokens.Count)
            {
                var token = _tokens[index];
                switch (token.Kind)
                {
                    case SyntaxTokenKind.LessThan:
                        depth++;
                        index++;
                        continue;
                    case SyntaxTokenKind.GreaterThan:
                        depth--;
                        index++;
                        if (depth == 0) return index;
                        continue;
                    case SyntaxTokenKind.GreaterThanGreaterThan:
                        depth -= 2;
                        index++;
                        if (depth <= 0) return index;
                        continue;
                    case SyntaxTokenKind.Bareword:
                    case SyntaxTokenKind.Comma:
                        index++;
                        continue;
                    default:
                        // Any other token shape — bail; this isn't a
                        // generic-argument list.
                        return startIndex;
                }
            }
            return startIndex;
        }

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

        /// <summary>
        /// Commands whose bareword arguments name types rather than values.
        /// </summary>
        /// <remarks>
        /// `TS-P2-55`. <c>cast</c> is the odd one out: it takes a type and then *values*, while
        /// the rest take type names throughout. Treating every one of its arguments as a type
        /// name meant `cast int Fuel.Uranium` handed the literal text "Fuel.Uranium" to the
        /// conversion — the same spelling `echo Fuel.Uranium` resolves to the enum member. So
        /// the rule is asked about a position, and the main argument loop is the one caller that
        /// knows which position it is at.
        /// </remarks>
        private static bool CommandExpectsTypeNameArguments(string commandName)
        {
            return commandName is
                "cast" or
                "constructors" or
                "describe-type" or
                "help" or
                "members" or
                "methods" or
                "get-methods" or
                // `TS-P2-68`. `which` asks about a *name*, so a dotted bareword must arrive as
                // text. Without this it resolved to the command object first and `which` was
                // handed something whose `ToString()` is not a name — so
                // `which ToastLib.Filesystem.GetFileName` printed nothing while the quoted form
                // worked, and `help` on the same name worked because it was already on this list.
                "which";
        }

        /// <summary>True when only the command's first argument names a type.</summary>
        private static bool CommandExpectsTypeNameFirstArgumentOnly(string? commandName)
        {
            return string.Equals(commandName, "cast", StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// Reads <c>name = value</c> where an argument is expected — <c>TS-P2-21</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The test and the read were written out twice, for method calls and for record-style
        /// literals, and a third argument list — <c>new</c>'s — simply never got a copy. So
        /// <c>new D(1, b = 7)</c> fell through to the operator parser, met <c>=</c> where no
        /// assignment belongs, and reported <c>tosh.parser.assignment_in_predicate</c> — a
        /// message about predicates for a call that contains no predicate. The rule lives here
        /// once now, which is what stops a fourth list from repeating it.
        /// </para>
        /// <para>
        /// Returns <see langword="false"/> without consuming anything when the next tokens are
        /// not a named argument, so the caller's ordinary path is unaffected.
        /// </para>
        /// </remarks>
        private bool TryParseNamedArgument(bool implicitCurrentItem, out ArgumentSyntax? argument)
        {
            argument = null;

            if (Current.Kind != SyntaxTokenKind.Bareword ||
                !IsValidIdentifier(Current.Text) ||
                Current.Text.StartsWith("$", StringComparison.Ordinal) ||
                Peek(1).Kind != SyntaxTokenKind.Bareword ||
                Peek(1).Text != "=")
            {
                return false;
            }

            var nameToken = NextToken();
            NextToken(); // consume '='

            var value = HasTopLevelOperatorBeforeCommaOrCloseParen()
                ? ParseOperatorExpression(Current.Span.Start, implicitCurrentItem)
                : ParseArgument(implicitCurrentItem: implicitCurrentItem);

            if (value is null)
            {
                // The name and '=' are consumed either way; the value's own diagnostic stands.
                return true;
            }

            argument = new NamedArgumentSyntax(
                nameToken.Text,
                value,
                TextSpan.FromBounds(nameToken.Span.Start, value.Span.End));
            return true;
        }

        private bool TryGetTypeNameEndOffset(int offset, out int endOffset)
        {
            if (Peek(offset).Kind != SyntaxTokenKind.Bareword ||
                !LooksLikePotentialTypeName(Peek(offset).Text))
            {
                endOffset = offset;
                return false;
            }

            endOffset = offset;

            if (Peek(endOffset + 1).Kind == SyntaxTokenKind.LessThan)
            {
                var depth = 0;
                var position = endOffset + 1;

                while (true)
                {
                    var token = Peek(position);

                    switch (token.Kind)
                    {
                        case SyntaxTokenKind.LessThan:
                            depth++;
                            break;

                        case SyntaxTokenKind.GreaterThan:
                            depth--;
                            break;

                        case SyntaxTokenKind.GreaterThanGreaterThan:
                            depth -= 2;
                            break;

                        case SyntaxTokenKind.Comma:
                        case SyntaxTokenKind.Bareword:
                            break;

                        default:
                            endOffset = offset;
                            return false;
                    }

                    if (depth <= 0)
                    {
                        endOffset = position;
                        break;
                    }

                    position++;
                }
            }

            // `TS-P2-69`. The array suffix, so `var y: string[] = […]` is recognised as a
            // declaration at all. This lookahead runs *before* parsing and decides whether
            // `var` starts one; it knew `<…>` and `?` but not `[]`, so the whole statement
            // fell through to command dispatch and reported "Command 'var' was not found"
            // — a message about `var` for a defect in the type annotation. Repeats for
            // jagged arrays, and matches the suffix loop in `ParseTypeNameSuffix`.
            while (Peek(endOffset + 1).Kind == SyntaxTokenKind.OpenBracket &&
                   Peek(endOffset + 2).Kind == SyntaxTokenKind.CloseBracket)
            {
                endOffset += 2;
            }

            if (Peek(endOffset + 1).Kind == SyntaxTokenKind.Bareword &&
                Peek(endOffset + 1).Text == "?")
            {
                endOffset++;
            }

            return true;
        }

        /// <summary>
        /// True when the token after the current one begins a
        /// whitespace-separated command argument: a literal, or a
        /// bareword that is not an operator. Operators are excluded so
        /// expressions such as <c>Math.PI + 1</c> are not mistaken for a
        /// command with arguments.
        /// </summary>
        private bool NextTokenStartsCommandArgument()
        {
            var next = Peek(1);

            if (HasLineBreakBetween(Current.Span.End, next.Span.Start))
            {
                return false;
            }

            switch (next.Kind)
            {
                case SyntaxTokenKind.Number:
                case SyntaxTokenKind.String:
                case SyntaxTokenKind.InterpolatedString:
                case SyntaxTokenKind.Boolean:
                case SyntaxTokenKind.Null:
                case SyntaxTokenKind.UnitLiteral:

                // Delimited arguments. Without these a module-qualified command
                // accepted a value argument and refused a *structured* one:
                // `Shell.HasPipe { ... }`, `M.F [1, 2]`, `M.F {| a = 1 |}` and
                // `M.F {: 1 :}` all fell back to being read as static member
                // accesses, leaving the argument as a separate stage and reporting
                // `missing_pipeline_separator` at the opening delimiter. `M.F 5`
                // worked, which is what made it look like a limitation of blocks
                // rather than a hole in this list.
                // An *adjacent* `[` is an index, not an argument. `M.F [1, 2]`
                // passes a list; `A.b.c[0]` subscripts the path. Spacing is what
                // separates them, and it is the same adjacency test
                // ParsePostfixChain already applies. Without this, the whole
                // dotted path was taken as a command name and the subscript was
                // left as a separate list argument.
                case SyntaxTokenKind.OpenBracket:
                    return next.Span.Start != Current.Span.End;

                case SyntaxTokenKind.OpenBrace:
                case SyntaxTokenKind.OpenBracePipe:
                case SyntaxTokenKind.OpenBraceColon:
                case SyntaxTokenKind.OpenBracePercent:
                    return true;
                case SyntaxTokenKind.Bareword:
                    return !IsAnyOperatorToken(next);
                default:
                    return false;
            }
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

        private static bool IsPipelineTerminator(
            SyntaxTokenKind kind,
            bool untilCloseParen,
            bool untilCloseBrace,
            bool untilSemicolon,
            bool untilOpenBrace = false)
        {
            if (kind == SyntaxTokenKind.EndOfFile)
            {
                return true;
            }

            if (untilCloseParen && kind == SyntaxTokenKind.CloseParen)
            {
                return true;
            }

            if (untilCloseBrace && kind == SyntaxTokenKind.CloseBrace)
            {
                return true;
            }

            if (untilSemicolon && kind == SyntaxTokenKind.Semicolon)
            {
                return true;
            }

            if (untilOpenBrace && kind == SyntaxTokenKind.OpenBrace)
            {
                return true;
            }

            return false;
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

        private static bool IsPositionalParameter(string name)
        {
            return name.Length > 0 && name[0] != '0' && name.All(char.IsDigit);
        }

        private static bool CanStartMemberAssignmentTarget(SyntaxToken token)
        {
            if (IsVariableReferenceLikeToken(token))
            {
                return true;
            }

            if (token.Kind != SyntaxTokenKind.Bareword || string.IsNullOrWhiteSpace(token.Text))
            {
                return false;
            }

            var rootName = token.Text.Split('.', 2, StringSplitOptions.None)[0];
            return IsValidIdentifier(rootName);
        }

        private static bool HasEmbeddedAssignmentMemberPath(SyntaxToken token)
        {
            if (token.Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            if (token.Text == "_")
            {
                return false;
            }

            if (IsVariableReferenceLikeToken(token))
            {
                ParseVariableReferenceToken(token, out _, out var memberPath);
                return !string.IsNullOrWhiteSpace(memberPath);
            }

            var separatorIndex = token.Text.IndexOf('.');
            return separatorIndex > 0 && IsValidIdentifier(token.Text[..separatorIndex]);
        }

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

        private static void ParseTypedIdentifierToken(
            string text,
            out string name,
            out string? typeName,
            out bool expectsFollowingTypeName)
        {
            var colonIndex = text.IndexOf(':');

            if (colonIndex < 0)
            {
                name = text;
                typeName = null;
                expectsFollowingTypeName = false;
                return;
            }

            name = text[..colonIndex];
            typeName = colonIndex < text.Length - 1
                ? text[(colonIndex + 1)..]
                : null;
            expectsFollowingTypeName = string.IsNullOrWhiteSpace(typeName);
        }

        private static TextSpan GetPipelineSpan(PipelineSyntax pipeline, TextSpan fallbackSpan)
        {
            if (pipeline.Stages.Count == 0)
            {
                return fallbackSpan;
            }

            var end = pipeline.Stages[^1].Span.End;

            if (pipeline.Redirections is { Count: > 0 })
            {
                end = pipeline.Redirections[^1].Span.End;
            }

            return TextSpan.FromBounds(pipeline.Stages[0].Span.Start, end);
        }

        private static int GetPipelineEnd(PipelineSyntax pipeline, int fallbackEnd)
        {
            if (pipeline.Redirections is { Count: > 0 })
            {
                return pipeline.Redirections[^1].Span.End;
            }

            return pipeline.Stages.Count > 0 ? pipeline.Stages[^1].Span.End : fallbackEnd;
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
