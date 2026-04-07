using System.Text;
using System.Globalization;
using Tosh.Core;

namespace Tosh.Language.Parsing;

public static class ToshParser
{
    public static ParseResult Parse(string source, string sourceName = "<input>")
    {
        try
        {
            var sourceText = source ?? string.Empty;
            var parser = new InternalParser(sourceName, sourceText, new ToshLexer(sourceText).Lex());
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

    private sealed class InternalParser
    {
        private readonly string _sourceName;
        private readonly string _sourceText;
        private readonly IReadOnlyList<SyntaxToken> _tokens;
        private readonly List<SyntaxDiagnostic> _diagnostics = [];
        private int _position;

        public InternalParser(string sourceName, string sourceText, IReadOnlyList<SyntaxToken> tokens)
        {
            _sourceName = sourceName;
            _sourceText = sourceText;
            _tokens = tokens;
        }

        public ParseResult Parse()
        {
            var statement = ParseScript();
            return new ParseResult(_sourceName, _sourceText, statement, _diagnostics);
        }

        private StatementSyntax ParseScript()
        {
            var statements = new List<StatementSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                statements.Add(ParseStatement(stopAtSemicolon: true));

                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (IsExplicitBackgroundStatementBoundary(statements[^1]))
                {
                    continue;
                }

                if (HasImplicitStatementBoundaryAfter(statements[^1].Span.End))
                {
                    continue;
                }

                if (Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_statement_separator",
                        Title: "Top-level statements must be separated by a newline or ';'.",
                        Span: Current.Span,
                        Label: "insert a newline or ';' between statements"));
                    SkipToStageBoundary(untilCloseParen: false, untilCloseBrace: false, untilSemicolon: false);

                    if (Current.Kind == SyntaxTokenKind.Semicolon)
                    {
                        NextToken();
                    }
                }
            }

            return statements.Count switch
            {
                0 => new ScriptStatementSyntax(Array.Empty<StatementSyntax>(), new TextSpan(0, 0)),
                1 => statements[0],
                _ => new ScriptStatementSyntax(
                    statements,
                    TextSpan.FromBounds(statements[0].Span.Start, statements[^1].Span.End)),
            };
        }

        private StatementSyntax ParseStatement(
            bool stopAtCloseParen = false,
            bool stopAtCloseBrace = false,
            bool stopAtSemicolon = false)
        {
            if (LooksLikeForStatement())
            {
                return ParseForStatement();
            }

            if (LooksLikeWhileStatement())
            {
                return ParseWhileStatement();
            }

            if (LooksLikeUntilStatement())
            {
                return ParseUntilStatement();
            }

            if (LooksLikeIfStatement())
            {
                return ParseIfStatement();
            }

            if (LooksLikeVariableDeclaration())
            {
                return ParseVariableDeclarationStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeAllocStatement())
            {
                return ParseAllocStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeUsingStatement())
            {
                return ParseUsingStatement();
            }

            if (LooksLikeRequireStatement())
            {
                return ParseRequireStatement();
            }

            if (LooksLikeBindStatement())
            {
                return ParseBindStatement();
            }

            if (LooksLikeFunctionDefinition())
            {
                return ParseFunctionDefinitionStatement();
            }

            if (LooksLikeClassDefinition())
            {
                return ParseClassDefinitionStatement();
            }

            if (LooksLikeModuleDefinition())
            {
                return ParseModuleDefinitionStatement();
            }

            if (LooksLikeEnumDefinition())
            {
                return ParseEnumDefinitionStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeRecordDefinition())
            {
                return ParseRecordDefinitionStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeEventDefinition())
            {
                return ParseEventDefinitionStatement();
            }

            if (LooksLikeBreakStatement())
            {
                return ParseLoopControlStatement(isBreak: true);
            }

            if (LooksLikeContinueStatement())
            {
                return ParseLoopControlStatement(isBreak: false);
            }

            if (LooksLikeReturnStatement())
            {
                return ParseReturnStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeThrowStatement())
            {
                return ParseThrowStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeTryStatement())
            {
                return ParseTryStatement();
            }

            if (LooksLikeDeferStatement())
            {
                return ParseDeferStatement();
            }

            if (LooksLikeSwitchStatement())
            {
                return ParseSwitchStatement();
            }

            if (LooksLikeTypedVariableDeclaration())
            {
                return ParseTypedVariableDeclarationStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeVariableAssignment())
            {
                return ParseVariableAssignmentStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeMemberAssignment())
            {
                return ParseMemberAssignmentStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            var pipeline = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            return new PipelineStatementSyntax(pipeline, GetPipelineSpan(pipeline, new TextSpan(0, 0)));
        }

        private StatementSyntax ParseForStatement()
        {
            var forToken = NextToken();
            var nameToken = ExpectVariableName();

            if (!(Current.Kind == SyntaxTokenKind.Bareword &&
                  string.Equals(Current.Text, "in", StringComparison.OrdinalIgnoreCase)))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_for_in",
                    Title: "For loops require 'in' before the source pipeline.",
                    Span: Current.Span,
                    Label: "insert 'in' between the loop variable and source"));
            }
            else
            {
                NextToken();
            }

            var source = ParseParenthesizedPipeline("for");
            var body = ParseRequiredBlock("for");
            return new ForStatementSyntax(
                nameToken.Text,
                source,
                body,
                TextSpan.FromBounds(forToken.Span.Start, body.Span.End));
        }

        private StatementSyntax ParseWhileStatement()
        {
            return ParseConditionalLoopStatement("while", static (condition, body, span) => new WhileStatementSyntax(condition, body, span));
        }

        private StatementSyntax ParseUntilStatement()
        {
            return ParseConditionalLoopStatement("until", static (condition, body, span) => new UntilStatementSyntax(condition, body, span));
        }

        private StatementSyntax ParseConditionalLoopStatement(
            string keyword,
            Func<ArgumentSyntax, BlockSyntax, TextSpan, StatementSyntax> factory)
        {
            var keywordToken = NextToken();

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: $"tosh::parser::expected_{keyword}_condition",
                    Title: $"{char.ToUpperInvariant(keyword[0])}{keyword[1..]} loops require a parenthesized condition.",
                    Span: keywordToken.Span,
                    Label: $"write a condition in parentheses after '{keyword}'",
                    Help: $"try '{keyword} (<condition>) {{ ... }}'."));
                return factory(
                    new BarewordArgumentSyntax(string.Empty, keywordToken.Span),
                    new BlockSyntax(Array.Empty<StatementSyntax>(), keywordToken.Span),
                    keywordToken.Span);
            }

            var openParen = NextToken();
            var condition = ParseConditionalExpression(openParen);
            var body = ParseRequiredBlock(keyword);
            return factory(
                condition,
                body,
                TextSpan.FromBounds(keywordToken.Span.Start, body.Span.End));
        }

        private StatementSyntax ParseIfStatement()
        {
            var ifToken = NextToken();

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_if_condition",
                    Title: "If statements require a parenthesized condition.",
                    Span: ifToken.Span,
                    Label: "write a condition in parentheses after 'if'",
                    Help: "try 'if (<condition>) { ... }'."));
                return new IfStatementSyntax(
                    new BarewordArgumentSyntax(string.Empty, ifToken.Span),
                    new BlockSyntax(Array.Empty<StatementSyntax>(), ifToken.Span),
                    null,
                    ifToken.Span);
            }

            var openParen = NextToken();
            var condition = ParseConditionalExpression(openParen);
            var thenBlock = ParseRequiredBlock("if");
            BlockSyntax? elseBlock = null;
            var end = thenBlock.Span.End;

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "else", StringComparison.OrdinalIgnoreCase))
            {
                var elseToken = NextToken();

                if (Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, "if", StringComparison.OrdinalIgnoreCase))
                {
                    var nestedIf = ParseIfStatement();
                    elseBlock = new BlockSyntax([nestedIf], nestedIf.Span);
                }
                else if (Current.Kind == SyntaxTokenKind.OpenBrace)
                {
                    elseBlock = ParseBlock();
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::expected_else_block",
                        Title: "Else clauses require a block or nested if statement.",
                        Span: elseToken.Span,
                        Label: "write '{ ... }' or 'if (...) { ... }' after 'else'"));
                    elseBlock = new BlockSyntax(Array.Empty<StatementSyntax>(), elseToken.Span);
                }

                end = elseBlock.Span.End;
            }

            return new IfStatementSyntax(
                condition,
                thenBlock,
                elseBlock,
                TextSpan.FromBounds(ifToken.Span.Start, end));
        }

        private StatementSyntax ParseVariableDeclarationStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            var varToken = NextToken();

            // Destructuring: var { a, b } = ... or var [a, b] = ...
            if (Current.Kind is SyntaxTokenKind.OpenBrace or SyntaxTokenKind.OpenBracket)
            {
                return ParseDestructuringDeclaration(declarationStart, modifier, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            var nameToken = ExpectVariableName();

            if (IsDeclarationBoundary(nameToken.Span.End, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon))
            {
                return new VariableDeclarationStatementSyntax(
                    nameToken.Text,
                    null,
                    null,
                    modifier,
                    TextSpan.FromBounds(declarationStart, nameToken.Span.End));
            }

            var equalsToken = ExpectEqualsToken("Variable declarations use '=' after the variable name.");
            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, equalsToken.Span.End);
            return new VariableDeclarationStatementSyntax(
                nameToken.Text,
                null,
                value,
                modifier,
                TextSpan.FromBounds(declarationStart, end));
        }

        private StatementSyntax ParseDestructuringDeclaration(
            int declarationStart,
            DeclarationModifier modifier,
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
                            Code: "tosh::parser::expected_destructuring_name",
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
            else // OpenBracket
            {
                NextToken(); // consume [
                var names = new List<string>();

                while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBracket)
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
                            Code: "tosh::parser::expected_destructuring_name",
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
                if (Current.Kind == SyntaxTokenKind.CloseBracket)
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
                TextSpan.FromBounds(declarationStart, end));
        }

        private StatementSyntax ParseTypedVariableDeclarationStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            var typeToken = NextToken();
            var typeName = ParseTypeNameSuffix(typeToken.Text);
            var nameToken = ExpectVariableName();
            var equalsToken = ExpectEqualsToken("Typed variable declarations use '=' after the variable name.");
            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, equalsToken.Span.End);
            return new VariableDeclarationStatementSyntax(
                nameToken.Text,
                typeName,
                value,
                modifier,
                TextSpan.FromBounds(declarationStart, end));
        }

        private StatementSyntax ParseAllocStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            NextToken();
            var nameToken = ExpectVariableName();
            var equalsToken = ExpectEqualsToken("Allocated buffer declarations use '=' after the variable name.");
            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, equalsToken.Span.End);
            return new AllocStatementSyntax(
                nameToken.Text,
                value,
                modifier,
                TextSpan.FromBounds(declarationStart, end));
        }

        private StatementSyntax ParseVariableAssignmentStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var nameToken = Current;
            var name = ParseAssignableVariableName();
            var assignmentToken = ExpectAssignmentOperatorToken("Assignments use '=', '+=', '-=', '*=', '/=', or '%=' between the variable name and the value.");
            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, assignmentToken.Span.End);
            return new VariableAssignmentStatementSyntax(
                name,
                NormalizeAssignmentOperator(assignmentToken),
                value,
                TextSpan.FromBounds(nameToken.Span.Start, end));
        }

        private StatementSyntax ParseMemberAssignmentStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var target = ParseMemberAssignmentTarget();
            var assignmentToken = ExpectAssignmentOperatorToken("Assignments use '=', '+=', '-=', '*=', '/=', or '%=' between the member path and the value.");
            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, assignmentToken.Span.End);
            return new MemberAssignmentStatementSyntax(
                target,
                NormalizeAssignmentOperator(assignmentToken),
                value,
                TextSpan.FromBounds(target.Span.Start, end));
        }

        private StatementSyntax ParseUsingStatement()
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            var usingToken = NextToken();

            if (Current.Kind is not SyntaxTokenKind.Bareword and not SyntaxTokenKind.String)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_using_target",
                    Title: "Using statements require a namespace or type alias target.",
                    Span: Current.Span,
                    Label: "write something like 'using System.IO' or 'using System.IO = IO'"));
                return new UsingStatementSyntax(string.Empty, null, modifier, usingToken.Span);
            }

            var targetToken = NextToken();
            string? alias = null;
            var end = targetToken.Span.End;

            if (IsFileImportTarget(targetToken))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::using_requires_namespace",
                    Title: "'using' is reserved for CLR namespaces and aliases.",
                    Span: targetToken.Span,
                    Label: "use 'require' to load ToSh files and modules",
                    Help: $"try 'require {targetToken.Text}'."));
                return new UsingStatementSyntax(targetToken.Text, null, modifier, TextSpan.FromBounds(declarationStart, end));
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                (string.Equals(Current.Text, "as", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Current.Text, "=", StringComparison.Ordinal)))
            {
                NextToken();
                var aliasToken = ExpectVariableName();
                alias = aliasToken.Text;
                end = aliasToken.Span.End;
            }

            return new UsingStatementSyntax(
                targetToken.Text,
                alias,
                modifier,
                TextSpan.FromBounds(declarationStart, end));
        }

        private StatementSyntax ParseRequireStatement()
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            var requireToken = NextToken();
            var isNative = false;

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "native", StringComparison.OrdinalIgnoreCase))
            {
                isNative = true;
                NextToken();
            }

            if (!isNative && Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                var imports = ParseRequireImportList();

                if (!(Current.Kind == SyntaxTokenKind.Bareword &&
                      string.Equals(Current.Text, "from", StringComparison.OrdinalIgnoreCase)))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::expected_require_from",
                        Title: "Selective require statements need 'from' before the target path.",
                        Span: Current.Span,
                        Label: "write something like 'require { Inventory } from \"./inventory.tosh\"'"));
                    return new RequireStatementSyntax(string.Empty, imports, false, null, modifier, TextSpan.FromBounds(declarationStart, imports[^1].Span.End));
                }

                NextToken();
                var targetToken = ExpectRequireTarget();
                var target = GetRequireTargetText(targetToken);
                return new RequireStatementSyntax(
                    target,
                    imports,
                    false,
                    null,
                    modifier,
                    TextSpan.FromBounds(declarationStart, targetToken.Span.End));
            }

            if (Current.Kind is not SyntaxTokenKind.Bareword and not SyntaxTokenKind.String)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_require_target",
                    Title: isNative ? "Native require statements need a library path or library name." : "Require statements need a ToSh file or module path.",
                    Span: Current.Span,
                    Label: isNative ? "write something like 'require native \"libc\" as LibC'" : "write something like 'require \"./common.tosh\"'"));
                return new RequireStatementSyntax(string.Empty, Array.Empty<RequireImportSyntax>(), isNative, null, modifier, requireToken.Span);
            }

            var firstToken = NextToken();

            if (!isNative &&
                Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "from", StringComparison.OrdinalIgnoreCase))
            {
                NextToken();
                var targetToken = ExpectRequireTarget();
                var alias = TryParseRequireAlias(out var aliasToken);
                var target = GetRequireTargetText(targetToken);
                var import = new RequireImportSyntax(firstToken.Text, alias, firstToken.Span);
                var end = aliasToken?.Span.End ?? targetToken.Span.End;
                return new RequireStatementSyntax(
                    target,
                    [import],
                    false,
                    null,
                    modifier,
                    TextSpan.FromBounds(declarationStart, end));
            }

            var legacyTarget = GetRequireTargetText(firstToken);
            SyntaxToken? nativeAliasToken = null;
            var nativeAlias = isNative ? TryParseRequireAlias(out nativeAliasToken) : null;
            return new RequireStatementSyntax(
                legacyTarget,
                Array.Empty<RequireImportSyntax>(),
                isNative,
                nativeAlias,
                modifier,
                TextSpan.FromBounds(declarationStart, (isNative ? nativeAliasToken?.Span.End : null) ?? firstToken.Span.End));
        }

        private StatementSyntax ParseBindStatement()
        {
            var bindToken = NextToken();
            var nativeTarget = default(string?);
            var moduleName = string.Empty;
            var moduleEnd = bindToken.Span.End;

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "native", StringComparison.OrdinalIgnoreCase))
            {
                NextToken();
                var targetToken = ExpectRequireTarget();
                nativeTarget = GetRequireTargetText(targetToken);
                moduleEnd = targetToken.Span.End;

                var alias = TryParseRequireAlias(out var aliasToken);
                moduleName = alias ?? GetDefaultNativeBindModuleName(nativeTarget);
                moduleEnd = aliasToken?.Span.End ?? moduleEnd;
            }
            else
            {
                var moduleToken = ExpectVariableName();
                moduleName = moduleToken.Text;
                moduleEnd = moduleToken.Span.End;
            }

            var functions = ParseNativeBindingBlock();
            var end = functions.Count > 0 ? functions[^1].Span.End : moduleEnd;

            if (Current.Kind == SyntaxTokenKind.CloseBrace)
            {
                end = NextToken().Span.End;
            }

            return new BindStatementSyntax(
                moduleName,
                nativeTarget,
                functions,
                TextSpan.FromBounds(bindToken.Span.Start, end));
        }

        private IReadOnlyList<NativeFunctionBindingSyntax> ParseNativeBindingBlock()
        {
            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_bind_body",
                    Title: "Bind statements require a body.",
                    Span: Current.Span,
                    Label: "write '{ ... }' after the bind target"));
                return Array.Empty<NativeFunctionBindingSyntax>();
            }

            var openBrace = NextToken();
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

                if (HasImplicitStatementBoundaryAfter(function.Span.End))
                {
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_bind_member_separator",
                        Title: "Bound functions must be separated by a newline or ';'.",
                        Span: Current.Span,
                        Label: "insert a newline or ';' between bound functions"));
                    SkipToBlockBoundary();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_brace",
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
                    Code: "tosh::parser::expected_bind_function",
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
            var returnTypeName = TryParseReturnTypeAnnotation();
            var symbolName = nameToken.Text;
            string? callingConventionName = null;

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
                        Code: "tosh::parser::expected_native_symbol_name",
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
                        Code: "tosh::parser::expected_native_calling_convention",
                        Title: "Native function bindings need a calling convention name after 'callconv'.",
                        Span: Current.Span,
                        Label: "write something like 'cdecl' or 'stdcall' here"));
                }
            }

            var end = Peek(-1).Span.End;

            return new NativeFunctionBindingSyntax(
                nameToken.Text,
                symbolName,
                parameters,
                returnTypeName,
                callingConventionName,
                TextSpan.FromBounds(memberStart, Math.Max(end, nameToken.Span.End)));
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
                    Code: "tosh::parser::missing_closing_parenthesis",
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
                        Code: "tosh::parser::unexpected_function_parameter_separator",
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
                        Code: "tosh::parser::missing_function_parameter_separator",
                        Title: "Function parameters must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between function parameters"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_parenthesis",
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
                    Code: "tosh::parser::expected_type_name",
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

                return new NativeFunctionParameterSyntax(name, string.IsNullOrWhiteSpace(typeName) ? null : typeName, passingMode, firstToken.Span);
            }

            var generatedName = $"arg{parameterIndex + 1}";
            var typeOnlyName = ParseTypeNameSuffix(nameOrType);
            return new NativeFunctionParameterSyntax(generatedName, string.IsNullOrWhiteSpace(typeOnlyName) ? null : typeOnlyName, passingMode, firstToken.Span);
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
                    Code: "tosh::parser::missing_closing_brace",
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
                Code: "tosh::parser::expected_require_target",
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

        private StatementSyntax ParseThrowStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var throwToken = NextToken();

            if (IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) ||
                Current.Kind == SyntaxTokenKind.Pipe ||
                HasImplicitStatementBoundaryAfter(throwToken.Span.End))
            {
                return new ThrowStatementSyntax(null, throwToken.Span);
            }

            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, throwToken.Span.End);
            return new ThrowStatementSyntax(
                value.Stages.Count == 0 ? null : value,
                TextSpan.FromBounds(throwToken.Span.Start, end));
        }

        private StatementSyntax ParseTryStatement()
        {
            var tryToken = NextToken();
            var tryBlock = ParseRequiredBlock("try");
            CatchClauseSyntax? catchClause = null;
            BlockSyntax? finallyBlock = null;
            var end = tryBlock.Span.End;

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "catch", StringComparison.OrdinalIgnoreCase))
            {
                var catchToken = NextToken();
                string? variableName = null;

                if (Current.Kind == SyntaxTokenKind.OpenParen)
                {
                    NextToken();

                    if (Current.Kind == SyntaxTokenKind.Bareword && IsValidIdentifier(Current.Text))
                    {
                        variableName = NextToken().Text;
                    }
                    else if (IsVariableReferenceLikeToken(Current))
                    {
                        variableName = ParseAssignableVariableName();
                    }
                    else
                    {
                        var invalidToken = Current;
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh::parser::expected_catch_variable",
                            Title: "Catch clauses require a variable name when parentheses are used.",
                            Span: invalidToken.Span,
                            Label: "write a variable like 'err' or '$err'"));
                    }

                    if (Current.Kind != SyntaxTokenKind.CloseParen)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh::parser::missing_closing_parenthesis",
                            Title: "A closing ')' is required here.",
                            Span: catchToken.Span,
                            Label: "this catch variable never closes"));
                    }
                    else
                    {
                        NextToken();
                    }
                }

                var catchBlock = ParseRequiredBlock("catch");
                catchClause = new CatchClauseSyntax(variableName, catchBlock, TextSpan.FromBounds(catchToken.Span.Start, catchBlock.Span.End));
                end = catchBlock.Span.End;
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "finally", StringComparison.OrdinalIgnoreCase))
            {
                var finallyToken = NextToken();
                finallyBlock = ParseRequiredBlock("finally");
                end = finallyBlock.Span.End;

                if (catchClause is null)
                {
                    catchClause = null;
                }
            }

            if (catchClause is null && finallyBlock is null)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::try_requires_handler",
                    Title: "Try statements require a catch block, a finally block, or both.",
                    Span: tryToken.Span,
                    Label: "add 'catch { ... }', 'finally { ... }', or both after this try block"));
            }

            return new TryStatementSyntax(
                tryBlock,
                catchClause,
                finallyBlock,
                TextSpan.FromBounds(tryToken.Span.Start, end));
        }

        private StatementSyntax ParseDeferStatement()
        {
            var deferToken = NextToken();
            var body = ParseRequiredBlock("defer");

            return new DeferStatementSyntax(
                body,
                TextSpan.FromBounds(deferToken.Span.Start, body.Span.End));
        }

        private StatementSyntax ParseSwitchStatement()
        {
            var switchToken = NextToken();

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_switch_value",
                    Title: "Switch statements require a parenthesized value.",
                    Span: switchToken.Span,
                    Label: "write a value in parentheses after 'switch'"));
                return new SwitchStatementSyntax(
                    new BarewordArgumentSyntax(string.Empty, switchToken.Span),
                    Array.Empty<SwitchCaseSyntax>(),
                    null,
                    switchToken.Span);
            }

            var openParen = NextToken();
            var value = ParseConditionalExpression(openParen);

            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_switch_block",
                    Title: "Switch statements require a case block.",
                    Span: Current.Span,
                    Label: "write '{ case ... { ... } }' after the switch value"));
                return new SwitchStatementSyntax(value, Array.Empty<SwitchCaseSyntax>(), null, switchToken.Span);
            }

            var openBrace = NextToken();
            var cases = new List<SwitchCaseSyntax>();
            BlockSyntax? defaultBlock = null;

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, "case", StringComparison.OrdinalIgnoreCase))
                {
                    var caseToken = NextToken();
                    var matchExpression = ParseArgument()
                                          ?? new BarewordArgumentSyntax(string.Empty, caseToken.Span);
                    var caseBlock = ParseRequiredBlock("case");
                    cases.Add(new SwitchCaseSyntax(matchExpression, caseBlock, TextSpan.FromBounds(caseToken.Span.Start, caseBlock.Span.End)));
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, "default", StringComparison.OrdinalIgnoreCase))
                {
                    var defaultToken = NextToken();
                    defaultBlock = ParseRequiredBlock("default");
                    continue;
                }

                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_switch_case",
                    Title: "Switch blocks may only contain 'case' and 'default' entries.",
                    Span: Current.Span,
                    Label: "write 'case <value> { ... }' or 'default { ... }' here"));
                SkipToBlockBoundary();
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this switch block never closes"));
                return new SwitchStatementSyntax(
                    value,
                    cases,
                    defaultBlock,
                    TextSpan.FromBounds(switchToken.Span.Start, openBrace.Span.End));
            }

            var closeBrace = NextToken();
            return new SwitchStatementSyntax(
                value,
                cases,
                defaultBlock,
                TextSpan.FromBounds(switchToken.Span.Start, closeBrace.Span.End));
        }

        private ArgumentSyntax ParseMatchArgument(bool implicitCurrentItem = false)
        {
            var matchToken = NextToken();

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_match_value",
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
                    Code: "tosh::parser::expected_match_block",
                    Title: "Match expressions require an arm block.",
                    Span: Current.Span,
                    Label: "write '{ pattern => result }' after the match value"));
                return new MatchArgumentSyntax(
                    value,
                    Array.Empty<MatchArmSyntax>(),
                    TextSpan.FromBounds(matchToken.Span.Start, value.Span.End));
            }

            var openBrace = NextToken();
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

                if (HasImplicitStatementBoundaryAfter(arm.Span.End))
                {
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_match_arm_separator",
                        Title: "Match arms must be separated by a newline, ';', or ','.",
                        Span: Current.Span,
                        Label: "insert a separator between match arms"));
                    SkipToBlockBoundary();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_brace",
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

        private ArgumentSyntax ParseIfExpressionArgument()
        {
            var ifToken = NextToken(); // consume 'if'

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_if_expression_condition",
                    Title: "If expressions require a parenthesized condition.",
                    Span: ifToken.Span,
                    Label: "write a condition in parentheses after 'if'",
                    Help: "try 'if (<condition>) { value } else { value }'."));
                return new BarewordArgumentSyntax(string.Empty, ifToken.Span);
            }

            var openParen = NextToken();
            var condition = ParseConditionalExpression(openParen);
            var thenBlock = ParseRequiredBlock("if expression");

            if (Current.Kind != SyntaxTokenKind.Bareword ||
                !string.Equals(Current.Text, "else", StringComparison.OrdinalIgnoreCase))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::if_expression_requires_else",
                    Title: "If expressions require an else block.",
                    Span: TextSpan.FromBounds(ifToken.Span.Start, thenBlock.Span.End),
                    Label: "add 'else { ... }' after this block",
                    Help: "if expressions must produce a value in all branches, so an else block is required."));
                return new IfExpressionArgumentSyntax(
                    condition,
                    thenBlock,
                    new BlockSyntax(Array.Empty<StatementSyntax>(), thenBlock.Span),
                    TextSpan.FromBounds(ifToken.Span.Start, thenBlock.Span.End));
            }

            NextToken(); // consume 'else'

            BlockSyntax elseBlock;

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "if", StringComparison.OrdinalIgnoreCase) &&
                Peek(1).Kind == SyntaxTokenKind.OpenParen)
            {
                // else if (...) { ... } else { ... } — parse as nested if statement inside a synthetic block
                var nestedIf = ParseIfStatement();
                elseBlock = new BlockSyntax([nestedIf], nestedIf.Span);
            }
            else if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                elseBlock = ParseBlock();
            }
            else
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_else_block",
                    Title: "Else clauses require a block or nested if expression.",
                    Span: Current.Span,
                    Label: "write '{ ... }' or 'if (...) { ... }' after 'else'"));
                elseBlock = new BlockSyntax(Array.Empty<StatementSyntax>(), Current.Span);
            }

            return new IfExpressionArgumentSyntax(
                condition,
                thenBlock,
                elseBlock,
                TextSpan.FromBounds(ifToken.Span.Start, elseBlock.Span.End));
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
                pattern = ParseArgument(implicitCurrentItem: implicitCurrentItem)
                          ?? new BarewordArgumentSyntax(string.Empty, Current.Span);

                if (pattern is VariableReferenceArgumentSyntax { Name: "_" })
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::match_default_keyword_required",
                        Title: "Use `default` for the fallback match arm.",
                        Span: pattern.Span,
                        Label: "write `default => ...` here instead of `_ => ...`",
                        Help: "ToSh keeps `_` for the current pipeline item. Use `default` as the match wildcard arm."));
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
                        Code: "tosh::parser::expected_match_guard_condition",
                        Title: "Match guards require a parenthesized condition.",
                        Span: ifToken.Span,
                        Label: "write `if (<condition>)` before `=>`"));
                }
            }

            if (!IsFatArrowToken(Current, Peek(1)))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_match_arm_arrow",
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

        private StatementSyntax ParseReturnStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var returnToken = NextToken();

            if (IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) ||
                Current.Kind == SyntaxTokenKind.Pipe ||
                HasImplicitStatementBoundaryAfter(returnToken.Span.End))
            {
                return new ReturnStatementSyntax(null, returnToken.Span);
            }

            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, returnToken.Span.End);

            return new ReturnStatementSyntax(
                value.Stages.Count == 0 ? null : value,
                TextSpan.FromBounds(returnToken.Span.Start, end));
        }

        private StatementSyntax ParseLoopControlStatement(bool isBreak)
        {
            var keyword = NextToken();
            return isBreak
                ? new BreakStatementSyntax(keyword.Span)
                : new ContinueStatementSyntax(keyword.Span);
        }

        private StatementSyntax ParseFunctionDefinitionStatement()
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            var funcToken = NextToken();
            var nameToken = ExpectCommandName();

            IReadOnlyList<FunctionParameterSyntax> parameters = Array.Empty<FunctionParameterSyntax>();

            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                parameters = ParseFunctionParameters();
            }
            else if (!IsFatArrowToken(Current, Peek(1)))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_function_signature",
                    Title: "Function definitions require a parameter list or '=>'.",
                    Span: Current.Span,
                    Label: "write '(...)' for a normal function or '=>' for a command wrapper"));
                return new FunctionDefinitionStatementSyntax(
                    nameToken.Text,
                    Array.Empty<FunctionParameterSyntax>(),
                    null,
                    new BlockSyntax(Array.Empty<StatementSyntax>(), nameToken.Span),
                    false,
                    modifier,
                    TextSpan.FromBounds(declarationStart, nameToken.Span.End));
            }

            var returnTypeName = TryParseReturnTypeAnnotation();

            string? handlesEvent = null;
            int? handlerPriority = null;
            var isOnceHandler = false;
            BlockSyntax? whenGuard = null;

            if (MatchesKeyword(Current, "handles"))
            {
                NextToken(); // handles
                var eventNameToken = ExpectVariableName();
                handlesEvent = eventNameToken.Text;

                if (MatchesKeyword(Current, "when"))
                {
                    NextToken(); // when
                    whenGuard = ParseRequiredBlock("when");
                }

                if (MatchesKeyword(Current, "priority"))
                {
                    NextToken(); // priority
                    var priorityToken = NextToken();

                    if (priorityToken.Kind == SyntaxTokenKind.Number && priorityToken.Value is int priorityValue)
                    {
                        handlerPriority = priorityValue;
                    }
                    else if (priorityToken.Kind == SyntaxTokenKind.Number && priorityToken.Value is long priorityLong)
                    {
                        handlerPriority = (int)priorityLong;
                    }
                    else
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh::parser::expected_priority_value",
                            Title: "Expected an integer priority value.",
                            Span: priorityToken.Span,
                            Label: "expected an integer"));
                    }
                }

                if (MatchesKeyword(Current, "once"))
                {
                    NextToken(); // once
                    isOnceHandler = true;
                }
            }

            BlockSyntax body;
            var isCommandWrapper = false;

            if (IsFatArrowToken(Current, Peek(1)))
            {
                isCommandWrapper = true;
                if (returnTypeName is not null)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::arrow_function_no_return_type",
                        Title: "Arrow function wrappers do not support return type annotations.",
                        Span: Current.Span,
                        Label: "use a block body for typed functions",
                        Help: $"try 'func {nameToken.Text}(...) {{ ... }}' instead."));
                }

                body = ParseFunctionArrowBody(nameToken.Text);

                // Auto-detect $1, $2, ... positional parameters if no explicit params
                if (parameters.Count == 0)
                {
                    parameters = DetectPositionalParameters(body);
                }
            }
            else
            {
                body = ParseRequiredBlock("func");
            }

            return new FunctionDefinitionStatementSyntax(
                nameToken.Text,
                parameters,
                returnTypeName,
                body,
                isCommandWrapper,
                modifier,
                TextSpan.FromBounds(declarationStart, body.Span.End),
                handlesEvent,
                handlerPriority,
                isOnceHandler,
                whenGuard);
        }

        private StatementSyntax ParseClassDefinitionStatement()
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            var classToken = NextToken();
            var nameToken = ExpectVariableName();
            var primaryConstructorParameters = Current.Kind == SyntaxTokenKind.OpenParen
                ? ParseFunctionParameters()
                : Array.Empty<FunctionParameterSyntax>();
            var body = ParseClassBody(nameToken.Text);

            return new ClassDefinitionStatementSyntax(
                nameToken.Text,
                primaryConstructorParameters,
                body,
                modifier,
                TextSpan.FromBounds(declarationStart, body.Count == 0 ? nameToken.Span.End : body[^1].Span.End));
        }

        private StatementSyntax ParseModuleDefinitionStatement()
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            NextToken(); // module
            var nameToken = ExpectVariableName();
            var body = ParseRequiredBlock("module");

            return new ModuleDefinitionStatementSyntax(
                nameToken.Text,
                body,
                modifier,
                TextSpan.FromBounds(declarationStart, body.Span.End));
        }

        private StatementSyntax ParseEnumDefinitionStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            NextToken(); // enum
            var nameToken = Current.Kind == SyntaxTokenKind.Bareword ? NextToken() : ExpectVariableName();
            ParseTypedIdentifierToken(nameToken.Text, out var enumName, out var inlineUnderlyingType, out var expectsFollowingUnderlyingType);
            string? underlyingTypeName = null;

            if (!expectsFollowingUnderlyingType)
            {
                underlyingTypeName = inlineUnderlyingType;
            }
            else
            {
                underlyingTypeName = ParseTypeName("enum underlying type");
            }

            if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
            {
                NextToken();
                var typeToken = ExpectVariableName();
                underlyingTypeName = typeToken.Text;
            }

            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_enum_body",
                    Title: "Enum definitions require a body.",
                    Span: Current.Span,
                    Label: $"write '{{ ... }}' after enum '{enumName}'"));
                return new EnumDefinitionStatementSyntax(enumName, underlyingTypeName, Array.Empty<EnumMemberSyntax>(), modifier, TextSpan.FromBounds(declarationStart, nameToken.Span.End));
            }

            var openBrace = NextToken();
            var members = new List<EnumMemberSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind is SyntaxTokenKind.Comma or SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                var memberName = ExpectVariableName();
                PipelineSyntax? value = null;
                var memberEnd = memberName.Span.End;

                if (IsEqualsToken(Current))
                {
                    var equalsToken = NextToken();
                    var expression = ParseOperatorExpression(Current.Span.Start, implicitCurrentItem: false);

                    if (expression is null)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh::parser::expected_enum_member_value",
                            Title: "Enum members require a value after '='.",
                            Span: equalsToken.Span,
                            Label: $"write a value for enum member '{memberName.Text}'"));
                    }
                    else
                    {
                        var stage = new ExpressionPipelineStageSyntax(expression, expression.Span);
                        value = new PipelineSyntax([stage]);
                        memberEnd = expression.Span.End;
                    }
                }

                members.Add(new EnumMemberSyntax(
                    memberName.Text,
                    value,
                    TextSpan.FromBounds(memberName.Span.Start, memberEnd)));

                if (Current.Kind is SyntaxTokenKind.Comma or SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (HasImplicitStatementBoundaryAfter(memberEnd))
                {
                    continue;
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this enum body never closes",
                    Help: "close the enum body with '}' after the last member."));
                return new EnumDefinitionStatementSyntax(enumName, underlyingTypeName, members, modifier, TextSpan.FromBounds(declarationStart, members.Count == 0 ? nameToken.Span.End : members[^1].Span.End));
            }

            var closeBrace = NextToken();
            return new EnumDefinitionStatementSyntax(
                enumName,
                underlyingTypeName,
                members,
                modifier,
                TextSpan.FromBounds(declarationStart, closeBrace.Span.End));
        }

        private StatementSyntax ParseRecordDefinitionStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            NextToken(); // record
            var nameToken = ExpectVariableName();

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_record_fields",
                    Title: "Record definitions require a field list.",
                    Span: Current.Span,
                    Label: $"write '(...)' after record '{nameToken.Text}'"));
                return new RecordDefinitionStatementSyntax(nameToken.Text, Array.Empty<RecordFieldDefinitionSyntax>(), modifier, TextSpan.FromBounds(declarationStart, nameToken.Span.End));
            }

            var fields = ParseRecordDefinitionFields(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            var end = fields.Count == 0 ? nameToken.Span.End : fields[^1].Span.End;
            return new RecordDefinitionStatementSyntax(
                nameToken.Text,
                fields,
                modifier,
                TextSpan.FromBounds(declarationStart, end));
        }

        private StatementSyntax ParseEventDefinitionStatement()
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();

            var isLocal = false;
            var isRequired = false;

            if (MatchesKeyword(Current, "local"))
            {
                isLocal = true;
                NextToken();
            }
            else if (MatchesKeyword(Current, "required"))
            {
                isRequired = true;
                NextToken();
            }

            NextToken(); // event

            var nameToken = ExpectVariableName();

            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                return new EventDefinitionStatementSyntax(
                    nameToken.Text,
                    Array.Empty<EventFieldDefinitionSyntax>(),
                    isRequired,
                    isLocal,
                    modifier,
                    TextSpan.FromBounds(declarationStart, nameToken.Span.End));
            }

            var closeBraceEnd = Current.Span.End;
            var fields = ParseEventDefinitionFields(out closeBraceEnd);

            return new EventDefinitionStatementSyntax(
                nameToken.Text,
                fields,
                isRequired,
                isLocal,
                modifier,
                TextSpan.FromBounds(declarationStart, closeBraceEnd));
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

                if (IsEqualsToken(Current))
                {
                    var equalsToken = NextToken();
                    var expression = ParseOperatorExpression(Current.Span.Start, implicitCurrentItem: false);

                    if (expression is null)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh::parser::expected_record_field_default",
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
                    TextSpan.FromBounds(nameToken.Span.Start, end)));

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_paren",
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
                    Code: "tosh::parser::expected_class_body",
                    Title: "Class definitions require a body.",
                    Span: Current.Span,
                    Label: $"write '{{ ... }}' after class '{className}'"));
                return Array.Empty<ClassMemberSyntax>();
            }

            var openBrace = NextToken();
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

                if (HasImplicitStatementBoundaryAfter(member.Span.End))
                {
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_class_member_separator",
                        Title: "Class members must be separated by a newline or ';'.",
                        Span: Current.Span,
                        Label: "insert a newline or ';' between class members"));
                    SkipToBlockBoundary();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_brace",
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
            var memberStart = Current.Span.Start;
            var isShy = false;
            var isStatic = false;

            while (Current.Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Current.Text, "shy", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "static", StringComparison.Ordinal)))
            {
                isShy |= string.Equals(Current.Text, "shy", StringComparison.Ordinal);
                isStatic |= string.Equals(Current.Text, "static", StringComparison.Ordinal);
                NextToken();
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "prop", StringComparison.Ordinal))
            {
                return ParseClassPropertyMember(isShy, memberStart);
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "func", StringComparison.Ordinal))
            {
                var method = ParseFunctionDefinitionStatement() as FunctionDefinitionStatementSyntax
                             ?? throw new InvalidOperationException("Expected a function definition while parsing a class method.");
                return new ClassMethodMemberSyntax(method, isStatic, isShy, TextSpan.FromBounds(memberStart, method.Span.End));
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, className, StringComparison.Ordinal) &&
                Peek(1).Kind == SyntaxTokenKind.OpenParen)
            {
                return ParseClassConstructorMember(className, memberStart);
            }

            var token = Current;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh::parser::expected_class_member",
                Title: $"Expected a member inside class '{className}'.",
                Span: token.Span,
                Label: "write 'prop', 'func', or a constructor here"));

            if (Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                NextToken();
            }

            return new ClassPropertyMemberSyntax(string.Empty, null, null, null, null, isShy, token.Span);
        }

        private ClassMemberSyntax ParseClassPropertyMember(bool isShy, int memberStart)
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
                    Code: "tosh::parser::expected_property_name",
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

            PipelineSyntax? initializer = null;
            BlockSyntax? getter = null;
            BlockSyntax? setter = null;
            var end = nameToken.Span.End;

            if (IsFatArrowToken(Current, Peek(1)))
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
                TextSpan.FromBounds(memberStart, end));
        }

        private (BlockSyntax? Getter, BlockSyntax? Setter, int End) ParsePropertyAccessorBlock()
        {
            var openBrace = NextToken();
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
                        Code: "tosh::parser::expected_property_accessor",
                        Title: "Property accessors must be 'get' or 'set'.",
                        Span: Current.Span,
                        Label: "write 'get' or 'set' here"));
                    NextToken();
                    continue;
                }

                var accessorName = NextToken().Text;
                var accessorBody = ParseArrowStatementBlock($"property {accessorName}");

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
                        Code: "tosh::parser::unknown_property_accessor",
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
                    Code: "tosh::parser::missing_closing_brace",
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
            var parameters = ParseFunctionParameters();
            var body = ParseRequiredBlock(className);

            return new ClassConstructorMemberSyntax(
                parameters,
                body,
                TextSpan.FromBounds(memberStart, body.Span.End));
        }

        private BlockSyntax ParseArrowStatementBlock(string owner)
        {
            var arrowStart = Current.Span.Start;
            ConsumeFatArrow();
            var statement = ParseStatement(stopAtCloseParen: false, stopAtCloseBrace: true, stopAtSemicolon: true);
            return new BlockSyntax([statement], TextSpan.FromBounds(arrowStart, statement.Span.End));
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
                parameters.Add(new FunctionParameterSyntax(i.ToString(), null, IsOptional: false, IsRest: false, body.Span));
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
                case ArrayLiteralArgumentSyntax list:
                    foreach (var item in list.Items)
                    {
                        ScanForPositionalRefs(item, ref maxPositional);
                    }
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

            var pipeline = ParsePipeline(
                untilCloseParen: false,
                untilCloseBrace: false,
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

            var expression = ParseArgument();

            if (expression is null)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_anonymous_function_expression",
                    Title: "Anonymous `=>` functions require an expression body.",
                    Span: Current.Span,
                    Label: "write an expression after `=>`"));

                return new BlockSyntax(Array.Empty<StatementSyntax>(), new TextSpan(arrowStart, 0));
            }

            var stage = new ExpressionPipelineStageSyntax(expression, expression.Span);
            var pipeline = new PipelineSyntax([stage]);
            var span = TextSpan.FromBounds(arrowStart, expression.Span.End);
            var statement = new PipelineStatementSyntax(pipeline, span);
            return new BlockSyntax([statement], span);
        }

        private ArgumentSyntax ParseAnonymousFunctionArgument()
        {
            var funcToken = NextToken();
            var parameters = Current.Kind == SyntaxTokenKind.OpenParen
                ? ParseFunctionParameters()
                : Array.Empty<FunctionParameterSyntax>();

            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                var body = ParseBlock();
                return new AnonymousFunctionArgumentSyntax(
                    parameters,
                    body,
                    TextSpan.FromBounds(funcToken.Span.Start, body.Span.End));
            }

            if (IsFatArrowToken(Current, Peek(1)))
            {
                var body = ParseAnonymousFunctionArrowBody();
                return new AnonymousFunctionArgumentSyntax(
                    parameters,
                    body,
                    TextSpan.FromBounds(funcToken.Span.Start, body.Span.End));
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh::parser::expected_anonymous_function_body",
                Title: "Anonymous functions require `=>` or a block body.",
                Span: Current.Span,
                Label: "write `=> <expression>` or `{ ... }` after the parameter list"));

            return new AnonymousFunctionArgumentSyntax(
                parameters,
                new BlockSyntax(Array.Empty<StatementSyntax>(), funcToken.Span),
                funcToken.Span);
        }

        private IReadOnlyList<FunctionParameterSyntax> ParseFunctionParameters()
        {
            var openParen = NextToken();
            var parameters = new List<FunctionParameterSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseParen)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::unexpected_function_parameter_separator",
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
                        Code: "tosh::parser::rest_parameter_must_be_last",
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
                        Code: "tosh::parser::missing_function_parameter_separator",
                        Title: "Function parameters must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between function parameters"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this parameter list never closes",
                    Help: "close the parameter list with ')' after the last parameter."));
                return parameters;
            }

            NextToken();
            return parameters;
        }

        private FunctionParameterSyntax ParseFunctionParameter()
        {
            var token = Current;

            if (token.Kind != SyntaxTokenKind.Bareword)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_function_parameter",
                    Title: "Expected a function parameter name.",
                    Span: token.Span,
                    Label: "parameters need an identifier like 'path' or 'days'"));

                if (Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    NextToken();
                }

                return new FunctionParameterSyntax(string.Empty, null, false, false, token.Span);
            }

            // Standalone '...' is shorthand for 'args...'
            if (token.Text == "...")
            {
                NextToken();
                return new FunctionParameterSyntax("args", null, false, true, token.Span);
            }

            // 'name...' rest parameter — strip the suffix
            if (token.Text.EndsWith("...", StringComparison.Ordinal))
            {
                var restName = token.Text[..^3];
                NextToken();

                if (!IsValidIdentifier(restName))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::expected_function_parameter",
                        Title: "Expected a function parameter name.",
                        Span: token.Span,
                        Label: "parameters need an identifier like 'path' or 'days'"));
                }

                return new FunctionParameterSyntax(restName, null, false, true, token.Span);
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
                    Code: "tosh::parser::expected_function_parameter",
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

            return new FunctionParameterSyntax(name, string.IsNullOrWhiteSpace(typeName) ? null : typeName, isOptional, false, nameToken.Span);
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

        private string ParseTypeName(string label)
        {
            if (Current.Kind == SyntaxTokenKind.Bareword)
            {
                return ParseTypeNameSuffix(NextToken().Text);
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh::parser::expected_type_name",
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
                            Code: "tosh::parser::missing_type_argument_separator",
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
                    Code: "tosh::parser::missing_closing_angle",
                    Title: "A closing '>' is required here.",
                    Span: Current.Span,
                    Label: "this generic type argument list never closes",
                    Help: "close the generic argument list with '>' after the last type."));
            }

            return builder.ToString();
        }

        private SyntaxToken ExpectVariableName()
        {
            if (Current.Kind == SyntaxTokenKind.Bareword && IsValidIdentifier(Current.Text))
            {
                return NextToken();
            }

            var token = Current;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh::parser::expected_variable_name",
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
                Code: "tosh::parser::expected_command_name",
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
                Code: "tosh::parser::expected_assignment_operator",
                Title: title,
                Span: token.Span,
                Label: "insert '=' here"));

            return new SyntaxToken(SyntaxTokenKind.Bareword, token.Span.Start, "=", "=");
        }

        private SyntaxToken ExpectAssignmentOperatorToken(string title)
        {
            if (IsAssignmentOperatorToken(Current))
            {
                return NextToken();
            }

            var token = Current;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh::parser::expected_assignment_operator",
                Title: title,
                Span: token.Span,
                Label: "insert '=', '+=', '-=', '*=', '/=', '%=', or '??=' here"));

            return new SyntaxToken(SyntaxTokenKind.Bareword, token.Span.Start, "=", "=");
        }

        private void ConsumeFatArrow()
        {
            if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == "=>")
            {
                NextToken();
                return;
            }

            if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == "=" &&
                Peek(1).Kind == SyntaxTokenKind.GreaterThan)
            {
                NextToken();
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
                        Code: "tosh::parser::expected_here_string_value",
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
                    Code: "tosh::parser::expected_command_name",
                    Title: "Expected a command name.",
                    Span: Current.Span,
                    Label: "commands start with a bareword like 'ls' or 'where'"));
                NextToken();
                return null;
            }

            var nameToken = NextToken();
            List<ArgumentSyntax> arguments;

            if (string.Equals(nameToken.Text, "where", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nameToken.Text, "take-while", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nameToken.Text, "skip-while", StringComparison.OrdinalIgnoreCase))
            {
                arguments = ParseWhereArguments(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }
            else if (string.Equals(nameToken.Text, "get", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(nameToken.Text, "select", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(nameToken.Text, "pick", StringComparison.OrdinalIgnoreCase))
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
                       !LooksLikeRedirectionOperator())
                {
                    if (HasImplicitStatementBoundaryAfter(lastConsumedEnd))
                    {
                        break;
                    }

                    if (TryParseCommaJoinedCommandArgument(out var joinedArgument))
                    {
                        arguments.Add(joinedArgument);
                        lastConsumedEnd = joinedArgument.Span.End;
                        continue;
                    }

                    var argument = ParseArgument(nameToken.Text);

                    if (argument is not null)
                    {
                        arguments.Add(argument);
                        lastConsumedEnd = argument.Span.End;
                    }
                }
            }

            var end = arguments.Count > 0 ? arguments[^1].Span.End : nameToken.Span.End;
            return new CommandSyntax(nameToken.Text, nameToken.Span, arguments, TextSpan.FromBounds(nameToken.Span.Start, end));
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
                LooksLikeRedirectionOperator())
            {
                return arguments;
            }

            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                arguments.Add(ParseMemberProjectionArgument());
                return arguments;
            }

            while (!IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                   Current.Kind != SyntaxTokenKind.Pipe &&
                   Current.Kind != SyntaxTokenKind.Ampersand &&
                   !LooksLikeRedirectionOperator())
            {
                if (HasImplicitStatementBoundaryAfter(lastConsumedEnd))
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

        private List<ArgumentSyntax> ParseWhereArguments(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var arguments = new List<ArgumentSyntax>();

            if (IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) ||
                Current.Kind == SyntaxTokenKind.Pipe ||
                Current.Kind == SyntaxTokenKind.Ampersand ||
                LooksLikeRedirectionOperator())
            {
                return arguments;
            }

            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                arguments.Add(ParsePredicateBlockArgument());
                return arguments;
            }

            var predicateArgument = ParseWherePredicateExpressionArgument();

            if (predicateArgument is not null)
            {
                arguments.Add(predicateArgument);
            }

            if (!IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                Current.Kind != SyntaxTokenKind.Pipe &&
                Current.Kind != SyntaxTokenKind.Ampersand &&
                !LooksLikeRedirectionOperator())
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::unexpected_predicate_tokens",
                    Title: "This predicate expression has extra tokens after it.",
                    Span: Current.Span,
                    Label: "predicate expressions must evaluate as a single condition"));
                SkipToStageBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            return arguments;
        }

        private ArgumentSyntax? ParseArgument(string? commandName = null, bool implicitCurrentItem = false)
        {
            var result = ParsePrimaryArgument(commandName, implicitCurrentItem);

            // Check for range operator: <expr>..<expr> or <expr>..<expr>..<expr>
            if (result is not null && Current.Kind == SyntaxTokenKind.DotDot)
            {
                if (result is VariableReferenceArgumentSyntax or MemberAccessArgumentSyntax
                    && result.Span.End == Current.Span.Start)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::accidental_double_dot",
                        Title: "Did you mean '.' (member access) instead of '..' (range)?",
                        Span: Current.Span,
                        Label: "this looks like an accidental double-dot"));
                }

                return ParseRangeArgument(result, implicitCurrentItem);
            }

            return result;
        }

        private ArgumentSyntax ParseRangeArgument(ArgumentSyntax start, bool implicitCurrentItem)
        {
            NextToken(); // consume first ..
            var second = ParsePrimaryArgument(implicitCurrentItem: implicitCurrentItem);

            if (second is null)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_range_bound",
                    Title: "Expected a value after '..'.",
                    Span: Current.Span,
                    Label: "range needs an end value"));
                return start;
            }

            // Check for three-part range: start..step..end
            if (Current.Kind == SyntaxTokenKind.DotDot)
            {
                NextToken(); // consume second ..
                var third = ParsePrimaryArgument(implicitCurrentItem: implicitCurrentItem);

                if (third is null)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::expected_range_end",
                        Title: "Expected end value after second '..'.",
                        Span: Current.Span,
                        Label: "stepped range needs an end value"));
                    return start;
                }

                // start..step..end
                var stepSpan = new TextSpan(start.Span.Start, third.Span.End - start.Span.Start);
                return new RangeArgumentSyntax(start, second, third, stepSpan);
            }

            // start..end
            var span = new TextSpan(start.Span.Start, second.Span.End - start.Span.Start);
            return new RangeArgumentSyntax(start, Step: null, second, span);
        }

        private ArgumentSyntax? ParsePrimaryArgument(string? commandName = null, bool implicitCurrentItem = false)
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

                        if (commandName is not null && CommandExpectsTypeNameArguments(commandName))
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
                            return ParseStaticMemberAccessArgument();
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
                        return new BarewordArgumentSyntax(token.Text, token.Span);
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

                case SyntaxTokenKind.OpenBrace:
                    if (string.Equals(commandName, "where", StringComparison.OrdinalIgnoreCase))
                    {
                        return ParsePredicateBlockArgument();
                    }

                    if (commandName is not null)
                    {
                        return ParseBlockArgument();
                    }

                    return ParsePostfixChain(ParseBraceLiteralArgument(implicitCurrentItem), implicitCurrentItem);

                case SyntaxTokenKind.Ampersand when Peek(1).Kind == SyntaxTokenKind.Bareword && IsValidCommandName(Peek(1).Text) && Current.Span.End == Peek(1).Span.Start:
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
                        Code: "tosh::parser::unexpected_token",
                        Title: $"Unexpected token '{Current.Text}'.",
                        Span: Current.Span,
                        Label: "this token does not fit here"));
                    NextToken();
                    return null;
            }
        }

        private ArgumentSyntax ParseNameOfArgument()
        {
            var start = Current.Span.Start;
            NextToken(); // consume "nameof"
            NextToken(); // consume "("

            var identifierToken = NextToken();
            var identifier = identifierToken.Text;
            var isVariableReference = identifier.StartsWith('$');

            // Strip leading '$' for variable references
            if (isVariableReference)
            {
                identifier = identifier[1..];
            }

            // Strip any member access (e.g. "$foo.Bar" → just "foo")
            var dotIndex = identifier.IndexOf('.');
            if (dotIndex >= 0)
            {
                identifier = identifier[..dotIndex];
            }

            if (Current.Kind == SyntaxTokenKind.CloseParen)
            {
                var end = Current.Span.End;
                NextToken(); // consume ")"
                return new NameOfArgumentSyntax(identifier, isVariableReference, TextSpan.FromBounds(start, end));
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh::parser::nameof_missing_close_paren",
                Title: "Expected ')' after nameof identifier.",
                Span: Current.Span,
                Label: "expected ')'"));

            return new NameOfArgumentSyntax(identifier, isVariableReference, TextSpan.FromBounds(start, identifierToken.Span.End));
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
            var identifier = identifierToken.Text;
            var isVariableReference = identifier.StartsWith('$');

            if (isVariableReference)
            {
                identifier = identifier[1..];
            }

            var dotIndex = identifier.IndexOf('.');
            if (dotIndex >= 0)
            {
                identifier = identifier[..dotIndex];
            }

            return new NameOfArgumentSyntax(identifier, isVariableReference, TextSpan.FromBounds(start, identifierToken.Span.End));
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
                    Code: "tosh::parser::expected_splat_target",
                    Title: "Argument splatting requires a variable or collection reference.",
                    Span: splatToken.Span,
                    Label: "write something like '...$tosh.Script.Args' here"));
                return new SplatArgumentSyntax(new BarewordArgumentSyntax(string.Empty, innerSpan), splatToken.Span);
            }

            var innerToken = new SyntaxToken(SyntaxTokenKind.Bareword, innerSpan.Start, innerText, innerText);

            if (!IsVariableReferenceLikeToken(innerToken))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::invalid_splat_target",
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

        private bool LooksLikeSpreadElement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   Current.Text.StartsWith("...", StringComparison.Ordinal) &&
                   Current.Text.Length > 3;
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
                    Code: "tosh::parser::invalid_spread_target",
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
                var token = NextToken();
                var text = token.Text;
                var separatorIndex = text.IndexOf('.');

                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::variable_references_require_dollar",
                    Title: "Variable assignments must use '$' after declaration.",
                    Span: token.Span,
                    Label: separatorIndex >= 0
                        ? $"write '${text} = ...' here"
                        : $"write '${text}.Member = ...' here",
                    Help: "declare variables with 'var name', then refer to them everywhere else as '$name'."));

                if (separatorIndex >= 0)
                {
                    var rootName = text[..separatorIndex];
                    var memberPath = text[(separatorIndex + 1)..];

                    expression = new VariableReferenceArgumentSyntax(
                        rootName,
                        TextSpan.FromBounds(token.Span.Start, token.Span.Start + rootName.Length));
                    expression = ApplyMemberOrMethodPostfix(expression, memberPath, token.Span, allowMethodCall: false);
                }
                else
                {
                    expression = new VariableReferenceArgumentSyntax(text, token.Span);
                }
            }
            else
            {
                var token = NextToken();
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_assignment_target",
                    Title: "Assignments require a variable or member path target.",
                    Span: token.Span,
                    Label: "write something like '$name = ...' or '$person.Name = ...'"));
                return new VariableReferenceArgumentSyntax(string.Empty, token.Span);
            }

            while (TryConsumePostfixToken(out var postfixToken, out var postfixText, out _))
            {
                expression = ApplyQualifiedMemberChain(expression, postfixText, postfixToken.Span, allowMethodCall: false);
            }

            if (expression is not MemberAccessArgumentSyntax)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_member_assignment_target",
                    Title: "This assignment needs a member path like '$person.Name'.",
                    Span: expression.Span,
                    Label: "assign directly to a member path here"));
            }

            return expression;
        }

        private ArgumentSyntax ParseStaticMethodCallArgument(bool implicitCurrentItem = false)
        {
            var methodToken = NextToken();
            var arguments = ParseInvocationArguments(implicitCurrentItem);
            var end = arguments.closeParenEnd ?? methodToken.Span.End;
            return new StaticMethodCallArgumentSyntax(
                methodToken.Text,
                arguments.arguments,
                TextSpan.FromBounds(methodToken.Span.Start, end));
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
                        Code: "tosh::parser::unexpected_projection_separator",
                        Title: "A projected member path is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add a member path here"));
                    NextToken();
                    continue;
                }

                if (Current.Kind != SyntaxTokenKind.Bareword)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::expected_projection_member_path",
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
                        Code: "tosh::parser::missing_projection_separator",
                        Title: "Projected member paths must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between projected member paths"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_projection_closing_brace",
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
                Code: "tosh::parser::expected_block",
                Title: $"The '{owner}' statement requires a block.",
                Span: Current.Span,
                Label: $"write '{{ ... }}' after '{owner}'"));
            return new BlockSyntax(Array.Empty<StatementSyntax>(), Current.Span);
        }

        private PipelineSyntax ParseParenthesizedPipeline(string owner)
        {
            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_parenthesized_source",
                    Title: $"The '{owner}' statement requires a parenthesized source.",
                    Span: Current.Span,
                    Label: $"write '(<pipeline>)' after '{owner}'"));
                return new PipelineSyntax(Array.Empty<PipelineStageSyntax>());
            }

            var openParen = NextToken();
            var pipeline = ParsePipeline(
                untilCloseParen: true,
                untilCloseBrace: false,
                untilSemicolon: false,
                allowExpressionStart: true);

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this parenthesized source never closes",
                    Help: "close the source pipeline with ')' before the block."));
                return pipeline;
            }

            NextToken();
            return pipeline;
        }

        private BlockSyntax ParseBlock()
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

                var statement = ParseStatement(stopAtCloseBrace: true, stopAtSemicolon: true);
                statements.Add(statement);

                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (IsExplicitBackgroundStatementBoundary(statement))
                {
                    continue;
                }

                if (HasImplicitStatementBoundaryAfter(statement.Span.End))
                {
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_block_separator",
                        Title: "Block statements must be separated by a newline or ';'.",
                        Span: Current.Span,
                        Label: "insert a newline or ';' between block statements"));
                    SkipToBlockBoundary();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this block never closes",
                    Help: "close the block with '}' after the last statement."));
                return new BlockSyntax(statements, openBrace.Span);
            }

            var closeBrace = NextToken();
            return new BlockSyntax(statements, TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
        }

        private ArgumentSyntax ParseArrayLiteralArgument(bool implicitCurrentItem = false)
        {
            var openBracket = NextToken();
            var items = new List<ArgumentSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBracket)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::unexpected_list_separator",
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

                if (Current.Kind is not SyntaxTokenKind.CloseBracket and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_list_separator",
                        Title: "Array items must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between array items"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBracket)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_bracket",
                    Title: "A closing ']' is required here.",
                    Span: openBracket.Span,
                    Label: "this array literal never closes",
                    Help: "close the array literal with ']' after the last item."));
                return new ArrayLiteralArgumentSyntax(items, openBracket.Span);
            }

            var closeBracket = NextToken();
            return new ArrayLiteralArgumentSyntax(items, TextSpan.FromBounds(openBracket.Span.Start, closeBracket.Span.End));
        }

        private ArgumentSyntax ParseBraceLiteralArgument(bool implicitCurrentItem = false)
        {
            return LooksLikeRecordLiteral()
                ? ParseRecordLiteralArgument(implicitCurrentItem)
                : ParseBraceCollectionLiteralArgument(implicitCurrentItem);
        }

        private ArgumentSyntax ParseBraceCollectionLiteralArgument(bool implicitCurrentItem = false)
        {
            var openBrace = NextToken();
            var items = new List<ArgumentSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::unexpected_list_separator",
                        Title: "A collection item is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add a collection item here"));
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

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_list_separator",
                        Title: "Collection items must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between collection items"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this collection literal never closes",
                    Help: "close the collection literal with '}' after the last item."));
                return new ArrayLiteralArgumentSyntax(items, openBrace.Span);
            }

            var closeBrace = NextToken();
            return new ArrayLiteralArgumentSyntax(items, TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
        }

        private ArgumentSyntax ParseRecordLiteralArgument(bool implicitCurrentItem = false)
        {
            var openBrace = NextToken();
            var fields = new List<RecordEntrySyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                // Spread entry: { ...$a, ...$b }
                if (LooksLikeSpreadElement())
                {
                    var spread = ParseSpreadElement();
                    fields.Add(new SpreadRecordEntrySyntax(spread.Value, spread.Span));

                    if (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken();
                    }
                    else if (HasImplicitStatementBoundaryAfter(spread.Span.End))
                    {
                        // newline separator
                    }

                    continue;
                }

                // Computed property: { ($expr) = value }
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
                            Code: "tosh::parser::expected_closing_paren",
                            Title: "A closing ')' is required after the computed property name.",
                            Span: Current.Span,
                            Label: "close the parenthesized expression"));
                    }

                    ExpectEqualsToken("Computed record fields use '=' between the key expression and value.");
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
                    else if (compValue is not null && HasImplicitStatementBoundaryAfter(compValue.Span.End))
                    {
                        // newline separator
                    }

                    continue;
                }

                string fieldName;
                TextSpan fieldStart;

                if (Current.Kind == SyntaxTokenKind.Bareword)
                {
                    var nameToken = NextToken();
                    fieldName = nameToken.Text;
                    fieldStart = nameToken.Span;
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
                        Code: "tosh::parser::expected_record_field_name",
                        Title: "Record literals require a field name before '='.",
                        Span: Current.Span,
                        Label: "write a field name like 'Name = value'"));
                    NextToken();
                    continue;
                }

                ExpectEqualsToken("Record fields use '=' between the field name and value.");
                var value = ParseArgument(implicitCurrentItem: implicitCurrentItem);

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

                if (value is not null && HasImplicitStatementBoundaryAfter(value.Span.End))
                {
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_record_field_separator",
                        Title: "Record fields must be separated by ',' or a newline.",
                        Span: Current.Span,
                        Label: "insert ',' or a newline between record fields"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_record_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this record literal never closes",
                    Help: "close the record literal with '}' after the last field."));
                return new RecordLiteralArgumentSyntax(fields, openBrace.Span);
            }

            var closeBrace = NextToken();
            return new RecordLiteralArgumentSyntax(fields, TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
        }

        private ArgumentSyntax ParseNewObjectArgument(bool implicitCurrentItem = false)
        {
            var newToken = NextToken();

            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_type_name",
                    Title: "Object construction requires a CLR type name.",
                    Span: Current.Span,
                    Label: "write a type after 'new', like 'new string(\"hello\")'"));
                return new NewObjectArgumentSyntax(string.Empty, Array.Empty<ArgumentSyntax>(), newToken.Span);
            }

            var typeToken = NextToken();
            var typeName = ParseTypeNameSuffix(typeToken.Text);

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_constructor_parenthesis",
                    Title: "Object construction uses C#-style parentheses.",
                    Span: typeToken.Span,
                    Label: "add '(' after the type name",
                    Help: "try 'new SomeType(...)' instead of command-style construction."));
                return new NewObjectArgumentSyntax(typeName, Array.Empty<ArgumentSyntax>(), TextSpan.FromBounds(newToken.Span.Start, typeToken.Span.End));
            }

            var openParen = NextToken();
            var arguments = new List<ArgumentSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseParen)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::unexpected_constructor_separator",
                        Title: "A constructor argument is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add an argument here"));
                    NextToken();
                    continue;
                }

                var argument = ParseArgument(implicitCurrentItem: implicitCurrentItem);

                if (argument is not null)
                {
                    arguments.Add(argument);
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseParen and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_constructor_separator",
                        Title: "Constructor arguments must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between constructor arguments"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this constructor call never closes",
                    Help: "close the argument list with ')' after the last constructor argument."));
                return new NewObjectArgumentSyntax(
                    typeName,
                    arguments,
                    TextSpan.FromBounds(newToken.Span.Start, arguments.Count > 0 ? arguments[^1].Span.End : typeToken.Span.End));
            }

            var constructorCloseParen = NextToken();
            return new NewObjectArgumentSyntax(
                typeName,
                arguments,
                TextSpan.FromBounds(newToken.Span.Start, constructorCloseParen.Span.End));
        }

        private ArgumentSyntax ParsePostfixChain(ArgumentSyntax expression, bool implicitCurrentItem = false)
        {
            while (true)
            {
                if (TryConsumePostfixToken(out var postfixToken, out var postfixText, out var nullSafe))
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

                break;
            }

            return expression;
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
                index = ParseArgument(implicitCurrentItem: implicitCurrentItem);
            }
            else
            {
                index = ParseArgument(implicitCurrentItem: implicitCurrentItem);

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    lookupKind = IndexLookupKind.ByKey;
                    NextToken();

                    if (Current.Kind != SyntaxTokenKind.CloseBracket)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh::parser::unsupported_double_index_lookup",
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
                    Code: "tosh::parser::expected_index_expression",
                    Title: "Index access requires an expression inside '[' and ']'.",
                    Span: openBracket.Span,
                    Label: "write an index, key, or value here"));

                index = new LiteralArgumentSyntax(null, openBracket.Span);
            }

            if (Current.Kind != SyntaxTokenKind.CloseBracket)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_bracket",
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
            if (allowMethodCall && Current.Kind == SyntaxTokenKind.OpenParen)
            {
                if (!IsValidIdentifier(postfixText))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::invalid_method_name",
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
                    NullSafe: nullSafe);
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
                        Code: "tosh::parser::unexpected_argument_separator",
                        Title: "An argument is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add an argument here"));
                    NextToken();
                    continue;
                }

                var argument = ParseArgument(implicitCurrentItem: implicitCurrentItem);

                if (argument is not null)
                {
                    arguments.Add(argument);
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseParen and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_argument_separator",
                        Title: "Arguments must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between arguments"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_parenthesis",
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

            if (HasTopLevelOperatorBeforeCloseParen())
            {
                var expression = ParseOperatorExpression(openParen.Span.Start, implicitCurrentItem);

                if (Current.Kind == SyntaxTokenKind.CloseParen)
                {
                    var operatorCloseParen = NextToken();
                    return expression with { Span = TextSpan.FromBounds(openParen.Span.Start, operatorCloseParen.Span.End) };
                }

                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this operator expression never closes",
                    Help: "close the expression with ')' after the right-hand operand."));
                return expression;
            }

            if (implicitCurrentItem && !HasTopLevelPipeBeforeCloseParen())
            {
                var predicateExpression = ParseWherePredicateExpression();

                if (Current.Kind == SyntaxTokenKind.CloseParen)
                {
                    var predicateCloseParen = NextToken();
                    return predicateExpression with { Span = TextSpan.FromBounds(openParen.Span.Start, predicateCloseParen.Span.End) };
                }

                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this subexpression never closes",
                    Help: "close the subexpression with ')' after the inner expression."));
                return predicateExpression;
            }

            var pipeline = ParsePipeline(
                untilCloseParen: true,
                untilCloseBrace: false,
                untilSemicolon: false,
                allowExpressionStart: true);

            if (Current.Kind != SyntaxTokenKind.CloseParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_parenthesis",
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
                    Code: "tosh::parser::missing_closing_parenthesis",
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
                    Code: "tosh::parser::missing_closing_parenthesis",
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
                    Code: "tosh::parser::missing_closing_parenthesis",
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

        private ArgumentSyntax ParseConditionalExpression(SyntaxToken openParen)
        {
            ArgumentSyntax? expression;

            if (HasTopLevelOperatorBeforeCloseParen())
            {
                expression = ParseOperatorExpression(openParen.Span.Start);

                if (Current.Kind == SyntaxTokenKind.CloseParen)
                {
                    var operatorCloseParen = NextToken();
                    return expression with { Span = TextSpan.FromBounds(openParen.Span.Start, operatorCloseParen.Span.End) };
                }

                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this condition never closes",
                    Help: "close the if-condition with ')' before the block."));
                return expression;
            }

            if (HasTopLevelPipeBeforeCloseParen())
            {
                var pipeline = ParsePipeline(
                    untilCloseParen: true,
                    untilCloseBrace: false,
                    untilSemicolon: false,
                    allowExpressionStart: true);

                if (Current.Kind == SyntaxTokenKind.CloseParen)
                {
                    var closeParen = NextToken();
                    return new SubexpressionArgumentSyntax(
                        pipeline,
                        TextSpan.FromBounds(openParen.Span.Start, closeParen.Span.End));
                }

                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this condition never closes",
                    Help: "close the if-condition with ')' before the block."));
                return new SubexpressionArgumentSyntax(pipeline, openParen.Span);
            }

            expression = ParseArgument();

            if (Current.Kind == SyntaxTokenKind.CloseParen)
            {
                var closeParen = NextToken();
                return expression is null
                    ? new BarewordArgumentSyntax(string.Empty, TextSpan.FromBounds(openParen.Span.Start, closeParen.Span.End))
                    : expression with { Span = TextSpan.FromBounds(openParen.Span.Start, closeParen.Span.End) };
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh::parser::missing_closing_parenthesis",
                Title: "A closing ')' is required here.",
                Span: openParen.Span,
                Label: "this condition never closes",
                Help: "close the if-condition with ')' before the block."));
            return expression ?? new BarewordArgumentSyntax(string.Empty, openParen.Span);
        }

        private ArgumentSyntax ParseOperatorExpression(int startPosition, bool implicitCurrentItem = false)
        {
            return ParseTernaryExpression(startPosition, implicitCurrentItem)
                   ?? new BarewordArgumentSyntax(string.Empty, new TextSpan(startPosition, 0));
        }

        private ArgumentSyntax? ParseTernaryExpression(int startPosition, bool implicitCurrentItem)
        {
            var condition = ParseNullCoalescingExpression(startPosition, implicitCurrentItem);

            if (!IsTernaryQuestionToken(Current))
            {
                return condition;
            }

            var questionToken = NextToken();
            var whenTrue = ParseTernaryExpression(questionToken.Span.End, implicitCurrentItem);

            SyntaxToken colonToken;
            if (IsTernaryColonToken(Current))
            {
                colonToken = NextToken();
            }
            else
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_ternary_colon",
                    Title: "A ternary expression requires ':'.",
                    Span: questionToken.Span,
                    Label: "this ternary expression is missing its ':' branch separator",
                    Help: "write `condition ? whenTrue : whenFalse` here."));
                colonToken = questionToken;
            }

            var whenFalse = ParseTernaryExpression(colonToken.Span.End, implicitCurrentItem);
            var end = whenFalse?.Span.End
                      ?? whenTrue?.Span.End
                      ?? colonToken.Span.End;

            return new ConditionalArgumentSyntax(
                condition ?? new BarewordArgumentSyntax(string.Empty, questionToken.Span),
                questionToken.Span,
                whenTrue ?? new BarewordArgumentSyntax(string.Empty, questionToken.Span),
                colonToken.Span,
                whenFalse ?? new BarewordArgumentSyntax(string.Empty, colonToken.Span),
                TextSpan.FromBounds(startPosition, end));
        }

        private ArgumentSyntax? ParseNullCoalescingExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseLogicalOrExpression(startPosition, implicitCurrentItem);

            while (Current.Kind == SyntaxTokenKind.QuestionQuestion)
            {
                var operatorToken = NextToken();
                var right = ParseLogicalOrExpression(startPosition, implicitCurrentItem);
                var end = right?.Span.End ?? operatorToken.Span.End;

                left = new OperatorArgumentSyntax(
                    left ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    "??",
                    operatorToken.Span,
                    right ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    TextSpan.FromBounds(startPosition, end));
            }

            return left;
        }

        private ArgumentSyntax? ParseLogicalOrExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseLogicalAndExpression(startPosition, implicitCurrentItem);

            while (IsLogicalOrOperatorToken(Current))
            {
                var operatorToken = NextToken();
                var right = ParseLogicalAndExpression(startPosition, implicitCurrentItem);
                var end = right?.Span.End ?? operatorToken.Span.End;

                left = new OperatorArgumentSyntax(
                    left ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    "or",
                    operatorToken.Span,
                    right ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    TextSpan.FromBounds(startPosition, end));
            }

            return left;
        }

        private ArgumentSyntax? ParseLogicalAndExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseComparisonExpression(startPosition, implicitCurrentItem);

            while (IsLogicalAndOperatorToken(Current))
            {
                var operatorToken = NextToken();
                var right = ParseComparisonExpression(startPosition, implicitCurrentItem);
                var end = right?.Span.End ?? operatorToken.Span.End;

                left = new OperatorArgumentSyntax(
                    left ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    "and",
                    operatorToken.Span,
                    right ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    TextSpan.FromBounds(startPosition, end));
            }

            return left;
        }

        private ArgumentSyntax? ParseComparisonExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseAdditiveExpression(startPosition, implicitCurrentItem);

            while (IsComparisonOperatorToken(Current))
            {
                var operatorToken = NextToken();
                var normalizedOperator = NormalizeBinaryOperator(operatorToken);

                // Handle "is not" as a compound operator → "is-not"
                if (normalizedOperator == "is" && Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, "not", StringComparison.OrdinalIgnoreCase))
                {
                    NextToken(); // consume "not"
                    normalizedOperator = "is-not";
                }

                // Handle "is in" as a compound operator → "is-in"
                if (normalizedOperator == "is" && Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, "in", StringComparison.OrdinalIgnoreCase))
                {
                    NextToken(); // consume "in"
                    normalizedOperator = "is-in";
                }

                // Handle "is not in" as a compound operator → "is-not-in"
                if (normalizedOperator == "is-not" && Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, "in", StringComparison.OrdinalIgnoreCase))
                {
                    NextToken(); // consume "in"
                    normalizedOperator = "is-not-in";
                }

                // Handle "not in" as a compound operator → "not-in"
                if (normalizedOperator == "not" && Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, "in", StringComparison.OrdinalIgnoreCase))
                {
                    NextToken(); // consume "in"
                    normalizedOperator = "not-in";
                }

                if (normalizedOperator == "=")
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::assignment_in_predicate",
                        Title: "Use '==' for equality comparisons, not '='.",
                        Span: operatorToken.Span,
                        Label: "did you mean '=='?",
                        Help: "try '==', '!=', 'in', '=~', 'and', 'or', or 'not' inside predicate expressions."));
                    normalizedOperator = "==";
                }

                var right = ParseAdditiveExpression(operatorToken.Span.End, implicitCurrentItem: false);
                var end = right?.Span.End ?? operatorToken.Span.End;

                left = new OperatorArgumentSyntax(
                    left ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    normalizedOperator,
                    operatorToken.Span,
                    right ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    TextSpan.FromBounds(startPosition, end));
            }

            return left;
        }

        private ArgumentSyntax? ParseAdditiveExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseMultiplicativeExpression(startPosition, implicitCurrentItem);

            while (IsAdditiveOperatorToken(Current))
            {
                var operatorToken = NextToken();
                var right = ParseMultiplicativeExpression(startPosition, implicitCurrentItem);
                var end = right?.Span.End ?? operatorToken.Span.End;

                left = new OperatorArgumentSyntax(
                    left ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    NormalizeBinaryOperator(operatorToken),
                    operatorToken.Span,
                    right ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    TextSpan.FromBounds(startPosition, end));
            }

            return left;
        }

        private ArgumentSyntax? ParseMultiplicativeExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseUnaryExpression(startPosition, implicitCurrentItem);

            while (IsMultiplicativeOperatorToken(Current))
            {
                var operatorToken = NextToken();
                var right = ParseUnaryExpression(startPosition, implicitCurrentItem);
                var end = right?.Span.End ?? operatorToken.Span.End;

                left = new OperatorArgumentSyntax(
                    left ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    NormalizeBinaryOperator(operatorToken),
                    operatorToken.Span,
                    right ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    TextSpan.FromBounds(startPosition, end));
            }

            return left;
        }

        private ArgumentSyntax? ParseUnaryExpression(int startPosition, bool implicitCurrentItem)
        {
            if (IsUnaryOperatorToken(Current))
            {
                var operatorToken = NextToken();
                var operand = ParseUnaryExpression(startPosition, implicitCurrentItem);
                var end = operand?.Span.End ?? operatorToken.Span.End;

                return new UnaryOperatorArgumentSyntax(
                    "not",
                    operatorToken.Span,
                    operand ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    TextSpan.FromBounds(startPosition, end));
            }

            return ParseArgumentOperand(implicitCurrentItem);
        }

        private ArgumentSyntax? ParseArgumentOperand(bool implicitCurrentItem = false)
        {
            if (Current.Kind is SyntaxTokenKind.CloseParen or SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_operand",
                    Title: "Expected an operand in this expression.",
                    Span: Current.Span,
                    Label: "operators need a value on both sides"));
                return null;
            }

            return ParseArgument(implicitCurrentItem: implicitCurrentItem);
        }

        private ArgumentSyntax? ParseWherePredicateExpressionArgument()
        {
            var expression = ParseWherePredicateExpression();

            if (expression is null)
            {
                return null;
            }

            var stage = new ExpressionPipelineStageSyntax(expression, expression.Span);
            var pipeline = new PipelineSyntax([stage]);
            var statement = new PipelineStatementSyntax(pipeline, expression.Span);
            var block = new BlockSyntax([statement], expression.Span);
            return new BlockArgumentSyntax(block, expression.Span);
        }

        private ArgumentSyntax ParseWherePredicateExpression()
        {
            return ParseOperatorExpression(Current.Span.Start, implicitCurrentItem: true);
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

                if (expression is not null && HasImplicitStatementBoundaryAfter(expression.Span.End))
                {
                    continue;
                }

                if (Current.Kind != SyntaxTokenKind.CloseBrace && Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_predicate_separator",
                        Title: "Predicate expressions must be separated by ';' or a newline.",
                        Span: Current.Span,
                        Label: "insert ';' or a newline between predicate expressions"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_brace",
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

        private PipelineSyntax ParsePipeline(
            bool untilCloseParen,
            bool untilCloseBrace,
            bool untilSemicolon,
            bool allowExpressionStart)
        {
            var stages = new List<PipelineStageSyntax>();
            List<RedirectionSyntax>? redirections = null;
            var isBackground = false;

            while (!IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon))
            {
                if (Current.Kind == SyntaxTokenKind.Ampersand)
                {
                    // &name at the start of a pipeline is a function reference expression, not background.
                    if (stages.Count == 0 && allowExpressionStart &&
                        Peek(1).Kind == SyntaxTokenKind.Bareword && IsValidCommandName(Peek(1).Text))
                    {
                        // Fall through to let ParsePipelineStage handle it as an expression.
                    }
                    else if (stages.Count == 0)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh::parser::unexpected_background_operator",
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

                if (Current.Kind == SyntaxTokenKind.Pipe)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::unexpected_pipeline_separator",
                        Title: "Unexpected pipeline separator.",
                        Span: Current.Span,
                        Label: "remove this '|' or put a stage before it"));
                    NextToken();
                    continue;
                }

                var stage = ParsePipelineStage(
                    allowExpressionStage: allowExpressionStart && stages.Count == 0,
                    stopAtCloseParen: untilCloseParen,
                    stopAtCloseBrace: untilCloseBrace,
                    stopAtSemicolon: untilSemicolon);

                if (stage is not null)
                {
                    stages.Add(stage);
                }

                // Check for redirection operators after a pipeline stage
                while (LooksLikeRedirectionOperator())
                {
                    var redirection = TryParseRedirection();
                    if (redirection is not null)
                    {
                        redirections ??= new List<RedirectionSyntax>();
                        redirections.Add(redirection);
                    }
                }

                if (Current.Kind == SyntaxTokenKind.Ampersand)
                {
                    NextToken();
                    isBackground = true;
                    break;
                }

                if (Current.Kind == SyntaxTokenKind.Pipe)
                {
                    var pipe = NextToken();

                    if (IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon))
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh::parser::missing_command_after_pipe",
                            Title: "A command is required after '|'.",
                            Span: pipe.Span,
                            Label: "a pipeline cannot end here",
                            Help: "add another command after the pipe."));
                    }

                    continue;
                }

                if (stage is not null && HasImplicitStatementBoundaryAfter(stage.Span.End))
                {
                    break;
                }

                if (IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon))
                {
                    break;
                }

                if (stage is ExpressionPipelineStageSyntax)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_pipeline_separator",
                        Title: "Expression pipeline stages must be separated by '|'.",
                        Span: Current.Span,
                        Label: "insert '|' before the next command"));
                    SkipToStageBoundary(untilCloseParen, untilCloseBrace, untilSemicolon);
                    continue;
                }
            }

            return new PipelineSyntax(stages, redirections, isBackground);
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

        private bool HasImplicitStatementBoundaryAfter(int previousEnd)
        {
            return Current.Kind != SyntaxTokenKind.EndOfFile &&
                   HasLineBreakBetween(previousEnd, Current.Span.Start) &&
                   LooksLikeStatementStart(Current);
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

        private bool LooksLikeStatementStart(SyntaxToken token)
        {
            return token.Kind switch
            {
                SyntaxTokenKind.String or
                SyntaxTokenKind.Number or
                SyntaxTokenKind.Boolean or
                SyntaxTokenKind.Null or
                SyntaxTokenKind.UnitLiteral or
                SyntaxTokenKind.OpenBrace or
                SyntaxTokenKind.OpenParen or
                SyntaxTokenKind.OpenBracket => true,
                SyntaxTokenKind.Bareword => true,
                _ => false,
            };
        }

        private bool HasTopLevelOperatorBeforeCloseParen() => HasTopLevelOperatorBeforeCloseParen(_position);

        private bool HasTopLevelOperatorBeforeStageBoundary(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
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
                        depth++;
                        break;

                    case SyntaxTokenKind.CloseParen:
                        if (depth == 0)
                        {
                            if (stopAtCloseParen)
                            {
                                return false;
                            }

                            continue;
                        }

                        depth--;
                        break;

                    case SyntaxTokenKind.CloseBrace:
                        if (depth == 0)
                        {
                            if (stopAtCloseBrace)
                            {
                                return false;
                            }

                            continue;
                        }

                        depth--;
                        break;

                    case SyntaxTokenKind.CloseBracket:
                        if (depth > 0)
                        {
                            depth--;
                        }
                        break;

                    case SyntaxTokenKind.Pipe:
                    case SyntaxTokenKind.Ampersand:
                        if (depth == 0)
                        {
                            return false;
                        }
                        break;

                    case SyntaxTokenKind.Semicolon:
                        if (depth == 0 && stopAtSemicolon)
                        {
                            return false;
                        }
                        break;

                    default:
                        if (depth == 0 &&
                            (IsTernaryQuestionToken(token) ||
                             IsNullCoalescingOperatorToken(token) ||
                             IsLogicalOrOperatorToken(token) ||
                             IsLogicalAndOperatorToken(token) ||
                             IsComparisonOperatorToken(token) ||
                             IsAdditiveOperatorToken(token) ||
                             IsMultiplicativeOperatorToken(token) ||
                             IsUnaryOperatorToken(token)))
                        {
                            return true;
                        }
                        break;
                }
            }

            return false;
        }

        private bool HasTopLevelOperatorBeforeCloseParen(int startIndex)
        {
            var depth = 0;

            for (var index = startIndex; index < _tokens.Count; index++)
            {
                var token = _tokens[index];

                switch (token.Kind)
                {
                    case SyntaxTokenKind.OpenParen:
                    case SyntaxTokenKind.OpenBrace:
                    case SyntaxTokenKind.OpenBracket:
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
                        if (depth > 0)
                        {
                            depth--;
                        }
                        break;

                    case SyntaxTokenKind.Pipe:
                        if (depth == 0)
                        {
                            return false;
                        }
                        break;

                    default:
                        if (depth == 0 &&
                            (IsTernaryQuestionToken(token) ||
                             IsNullCoalescingOperatorToken(token) ||
                             IsLogicalOrOperatorToken(token) ||
                             IsLogicalAndOperatorToken(token) ||
                             IsComparisonOperatorToken(token) ||
                             IsAdditiveOperatorToken(token) ||
                             IsMultiplicativeOperatorToken(token) ||
                             IsUnaryOperatorToken(token)))
                        {
                            return true;
                        }
                        break;
                }
            }

            return false;
        }

        private bool HasTopLevelPipeBeforeCloseParen()
        {
            var depth = 0;

            for (var index = _position; index < _tokens.Count; index++)
            {
                var token = _tokens[index];

                switch (token.Kind)
                {
                    case SyntaxTokenKind.OpenParen:
                    case SyntaxTokenKind.OpenBrace:
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
                        if (depth > 0)
                        {
                            depth--;
                        }
                        break;

                    case SyntaxTokenKind.Pipe:
                        if (depth == 0)
                        {
                            return true;
                        }
                        break;
                }
            }

            return false;
        }

        private bool LooksLikeVariableDeclaration()
        {
            var offset = GetDeclarationModifierOffset();

            if (!MatchesKeywordAtOffset(offset, "var"))
            {
                return false;
            }

            // Destructuring: var { ... } = ... or var [ ... ] = ...
            var afterVar = Peek(offset + 1);
            if (afterVar.Kind is SyntaxTokenKind.OpenBrace or SyntaxTokenKind.OpenBracket)
            {
                return true;
            }

            if (afterVar.Kind != SyntaxTokenKind.Bareword ||
                !IsValidIdentifier(afterVar.Text))
            {
                return false;
            }

            return IsEqualsToken(Peek(offset + 2)) ||
                   Peek(offset + 2).Kind is SyntaxTokenKind.EndOfFile or SyntaxTokenKind.Semicolon or SyntaxTokenKind.CloseBrace or SyntaxTokenKind.CloseParen ||
                   HasLineBreakBetween(Peek(offset + 1).Span.End, Peek(offset + 2).Span.Start);
        }

        private bool LooksLikeAllocStatement()
        {
            var offset = GetDeclarationModifierOffset();

            return MatchesKeywordAtOffset(offset, "alloc") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   IsEqualsToken(Peek(offset + 2));
        }


        private bool LooksLikeUsingStatement()
        {
            var offset = GetDeclarationModifierOffset();
            return MatchesKeywordAtOffset(offset, "using");
        }

        private bool LooksLikeRequireStatement()
        {
            var offset = GetDeclarationModifierOffset();
            return MatchesKeywordAtOffset(offset, "require");
        }

        private bool LooksLikeBindStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "bind", StringComparison.OrdinalIgnoreCase) &&
                   Peek(1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(1).Text);
        }

        private bool LooksLikeFunctionDefinition()
        {
            var offset = GetDeclarationModifierOffset();
            return MatchesKeywordAtOffset(offset, "func") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidCommandName(Peek(offset + 1).Text) &&
                   (Peek(offset + 2).Kind == SyntaxTokenKind.OpenParen || IsFatArrowToken(Peek(offset + 2), Peek(offset + 3)));
        }

        private bool LooksLikeClassDefinition()
        {
            var offset = GetDeclarationModifierOffset();
            return MatchesKeywordAtOffset(offset, "class") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   (Peek(offset + 2).Kind == SyntaxTokenKind.OpenParen ||
                    Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace);
        }

        private bool LooksLikeModuleDefinition()
        {
            var offset = GetDeclarationModifierOffset();
            return MatchesKeywordAtOffset(offset, "module") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace;
        }

        private bool LooksLikeEnumDefinition()
        {
            var offset = GetDeclarationModifierOffset();
            if (!MatchesKeywordAtOffset(offset, "enum") ||
                Peek(offset + 1).Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            ParseTypedIdentifierToken(
                Peek(offset + 1).Text,
                out var name,
                out _,
                out var expectsFollowingUnderlyingType);

            return IsValidIdentifier(name) &&
                   (Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace ||
                    expectsFollowingUnderlyingType ||
                    (Peek(offset + 2).Kind == SyntaxTokenKind.Bareword && Peek(offset + 2).Text == ":"));
        }

        private bool LooksLikeRecordDefinition()
        {
            var offset = GetDeclarationModifierOffset();
            return MatchesKeywordAtOffset(offset, "record") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   Peek(offset + 2).Kind == SyntaxTokenKind.OpenParen;
        }

        private bool LooksLikeEventDefinition()
        {
            var offset = GetDeclarationModifierOffset();

            // local event Name { ... }
            if (MatchesKeywordAtOffset(offset, "local") &&
                MatchesKeywordAtOffset(offset + 1, "event") &&
                Peek(offset + 2).Kind == SyntaxTokenKind.Bareword &&
                IsValidIdentifier(Peek(offset + 2).Text))
            {
                return true;
            }

            // required event Name { ... }
            if (MatchesKeywordAtOffset(offset, "required") &&
                MatchesKeywordAtOffset(offset + 1, "event") &&
                Peek(offset + 2).Kind == SyntaxTokenKind.Bareword &&
                IsValidIdentifier(Peek(offset + 2).Text))
            {
                return true;
            }

            // event Name { ... }
            return MatchesKeywordAtOffset(offset, "event") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace;
        }

        private bool LooksLikeReturnStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "return", StringComparison.OrdinalIgnoreCase);
        }

        private bool LooksLikeBreakStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "break", StringComparison.OrdinalIgnoreCase);
        }

        private bool LooksLikeContinueStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "continue", StringComparison.OrdinalIgnoreCase);
        }

        private bool LooksLikeIfStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "if", StringComparison.OrdinalIgnoreCase);
        }

        private bool LooksLikeForStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "for", StringComparison.OrdinalIgnoreCase);
        }

        private bool LooksLikeWhileStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "while", StringComparison.OrdinalIgnoreCase);
        }

        private bool LooksLikeUntilStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "until", StringComparison.OrdinalIgnoreCase);
        }

        private bool LooksLikeThrowStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "throw", StringComparison.OrdinalIgnoreCase);
        }

        private bool LooksLikeTryStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "try", StringComparison.OrdinalIgnoreCase);
        }

        private bool LooksLikeDeferStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "defer", StringComparison.OrdinalIgnoreCase) &&
                   Peek(1).Kind == SyntaxTokenKind.OpenBrace;
        }

        private bool LooksLikeSwitchStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "switch", StringComparison.OrdinalIgnoreCase);
        }

        private bool LooksLikeMatchExpression()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "match", StringComparison.OrdinalIgnoreCase) &&
                   Peek(1).Kind == SyntaxTokenKind.OpenParen;
        }

        private bool LooksLikeIfExpression()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "if", StringComparison.OrdinalIgnoreCase) &&
                   Peek(1).Kind == SyntaxTokenKind.OpenParen;
        }

        private static bool CommandExpectsTypeNameArguments(string commandName)
        {
            return commandName is
                "cast" or
                "constructors" or
                "describe-type" or
                "help" or
                "members" or
                "methods" or
                "get-methods";
        }

        private bool LooksLikeTypedVariableDeclaration()
        {
            // Pattern: TypeName identifier =
            // By this point all keyword-led statements (for, while, until, if, var, alias,
            // using, require, func, return, throw, break, continue) have already been checked,
            // so any remaining Bareword Bareword = must be a typed declaration.
            var offset = GetDeclarationModifierOffset();
            return TryGetTypeNameEndOffset(offset, out var typeNameEndOffset) &&
                   Peek(typeNameEndOffset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(typeNameEndOffset + 1).Text) &&
                   IsEqualsToken(Peek(typeNameEndOffset + 2));
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
                    string.Equals(Peek(1).Text, "using", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "require", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "func", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "class", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "module", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "enum", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "record", StringComparison.Ordinal));
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

        private bool LooksLikeVariableAssignment()
        {
            return IsAssignableVariableToken(Current) &&
                   IsAssignmentOperatorToken(Peek(1));
        }

        private bool LooksLikeMemberAssignment()
        {
            if (!CanStartMemberAssignmentTarget(Current))
            {
                return false;
            }

            var offset = 1;
            var hasMemberPath = HasEmbeddedAssignmentMemberPath(Current);

            while (IsPostfixToken(Peek(offset)))
            {
                hasMemberPath = true;
                offset++;
            }

            return hasMemberPath && IsAssignmentOperatorToken(Peek(offset));
        }

        private bool LooksLikeExpressionStage()
        {
            return Current.Kind switch
            {
                SyntaxTokenKind.String or SyntaxTokenKind.Number or SyntaxTokenKind.Boolean or SyntaxTokenKind.Null or SyntaxTokenKind.UnitLiteral or SyntaxTokenKind.OpenParen or SyntaxTokenKind.DollarOpenParen or SyntaxTokenKind.LessThanOpenParen or SyntaxTokenKind.OpenBracket or SyntaxTokenKind.OpenBrace or SyntaxTokenKind.InterpolatedString => true,
                SyntaxTokenKind.Ampersand => Peek(1).Kind == SyntaxTokenKind.Bareword && IsValidCommandName(Peek(1).Text),
                SyntaxTokenKind.Bareword => IsVariableReferenceLikeToken(Current) ||
                                            LooksLikeAnonymousFunctionExpression() ||
                                            LooksLikeMatchExpression() ||
                                            LooksLikeIfExpression() ||
                                            LooksLikeNameOfExpression() ||
                                            LooksLikeNewObjectExpression() ||
                                            LooksLikeStaticMethodCallExpression() ||
                                            LooksLikeStaticMemberAccessExpression() ||
                                            LooksLikeIntrinsicLiteralExpression(),
                _ => false,
            };
        }

        private bool LooksLikeAnonymousFunctionExpression()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "func", StringComparison.Ordinal) &&
                   Peek(1).Kind == SyntaxTokenKind.OpenParen;
        }

        private bool LooksLikeIntrinsicLiteralExpression()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   IntrinsicLiteralParser.TryParseExpressionLiteral(Current.Text, out _);
        }

        private bool LooksLikeNameOfExpression()
        {
            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            // nameof($var) — function-call style
            if (string.Equals(Current.Text, "nameof", StringComparison.Ordinal) &&
                Peek(1).Kind == SyntaxTokenKind.OpenParen)
            {
                return true;
            }

            // name-of $var — command style (no parens)
            if (string.Equals(Current.Text, "name-of", StringComparison.OrdinalIgnoreCase) &&
                Peek(1).Kind == SyntaxTokenKind.Bareword)
            {
                return true;
            }

            return false;
        }

        private bool LooksLikeNewObjectExpression()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "new", StringComparison.Ordinal) &&
                   TryGetTypeNameEndOffset(1, out var typeNameEndOffset) &&
                   Peek(typeNameEndOffset + 1).Kind == SyntaxTokenKind.OpenParen;
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

            if (Peek(endOffset + 1).Kind == SyntaxTokenKind.Bareword &&
                Peek(endOffset + 1).Text == "?")
            {
                endOffset++;
            }

            return true;
        }

        private bool LooksLikePotentialTypeName(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text[0] == '$')
            {
                return false;
            }

            return IsValidIdentifier(text) ||
                   LooksLikeQualifiedDotNetAccess(text) ||
                   LooksLikePotentialClrTypeName(text);
        }

        private bool LooksLikeStaticMethodCallExpression()
        {
            if (Current.Kind != SyntaxTokenKind.Bareword || Peek(1).Kind != SyntaxTokenKind.OpenParen)
            {
                return false;
            }

            if (LooksLikeQualifiedDotNetAccess(Current.Text))
            {
                return true;
            }

            if (!LooksLikePotentialClrTypeName(Current.Text))
            {
                return false;
            }

            // Disambiguate: an unqualified PascalCase name followed by parens containing
            // operator expressions (e.g., Name($a + "," + $b)) is a bareword argument
            // followed by a parenthesized subexpression, not a static method call.
            // Peek(2) is the first token inside the parens (after the open paren).
            if (!Current.Text.Contains('.') && HasTopLevelOperatorBeforeCloseParen(_position + 2))
            {
                return false;
            }

            return true;
        }

        private bool LooksLikeStaticMemberAccessExpression()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   LooksLikeQualifiedDotNetAccess(Current.Text);
        }

        private static bool IsBinaryOperatorToken(SyntaxToken token)
        {
            return token.Kind is SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanEqual
                    or SyntaxTokenKind.LessThan or SyntaxTokenKind.LessThanEqual
                    or SyntaxTokenKind.BangEqual or SyntaxTokenKind.BangTilde
                || (token.Kind == SyntaxTokenKind.Bareword && IsBinaryOperator(token.Text));
        }

        private static bool IsLogicalOrOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.DoublePipe
                || (token.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(token.Text, "or", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsTernaryQuestionToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(token.Text, "?", StringComparison.Ordinal);
        }

        private static bool IsNullCoalescingOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.QuestionQuestion;
        }

        private static bool IsTernaryColonToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(token.Text, ":", StringComparison.Ordinal);
        }

        private static bool IsLogicalAndOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.DoubleAmpersand
                || (token.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(token.Text, "and", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsComparisonOperatorToken(SyntaxToken token)
        {
            return token.Kind is SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanEqual
                or SyntaxTokenKind.LessThan or SyntaxTokenKind.LessThanEqual
                or SyntaxTokenKind.BangEqual or SyntaxTokenKind.BangTilde
                || IsEqualsToken(token)
                || (token.Kind == SyntaxTokenKind.Bareword && token.Text is
                    "==" or "=~" or "in" or "not-in" or "contains" or "starts-with" or "ends-with" or "is" or "is-not" or "not" or "as");
        }

        private static bool IsAdditiveOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && token.Text is "+" or "-";
        }

        private static bool IsMultiplicativeOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && token.Text is "*" or "/" or "%";
        }

        private static bool IsUnaryOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(token.Text, "not", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeBinaryOperator(SyntaxToken token)
        {
            return token.Kind switch
            {
                SyntaxTokenKind.GreaterThan => ">",
                SyntaxTokenKind.GreaterThanEqual => ">=",
                SyntaxTokenKind.LessThan => "<",
                SyntaxTokenKind.LessThanEqual => "<=",
                SyntaxTokenKind.BangEqual => "!=",
                SyntaxTokenKind.BangTilde => "!~",
                SyntaxTokenKind.DoublePipe => "or",
                SyntaxTokenKind.DoubleAmpersand => "and",
                _ when IsEqualsToken(token) => "=",
                _ => token.Text.ToLowerInvariant() switch
                {
                    "=~" => "=~",
                    "in" => "in",
                    "not-in" => "not-in",
                    "or" => "or",
                    "and" => "and",
                    "==" => "==",
                    "contains" => "contains",
                    "starts-with" => "starts-with",
                    "ends-with" => "ends-with",
                    "as" => "as",
                    "+" => "+",
                    "-" => "-",
                    "*" => "*",
                    "/" => "/",
                    "%" => "%",
                    _ => token.Text,
                },
            };
        }

        private static bool IsWhereComparisonOperator(SyntaxToken token)
        {
            return token.Kind is SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanEqual
                    or SyntaxTokenKind.LessThan or SyntaxTokenKind.LessThanEqual
                    or SyntaxTokenKind.BangEqual or SyntaxTokenKind.BangTilde
                || (token.Kind == SyntaxTokenKind.Bareword && token.Text is
                    "==" or "=" or "=~" or "in" or "not-in" or "contains" or "starts-with" or "ends-with");
        }

        private static bool IsBinaryOperator(string text)
        {
            return text is "+" or "-" or "*" or "/" or "%" or "==" or "=~" or "in" or "not-in" or "and" or "or";
        }

        private bool LooksLikeRecordLiteral()
        {
            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                return false;
            }

            var next = Peek(1);

            if (next.Kind == SyntaxTokenKind.CloseBrace)
            {
                return true;
            }

            // Computed property: { ($expr) = value }
            if (next.Kind == SyntaxTokenKind.OpenParen)
            {
                return true;
            }

            // Spread entry: { ...$var }
            if (next.Kind == SyntaxTokenKind.Bareword &&
                next.Text.StartsWith("...", StringComparison.Ordinal) &&
                next.Text.Length > 3)
            {
                return true;
            }

            if (next.Kind is not SyntaxTokenKind.Bareword and not SyntaxTokenKind.String)
            {
                return false;
            }

            return IsEqualsToken(Peek(2));
        }

        private static bool IsEqualsToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && string.Equals(token.Text, "=", StringComparison.Ordinal);
        }

        private static bool IsAssignmentOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword &&
                   token.Text is "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "??=";
        }

        private static string NormalizeAssignmentOperator(SyntaxToken token)
        {
            return token.Text switch
            {
                "=" => "=",
                "+=" => "+=",
                "-=" => "-=",
                "*=" => "*=",
                "/=" => "/=",
                "%=" => "%=",
                "??=" => "??=",
                _ => "=",
            };
        }

        private static bool IsFatArrowToken(SyntaxToken current, SyntaxToken next)
        {
            return (current.Kind == SyntaxTokenKind.Bareword && current.Text == "=" && next.Kind == SyntaxTokenKind.GreaterThan) ||
                   (current.Kind == SyntaxTokenKind.Bareword && current.Text == "=>");
        }

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

        private bool LooksLikeRedirectionOperator()
        {
            // Matches: o> o>> out> out>> e> e>> err> err>> o+e> o+e>> out+err> out+err>> err+out> err+out>>
            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            var text = Current.Text;
            var next = Peek(1);

            return text is "o" or "out" or "e" or "err" or "o+e" or "e+o" or "out+err" or "err+out"
                   && next.Kind is SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanGreaterThan;
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
                    Code: "tosh::parser::expected_redirection_target",
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

        private static bool IsPipelineTerminator(
            SyntaxTokenKind kind,
            bool untilCloseParen,
            bool untilCloseBrace,
            bool untilSemicolon)
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

            return false;
        }

        private bool IsDeclarationBoundary(int previousEnd, bool untilCloseParen, bool untilCloseBrace, bool untilSemicolon)
        {
            return IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon) ||
                   HasImplicitStatementBoundaryAfter(previousEnd);
        }

        private bool TryConsumePostfixToken(out SyntaxToken token, out string postfixText, out bool nullSafe)
        {
            if (IsPostfixToken(Current))
            {
                token = NextToken();
                postfixText = token.Text[1..];
                nullSafe = false;
                return true;
            }

            if (Current.Kind == SyntaxTokenKind.QuestionDot && Peek(1).Kind == SyntaxTokenKind.Bareword)
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

        private bool LooksLikeSplatArgument()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   Current.Text.StartsWith("...", StringComparison.Ordinal) &&
                   Current.Text.Length > 3;
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
                    Code: "tosh::parser::variable_references_require_dollar",
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
                Code: "tosh::parser::expected_variable_name",
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

        private static bool LooksLikeQualifiedDotNetAccess(string text)
        {
            if (string.IsNullOrWhiteSpace(text) ||
                !text.Contains('.', StringComparison.Ordinal) ||
                text[0] == '.')
            {
                return false;
            }

            var firstSegment = text.Split('.', 2, StringSplitOptions.None)[0];

            if (string.IsNullOrWhiteSpace(firstSegment))
            {
                return false;
            }

            return char.IsUpper(firstSegment[0]) || string.Equals(firstSegment, "string", StringComparison.Ordinal);
        }

        private static bool LooksLikePotentialClrTypeName(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (LooksLikeQualifiedDotNetAccess(text))
            {
                return true;
            }

            return char.IsUpper(text[0]);
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

                if (!(char.IsLetterOrDigit(character) || character == '_'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidCommandName(string text)
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
                   token.Text[0] == '.';
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

        private static bool IsExplicitBackgroundStatementBoundary(StatementSyntax statement)
        {
            return statement is PipelineStatementSyntax { Pipeline.IsBackground: true };
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
    }
}
