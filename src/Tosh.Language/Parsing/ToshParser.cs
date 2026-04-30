using System.Text;
using System.Globalization;
using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public static class ToshParser
{
    public static ParseResult Parse(string source, string sourceName = "<input>")
    {
        try
        {
            var sourceText = source ?? string.Empty;
            var lexer = new ToshLexer(sourceText);
            var tokens = lexer.Lex();
            var parser = new InternalParser(sourceName, sourceText, tokens, lexer.LineHushDirectives);
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
        private readonly IReadOnlyList<LineHushDirective> _lineHushDirectives;
        private readonly List<SyntaxDiagnostic> _diagnostics = [];
        private int _position;
        private bool _stopRefinementAtEquals;

        public InternalParser(
            string sourceName,
            string sourceText,
            IReadOnlyList<SyntaxToken> tokens,
            IReadOnlyList<LineHushDirective> lineHushDirectives)
        {
            _sourceName = sourceName;
            _sourceText = sourceText;
            _tokens = tokens;
            _lineHushDirectives = lineHushDirectives;
        }

        public ParseResult Parse()
        {
            var statement = ParseScript();
            return new ParseResult(_sourceName, _sourceText, statement, _diagnostics, _lineHushDirectives);
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
                        Code: "tosh.parser.missing_statement_separator",
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
                1 when statements[0] is ScriptInputStatementSyntax or SubcommandStatementSyntax => new ScriptStatementSyntax(
                    statements,
                    statements[0].Span),
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
            var docTokens = ConsumeDocCommentTokens();

            // Tuple unpacking assignment: ($a, $b) = ...
            if (Current.Kind == SyntaxTokenKind.OpenParen && LooksLikeTupleAssignment())
            {
                var start = Current.Span.Start;
                var names = new List<string>();
                var openParen = NextToken();
                while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseParen)
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
                    else if (IsVariableReferenceLikeToken(Current))
                    {
                        names.Add(ParseAssignableVariableName());
                    }
                    else
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.expected_tuple_assign_name",
                            Title: "Expected a variable name in tuple assignment.",
                            Span: Current.Span,
                            Label: "write a variable name like 'a' or '$a'"));
                        NextToken();
                    }
                    if (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken();
                    }
                }
                var closeSpan = Current.Span;
                if (Current.Kind == SyntaxTokenKind.CloseParen)
                {
                    NextToken();
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_closing_paren_tuple_assign",
                        Title: "A closing ')' is required for tuple assignment.",
                        Span: closeSpan,
                        Label: "close the tuple assignment with ')'"));
                }
                if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == "=")
                {
                    NextToken();
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_equals_tuple_assign",
                        Title: "Tuple assignment requires '=' after the variable list.",
                        Span: Current.Span,
                        Label: "write '=' after the variable list"));
                }
                var value = ParsePipeline(
                    untilCloseParen: stopAtCloseParen,
                    untilCloseBrace: stopAtCloseBrace,
                    untilSemicolon: stopAtSemicolon,
                    allowExpressionStart: true);
                var end = GetPipelineEnd(value, closeSpan.End);
                return new TupleAssignmentStatementSyntax(
                    names,
                    value,
                    TextSpan.FromBounds(start, end));
            }

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

            if (LooksLikeTypeAliasDeclaration())
            {
                return ParseTypeAliasStatement(docTokens);
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
                return ParseFunctionDefinitionStatement(docTokens);
            }

            if (LooksLikeScriptInputDeclaration())
            {
                return ParseScriptInputStatement(docTokens);
            }

            if (LooksLikeSubcommandDeclaration())
            {
                return ParseSubcommandStatement(docTokens);
            }

            if (LooksLikeRuneDefinition())
            {
                return ParseRuneDefinitionStatement(docTokens);
            }

            if (LooksLikeClassDefinition())
            {
                return ParseClassDefinitionStatement(docTokens);
            }

            if (LooksLikeInterfaceDefinition())
            {
                return ParseInterfaceDefinitionStatement(docTokens);
            }

            if (LooksLikeUnionDefinition())
            {
                return ParseUnionDefinitionStatement(docTokens);
            }

            if (LooksLikeModuleDefinition())
            {
                return ParseModuleDefinitionStatement(docTokens);
            }

            if (LooksLikeEnumDefinition())
            {
                return ParseEnumDefinitionStatement(docTokens, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeRecordDefinition())
            {
                return ParseRecordDefinitionStatement(docTokens, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeStructDefinition())
            {
                return ParseStructDefinitionStatement(docTokens);
            }

            if (LooksLikeTraitDefinition())
            {
                return ParseTraitDefinitionStatement(docTokens);
            }

            if (LooksLikeEventDefinition())
            {
                return ParseEventDefinitionStatement(docTokens);
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

            if (LooksLikeYieldStatement())
            {
                return ParseYieldStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
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
                    Code: "tosh.parser.expected_for_in",
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
                    Code: $"tosh.parser.expected_{keyword}_condition",
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
                    Code: "tosh.parser.expected_if_condition",
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
                        Code: "tosh.parser.expected_else_block",
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
            var isConst = string.Equals(varToken.Text, "const", StringComparison.Ordinal);

            // Destructuring: var { a, b } = ... or var [a, b] = ...
            if (Current.Kind is SyntaxTokenKind.OpenBrace or SyntaxTokenKind.OpenBracket)
            {
                return ParseDestructuringDeclaration(declarationStart, modifier, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            var nameToken = Current.Kind == SyntaxTokenKind.Bareword ? NextToken() : ExpectVariableName();
            ParseTypedIdentifierToken(nameToken.Text, out var declaredName, out var inlineTypeName, out var expectsFollowingTypeName);

            if (!IsValidIdentifier(declaredName))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_variable_name",
                    Title: "Expected a variable name.",
                    Span: nameToken.Span,
                    Label: "variables need a C#-style identifier like 'answer' or 'fileList'"));
                declaredName = string.Empty;
            }

            // Resolve type annotation (var name: Type or var name:Type)
            string? typeName = inlineTypeName;
            if (expectsFollowingTypeName)
            {
                typeName = ParseTypeName("variable type");
            }

            var refinement = typeName is not null ? TryParseRefinementClause() : null;
            var declarationHeadEnd = refinement?.Span.End ?? nameToken.Span.End;

            if (IsDeclarationBoundary(nameToken.Span.End, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon))
            {
                if (isConst)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.const_requires_value",
                        Title: "A 'const' declaration requires an initializer.",
                        Span: varToken.Span,
                        Label: "use 'const x = ...' to declare a constant"));
                }

                return new VariableDeclarationStatementSyntax(
                    declaredName,
                    typeName,
                    null,
                    modifier,
                    isConst,
                    TextSpan.FromBounds(declarationStart, declarationHeadEnd),
                    refinement);
            }

            var equalsToken = ExpectEqualsToken("Variable declarations use '=' after the variable name.");
            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, equalsToken.Span.End);
            return new VariableDeclarationStatementSyntax(
                declaredName,
                typeName,
                value,
                modifier,
                isConst,
                TextSpan.FromBounds(declarationStart, end),
                refinement);
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
            var refinement = typeName is not null ? TryParseRefinementClause() : null;
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
                false,
                TextSpan.FromBounds(declarationStart, end),
                refinement);
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
                    Code: "tosh.parser.expected_using_target",
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
                    Code: "tosh.parser.using_requires_namespace",
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

        private StatementSyntax ParseTypeAliasStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var docComment = DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>());
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            var typeToken = NextToken();
            var nameToken = ExpectVariableName();
            var typeParameters = ParseTypeParameterList();
            var equalsToken = ExpectEqualsToken("Type aliases use '=' between the alias name and the base type.");
            var baseTypeName = ParseTypeName("alias base type");
            var rangeRefinement = TryParseTypeAliasRangeRefinementClause();
            var refinement = TryParseRefinementClause();
            var blockRefinement = TryParseTypeAliasRefinementBlock();

            refinement = MergeRefinementSpecifications(rangeRefinement, refinement, blockRefinement);

            var end = refinement?.Span.End ?? Peek(-1).Span.End;

            if (string.IsNullOrWhiteSpace(baseTypeName))
            {
                end = equalsToken.Span.End;
            }

            return new TypeAliasStatementSyntax(
                nameToken.Text,
                typeParameters,
                baseTypeName,
                refinement,
                modifier,
                TextSpan.FromBounds(declarationStart, Math.Max(typeToken.Span.End, end)),
                docComment);
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
                        Code: "tosh.parser.expected_require_from",
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
                    Code: "tosh.parser.expected_require_target",
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
                    Code: "tosh.parser.expected_bind_body",
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
                            Code: "tosh.parser.expected_catch_variable",
                            Title: "Catch clauses require a variable name when parentheses are used.",
                            Span: invalidToken.Span,
                            Label: "write a variable like 'err' or '$err'"));
                    }

                    if (Current.Kind != SyntaxTokenKind.CloseParen)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.missing_closing_parenthesis",
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
                    Code: "tosh.parser.try_requires_handler",
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
                    Code: "tosh.parser.expected_switch_value",
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
                    Code: "tosh.parser.expected_switch_block",
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
                    var matchExpression = TryParsePatternExpression()
                                          ?? ParseArgument()
                                          ?? new BarewordArgumentSyntax(string.Empty, caseToken.Span);

                    ArgumentSyntax? guard = null;
                    if (Current.Kind == SyntaxTokenKind.Bareword &&
                        string.Equals(Current.Text, "if", StringComparison.OrdinalIgnoreCase))
                    {
                        NextToken();
                        if (Current.Kind == SyntaxTokenKind.OpenParen)
                        {
                            guard = ParseParenthesizedArgument(implicitCurrentItem: false);
                        }
                    }

                    var caseBlock = ParseRequiredBlock("case");
                    cases.Add(new SwitchCaseSyntax(matchExpression, guard, caseBlock, TextSpan.FromBounds(caseToken.Span.Start, caseBlock.Span.End)));
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
                    Code: "tosh.parser.expected_switch_case",
                    Title: "Switch blocks may only contain 'case' and 'default' entries.",
                    Span: Current.Span,
                    Label: "write 'case <value> { ... }' or 'default { ... }' here"));
                SkipToBlockBoundary();
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
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

        private bool IsPatternExpressionStart()
        {
            if (Current.Kind is SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanEqual
                             or SyntaxTokenKind.LessThan or SyntaxTokenKind.LessThanEqual
                             or SyntaxTokenKind.BangEqual or SyntaxTokenKind.BangTilde)
                return true;

            if (Current.Kind != SyntaxTokenKind.Bareword)
                return false;

            // Single-token bareword operators
            if (Current.Text is "==" or "=~" or "in" or "contains" or "starts-with" or "ends-with")
                return true;

            // "not in" — two-token compound
            if (string.Equals(Current.Text, "not", StringComparison.OrdinalIgnoreCase) &&
                Peek(1).Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Peek(1).Text, "in", StringComparison.OrdinalIgnoreCase))
                return true;

            // "is", "is not", "is in", "is not in"
            if (string.Equals(Current.Text, "is", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private ArgumentSyntax? TryParsePatternExpression()
        {
            // Comparison pattern operators that can appear without a left operand:
            //   is [not] [in] Type/Collection, is [not] in Collection
            //   ==, !=, =~, !~, >, >=, <, <=
            //   in, not in, contains, starts-with, ends-with

            // "is", "is not", "is in", "is not in"
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "is", StringComparison.OrdinalIgnoreCase))
            {
                var opToken = NextToken();
                string op = "is";

                if (Current.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(Current.Text, "not", StringComparison.OrdinalIgnoreCase))
                {
                    NextToken();
                    op = "is-not";

                    // "is not in"
                    if (Current.Kind == SyntaxTokenKind.Bareword &&
                        string.Equals(Current.Text, "in", StringComparison.OrdinalIgnoreCase))
                    {
                        NextToken();
                        op = "is-not-in";
                    }
                }
                else if (Current.Kind == SyntaxTokenKind.Bareword &&
                         string.Equals(Current.Text, "in", StringComparison.OrdinalIgnoreCase))
                {
                    NextToken();
                    op = "is-in";
                }

                var operand = ParseUnaryExpression(opToken.Span.Start, implicitCurrentItem: false)
                              ?? new BarewordArgumentSyntax(string.Empty, opToken.Span);
                return new ComparisonPatternSyntax(op, opToken.Span, operand,
                    TextSpan.FromBounds(opToken.Span.Start, operand.Span.End));
            }

            // "not in"
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "not", StringComparison.OrdinalIgnoreCase) &&
                Peek(1).Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Peek(1).Text, "in", StringComparison.OrdinalIgnoreCase))
            {
                var opToken = NextToken(); // consume "not"
                NextToken();              // consume "in"
                var operand = ParseUnaryExpression(opToken.Span.Start, implicitCurrentItem: false)
                              ?? new BarewordArgumentSyntax(string.Empty, opToken.Span);
                return new ComparisonPatternSyntax("not-in", opToken.Span, operand,
                    TextSpan.FromBounds(opToken.Span.Start, operand.Span.End));
            }

            // Symbolic token-kind operators: >, >=, <, <=, !=, !~
            if (Current.Kind is SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanEqual
                             or SyntaxTokenKind.LessThan or SyntaxTokenKind.LessThanEqual
                             or SyntaxTokenKind.BangEqual or SyntaxTokenKind.BangTilde)
            {
                var opToken = NextToken();
                var op = opToken.Kind switch
                {
                    SyntaxTokenKind.GreaterThan => ">",
                    SyntaxTokenKind.GreaterThanEqual => ">=",
                    SyntaxTokenKind.LessThan => "<",
                    SyntaxTokenKind.LessThanEqual => "<=",
                    SyntaxTokenKind.BangEqual => "!=",
                    SyntaxTokenKind.BangTilde => "!~",
                    _ => opToken.Text,
                };
                var operand = ParseUnaryExpression(opToken.Span.Start, implicitCurrentItem: false)
                              ?? new BarewordArgumentSyntax(string.Empty, opToken.Span);
                return new ComparisonPatternSyntax(op, opToken.Span, operand,
                    TextSpan.FromBounds(opToken.Span.Start, operand.Span.End));
            }

            // Bareword operators: ==, =~, in, contains, starts-with, ends-with
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                Current.Text is "==" or "=~" or "in" or "contains" or "starts-with" or "ends-with")
            {
                var opToken = NextToken();
                var operand = ParseUnaryExpression(opToken.Span.Start, implicitCurrentItem: false)
                              ?? new BarewordArgumentSyntax(string.Empty, opToken.Span);
                return new ComparisonPatternSyntax(opToken.Text, opToken.Span, operand,
                    TextSpan.FromBounds(opToken.Span.Start, operand.Span.End));
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

        private ArgumentSyntax ParseIfExpressionArgument()
        {
            var ifToken = NextToken(); // consume 'if'

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_if_expression_condition",
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
                    Code: "tosh.parser.if_expression_requires_else",
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
                    Code: "tosh.parser.expected_else_block",
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
                // Only require _ prefix before comparison/type-check patterns
                // (is, >, >=, <, <=, =~) to disambiguate from redirections.
                // Plain value arms like "gz" => ... or 42 => ... do not need it.
                if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == "_")
                {
                    // Check if this is `_ =>` (wildcard shorthand) — suggest `default` instead.
                    if (IsFatArrowToken(Peek(1), Peek(2)))
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

            if (!IsFatArrowToken(Current, Peek(1)))
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

        private StatementSyntax ParseYieldStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var yieldToken = NextToken();

            if (IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) ||
                Current.Kind == SyntaxTokenKind.Pipe ||
                HasImplicitStatementBoundaryAfter(yieldToken.Span.End))
            {
                return new YieldStatementSyntax(null, yieldToken.Span);
            }

            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, yieldToken.Span.End);

            return new YieldStatementSyntax(
                value.Stages.Count == 0 ? null : value,
                TextSpan.FromBounds(yieldToken.Span.Start, end));
        }

        private StatementSyntax ParseLoopControlStatement(bool isBreak)
        {
            var keyword = NextToken();
            return isBreak
                ? new BreakStatementSyntax(keyword.Span)
                : new ContinueStatementSyntax(keyword.Span);
        }

        private StatementSyntax ParseScriptInputStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var docComment = DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>());
            var keyword = NextToken();
            var kind = keyword.Text is "flag" or "flags"
                ? ScriptInputDeclarationKind.Flag
                : ScriptInputDeclarationKind.Argument;
            var isList = keyword.Text is "flags" or "args";

            if (isList)
            {
                if (Current.Kind != SyntaxTokenKind.OpenParen)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_script_input_list",
                        Title: "Script input lists require '(...)'.",
                        Span: Current.Span,
                        Label: "write inputs inside parentheses"));

                    return new ScriptInputStatementSyntax(
                        kind,
                        Array.Empty<FunctionParameterSyntax>(),
                        keyword.Span,
                        docComment);
                }

                var parameters = ParseFunctionParameters();
                var end = Peek(-1).Span.End;
                return new ScriptInputStatementSyntax(
                    kind,
                    parameters,
                    TextSpan.FromBounds(keyword.Span.Start, end),
                    docComment);
            }

            var parameter = ParseFunctionParameter();
            // Attach the doc-comment body as the parameter description for single-flag/arg declarations.
            var description = docComment?.Description?.Trim();
            if (!string.IsNullOrEmpty(description))
                parameter = parameter with { Description = description };
            return new ScriptInputStatementSyntax(
                kind,
                [parameter],
                TextSpan.FromBounds(keyword.Span.Start, parameter.Span.End),
                docComment);
        }

        private static bool IsSubcommandModifierKeyword(string text) =>
            text is "eager" or "hidden" or "hollow" or "vital";

        private static bool IsSubcommandKeyword(string text) =>
            text is "subcommand" or "subcmd";

        private bool LooksLikeSubcommandDeclaration()
        {
            var offset = 0;
            while (Peek(offset).Kind == SyntaxTokenKind.Bareword &&
                   IsSubcommandModifierKeyword(Peek(offset).Text))
            {
                offset++;
            }

            return Peek(offset).Kind == SyntaxTokenKind.Bareword &&
                   IsSubcommandKeyword(Peek(offset).Text) &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidCommandName(Peek(offset + 1).Text) &&
                   (Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace ||
                    Peek(offset + 2).Kind == SyntaxTokenKind.OpenParen ||
                    IsFatArrowToken(Peek(offset + 2), Peek(offset + 3)));
        }

        private StatementSyntax ParseSubcommandStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var docComment = DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>());
            var declarationStart = Current.Span.Start;
            var modifiers = SubcommandModifier.None;
            var seenModifiers = new HashSet<string>(StringComparer.Ordinal);

            while (Current.Kind == SyntaxTokenKind.Bareword && IsSubcommandModifierKeyword(Current.Text))
            {
                var modifierToken = NextToken();
                if (!seenModifiers.Add(modifierToken.Text))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.duplicate_subcommand_modifier",
                        Title: $"Subcommand modifier '{modifierToken.Text}' is repeated.",
                        Span: modifierToken.Span,
                        Label: "remove the duplicate modifier"));
                    continue;
                }

                modifiers |= modifierToken.Text switch
                {
                    "eager" => SubcommandModifier.Eager,
                    "hidden" => SubcommandModifier.Hidden,
                    "hollow" => SubcommandModifier.Hollow,
                    "vital" => SubcommandModifier.Vital,
                    _ => SubcommandModifier.None,
                };
            }

            if ((modifiers & SubcommandModifier.Eager) != 0 &&
                (modifiers & SubcommandModifier.Hollow) != 0)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.incompatible_subcommand_modifiers",
                    Title: "'eager' and 'hollow' cannot be combined on a subcommand.",
                    Span: Current.Span,
                    Label: "remove one of these modifiers",
                    Help: "'hollow' forbids a body, but 'eager' requires one to run."));
            }

            var keyword = NextToken();
            if (Current.Kind != SyntaxTokenKind.Bareword || !IsValidCommandName(Current.Text))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_subcommand_name",
                    Title: $"The '{keyword.Text}' keyword requires a subcommand name.",
                    Span: Current.Span,
                    Label: "write a name after this keyword"));

                return new SubcommandStatementSyntax(
                    Name: "_",
                    Modifiers: modifiers,
                    Body: new BlockSyntax(Array.Empty<StatementSyntax>(), keyword.Span),
                    Span: TextSpan.FromBounds(declarationStart, keyword.Span.End),
                    DocComment: docComment);
            }

            var nameToken = NextToken();

            IReadOnlyList<FunctionParameterSyntax> parameters = Array.Empty<FunctionParameterSyntax>();
            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                parameters = ParseFunctionParameters();
            }

            BlockSyntax body;

            if (IsFatArrowToken(Current, Peek(1)))
            {
                var arrowStart = Current.Span.Start;
                ConsumeFatArrow();

                var pipeline = ParsePipeline(
                    untilCloseParen: false,
                    untilCloseBrace: true,
                    untilSemicolon: true,
                    allowExpressionStart: true);
                var pipelineSpan = GetPipelineSpan(pipeline, new TextSpan(arrowStart, 0));
                var arrowBody = new BlockSyntax(
                    [new PipelineStatementSyntax(pipeline, pipelineSpan)],
                    pipelineSpan);

                var synthesizedStatements = new List<StatementSyntax>();
                if (parameters.Count > 0)
                {
                    synthesizedStatements.Add(new ScriptInputStatementSyntax(
                        ScriptInputDeclarationKind.Argument,
                        parameters,
                        TextSpan.FromBounds(parameters[0].Span.Start, parameters[^1].Span.End)));
                }
                synthesizedStatements.AddRange(arrowBody.Statements);

                body = new BlockSyntax(synthesizedStatements, arrowBody.Span);
            }
            else
            {
                if (parameters.Count > 0)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.subcommand_params_require_arrow",
                        Title: "Parameter lists on subcommands require a '=>' body.",
                        Span: parameters[0].Span,
                        Label: "write '=> <pipeline>' after the parameter list, or drop the parens and declare args inside a block body"));
                }
                body = ParseRequiredBlock(keyword.Text);
            }

            if ((modifiers & SubcommandModifier.Hollow) != 0)
            {
                foreach (var statement in body.Statements)
                {
                    if (statement is not SubcommandStatementSyntax)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.hollow_subcommand_must_be_empty",
                            Title: $"Hollow subcommand '{nameToken.Text}' may only contain nested subcommands.",
                            Span: statement.Span,
                            Label: "remove this statement or drop the 'hollow' modifier",
                            Help: "'hollow' declares a namespace-only subcommand whose body is reserved for nested subcommands."));
                    }
                }
            }

            return new SubcommandStatementSyntax(
                Name: nameToken.Text,
                Modifiers: modifiers,
                Body: body,
                Span: TextSpan.FromBounds(declarationStart, body.Span.End),
                DocComment: docComment);
        }

        private StatementSyntax ParseFunctionDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null, bool allowOperatorName = false)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            var funcToken = NextToken();
            var openParenConsumed = false;
            var nameToken = allowOperatorName ? ExpectCommandOrOperatorName(out openParenConsumed) : ExpectCommandName();
            // openParenConsumed is true when the name token was "<(" (LessThanOpenParen), meaning
            // the opening "(" of the parameter list was already consumed as part of that token.
            var opNameOpenParenConsumed = allowOperatorName && openParenConsumed;

            IReadOnlyList<FunctionParameterSyntax> parameters = Array.Empty<FunctionParameterSyntax>();

            if (opNameOpenParenConsumed || Current.Kind == SyntaxTokenKind.OpenParen)
            {
                parameters = ParseFunctionParameters(skipOpenParen: opNameOpenParenConsumed);
            }
            else if (!IsFatArrowToken(Current, Peek(1)))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_function_signature",
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
                            Code: "tosh.parser.expected_priority_value",
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
                body = ParseFunctionArrowBody(nameToken.Text, allowExpressionStart: true);

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
                whenGuard,
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
        }

        private StatementSyntax ParseRuneDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();

            // Parse rune-level modifiers: sealed (default), leaky, fixed, lazy
            var isSealed = true; // sealed by default
            var isFixed = false;

            while (Current.Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Current.Text, "sealed", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "leaky", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "fixed", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "lazy", StringComparison.Ordinal)))
            {
                var modText = Current.Text;
                NextToken();

                if (string.Equals(modText, "sealed", StringComparison.Ordinal))
                    isSealed = true;
                else if (string.Equals(modText, "leaky", StringComparison.Ordinal))
                    isSealed = false;
                else if (string.Equals(modText, "fixed", StringComparison.Ordinal))
                    isFixed = true;
                // "lazy" is the default (eval-time), no-op
            }

            var runeToken = NextToken(); // consume 'rune'
            var nameToken = ExpectCommandName();

            IReadOnlyList<FunctionParameterSyntax> parameters = Array.Empty<FunctionParameterSyntax>();

            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                parameters = ParseFunctionParameters();
            }

            var body = ParseRequiredBlock("rune");

            return new RuneDefinitionStatementSyntax(
                nameToken.Text,
                parameters,
                body,
                isSealed,
                isFixed,
                modifier,
                TextSpan.FromBounds(declarationStart, body.Span.End),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
        }

        private StatementSyntax ParseClassDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();

            // Parse class-level modifiers: sealed, hollow, hermit, strict, partial
            var isSealed = false;
            var isAbstract = false;
            var isHermit = false;
            var isStrict = false;
            var isPartial = false;
            while (Current.Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Current.Text, "sealed", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "hollow", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "hermit", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "strict", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "partial", StringComparison.Ordinal)))
            {
                isSealed |= string.Equals(Current.Text, "sealed", StringComparison.Ordinal);
                isAbstract |= string.Equals(Current.Text, "hollow", StringComparison.Ordinal);
                isHermit |= string.Equals(Current.Text, "hermit", StringComparison.Ordinal);
                isStrict |= string.Equals(Current.Text, "strict", StringComparison.Ordinal);
                isPartial |= string.Equals(Current.Text, "partial", StringComparison.Ordinal);
                NextToken();
            }

            var classToken = NextToken();
            var nameToken = ExpectVariableName();
            var primaryConstructorParameters = Current.Kind == SyntaxTokenKind.OpenParen
                ? ParseFunctionParameters()
                : Array.Empty<FunctionParameterSyntax>();

            // Parse optional 'extends BaseClass' with optional constructor args
            string? baseClassName = null;
            IReadOnlyList<PipelineSyntax>? baseConstructorArgs = null;
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "extends", StringComparison.OrdinalIgnoreCase))
            {
                NextToken(); // consume 'extends'
                baseClassName = ParseTypeName("base class");

                // Parse optional base constructor args: extends Parent($x, $y)
                if (Current.Kind == SyntaxTokenKind.OpenParen)
                {
                    baseConstructorArgs = ParseBaseConstructorArguments();
                }
            }

            // Parse optional 'fulfills Interface1, Interface2, ...'
            List<string>? implementedInterfaces = null;
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                (string.Equals(Current.Text, "fulfills", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Current.Text, "implements", StringComparison.OrdinalIgnoreCase)))
            {
                NextToken(); // consume 'fulfills'/'implements'
                implementedInterfaces = new List<string>();
                implementedInterfaces.Add(ParseTypeName("interface"));

                while (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken(); // consume ','
                    implementedInterfaces.Add(ParseTypeName("interface"));
                }
            }

            // Parse optional 'uses Trait1, Trait2, ...'
            List<string>? usedTraits = null;
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "uses", StringComparison.OrdinalIgnoreCase))
            {
                NextToken(); // consume 'uses'
                usedTraits = new List<string>();
                usedTraits.Add(ParseTypeName("trait"));

                while (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken(); // consume ','
                    usedTraits.Add(ParseTypeName("trait"));
                }
            }

            var body = ParseClassBody(nameToken.Text);

            return new ClassDefinitionStatementSyntax(
                nameToken.Text,
                primaryConstructorParameters,
                body,
                modifier,
                TextSpan.FromBounds(declarationStart, body.Count == 0 ? nameToken.Span.End : body[^1].Span.End),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()),
                BaseClassName: baseClassName,
                BaseConstructorArgs: baseConstructorArgs,
                ImplementedInterfaces: implementedInterfaces,
                UsedTraits: usedTraits,
                IsSealed: isSealed,
                IsAbstract: isAbstract,
                IsHermit: isHermit,
                IsStrict: isStrict,
                IsPartial: isPartial);
        }

        private StatementSyntax ParseInterfaceDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            NextToken(); // consume 'interface'
            var nameToken = ExpectVariableName();

            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_interface_body",
                    Title: "Interface definitions require a body.",
                    Span: Current.Span,
                    Label: $"write '{{ ... }}' after interface '{nameToken.Text}'"));
                return new InterfaceDefinitionStatementSyntax(
                    nameToken.Text,
                    Array.Empty<InterfaceMethodSignatureSyntax>(),
                    modifier,
                    TextSpan.FromBounds(declarationStart, nameToken.Span.End),
                    DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
            }

            NextToken(); // consume '{'
            var methods = new List<InterfaceMethodSignatureSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind is SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Bareword && string.Equals(Current.Text, "func", StringComparison.OrdinalIgnoreCase))
                {
                    var methodStart = Current.Span.Start;
                    NextToken(); // consume 'func'
                    var methodName = ExpectVariableName();
                    var parameters = Current.Kind == SyntaxTokenKind.OpenParen
                        ? ParseFunctionParameters()
                        : Array.Empty<FunctionParameterSyntax>();

                    // Optional return type: ': TypeName'
                    string? returnTypeName = null;
                    if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
                    {
                        NextToken(); // consume ':'
                        var typeToken = ExpectVariableName();
                        returnTypeName = typeToken.Text;
                    }

                    methods.Add(new InterfaceMethodSignatureSyntax(
                        methodName.Text,
                        parameters,
                        returnTypeName,
                        TextSpan.FromBounds(methodStart, parameters.Count > 0 ? parameters[^1].Span.End : methodName.Span.End)));
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_interface_member",
                        Title: "Interface bodies can only contain method signatures (func name(params)).",
                        Span: Current.Span,
                        Label: "expected 'func'"));
                    NextToken();
                }
            }

            var closeBrace = Current;
            if (Current.Kind == SyntaxTokenKind.CloseBrace) NextToken();

            return new InterfaceDefinitionStatementSyntax(
                nameToken.Text,
                methods,
                modifier,
                TextSpan.FromBounds(declarationStart, closeBrace.Span.End),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
        }

        private StatementSyntax ParseUnionDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            NextToken(); // consume 'union'
            var nameToken = ExpectVariableName();

            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_union_body",
                    Title: "Union definitions require a body.",
                    Span: Current.Span,
                    Label: $"write '{{ ... }}' after union '{nameToken.Text}'"));
                return new UnionDefinitionStatementSyntax(
                    nameToken.Text,
                    Array.Empty<UnionVariantSyntax>(),
                    modifier,
                    TextSpan.FromBounds(declarationStart, nameToken.Span.End),
                    DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
            }

            NextToken(); // consume '{'
            var variants = new List<UnionVariantSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind is SyntaxTokenKind.Semicolon or SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                var variantStart = Current.Span.Start;
                var variantName = ExpectVariableName();
                var fields = Current.Kind == SyntaxTokenKind.OpenParen
                    ? ParseFunctionParameters()
                    : Array.Empty<FunctionParameterSyntax>();

                variants.Add(new UnionVariantSyntax(
                    variantName.Text,
                    fields,
                    TextSpan.FromBounds(variantStart, fields.Count > 0 ? fields[^1].Span.End : variantName.Span.End)));
            }

            var closeBrace = Current;
            if (Current.Kind == SyntaxTokenKind.CloseBrace) NextToken();

            return new UnionDefinitionStatementSyntax(
                nameToken.Text,
                variants,
                modifier,
                TextSpan.FromBounds(declarationStart, closeBrace.Span.End),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
        }

        private StatementSyntax ParseModuleDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
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
                TextSpan.FromBounds(declarationStart, body.Span.End),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
        }

        private StatementSyntax ParseEnumDefinitionStatement(
            IReadOnlyList<SyntaxToken>? docTokens,
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
                    Code: "tosh.parser.expected_enum_body",
                    Title: "Enum definitions require a body.",
                    Span: Current.Span,
                    Label: $"write '{{ ... }}' after enum '{enumName}'"));
                return new EnumDefinitionStatementSyntax(enumName, underlyingTypeName, Array.Empty<EnumMemberSyntax>(), modifier, TextSpan.FromBounds(declarationStart, nameToken.Span.End),
                    DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
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
                            Code: "tosh.parser.expected_enum_member_value",
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
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this enum body never closes",
                    Help: "close the enum body with '}' after the last member."));
                return new EnumDefinitionStatementSyntax(enumName, underlyingTypeName, members, modifier, TextSpan.FromBounds(declarationStart, members.Count == 0 ? nameToken.Span.End : members[^1].Span.End),
                    DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
            }

            var closeBrace = NextToken();
            return new EnumDefinitionStatementSyntax(
                enumName,
                underlyingTypeName,
                members,
                modifier,
                TextSpan.FromBounds(declarationStart, closeBrace.Span.End),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
        }

        private StatementSyntax ParseRecordDefinitionStatement(
            IReadOnlyList<SyntaxToken>? docTokens,
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();

            var isSealed = false;
            var isStrict = false;
            var isPartial = false;

            while (Current.Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Current.Text, "sealed", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "strict", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "partial", StringComparison.Ordinal)))
            {
                isSealed |= string.Equals(Current.Text, "sealed", StringComparison.Ordinal);
                isStrict |= string.Equals(Current.Text, "strict", StringComparison.Ordinal);
                isPartial |= string.Equals(Current.Text, "partial", StringComparison.Ordinal);
                NextToken();
            }

            NextToken(); // record
            var nameToken = ExpectVariableName();

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_record_fields",
                    Title: "Record definitions require a field list.",
                    Span: Current.Span,
                    Label: $"write '(...)' after record '{nameToken.Text}'"));
                return new RecordDefinitionStatementSyntax(nameToken.Text, Array.Empty<RecordFieldDefinitionSyntax>(), modifier, isSealed, isStrict, isPartial, TextSpan.FromBounds(declarationStart, nameToken.Span.End),
                    DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
            }

            var fields = ParseRecordDefinitionFields(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            var end = fields.Count == 0 ? nameToken.Span.End : fields[^1].Span.End;
            return new RecordDefinitionStatementSyntax(
                nameToken.Text,
                fields,
                modifier,
                isSealed,
                isStrict,
                isPartial,
                TextSpan.FromBounds(declarationStart, end),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
        }

        private StatementSyntax ParseStructDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();

            var isSealed = false;
            var isFluid = false;
            var isPartial = false;

            while (Current.Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Current.Text, "sealed", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "fluid", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "partial", StringComparison.Ordinal)))
            {
                isSealed |= string.Equals(Current.Text, "sealed", StringComparison.Ordinal);
                isFluid |= string.Equals(Current.Text, "fluid", StringComparison.Ordinal);
                isPartial |= string.Equals(Current.Text, "partial", StringComparison.Ordinal);
                NextToken();
            }

            NextToken(); // consume 'struct'
            var nameToken = ExpectVariableName();

            // Parse fields: struct Name(field1, field2, ...)
            IReadOnlyList<RecordFieldDefinitionSyntax> fields = Array.Empty<RecordFieldDefinitionSyntax>();
            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                fields = ParseRecordDefinitionFields(stopAtCloseParen: false, stopAtCloseBrace: false, stopAtSemicolon: false);
            }

            // Parse optional body: { prop ...; func ...; }
            IReadOnlyList<ClassMemberSyntax> members = Array.Empty<ClassMemberSyntax>();
            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                members = ParseClassBody(nameToken.Text);
            }

            var end = members.Count > 0 ? members[^1].Span.End
                : fields.Count > 0 ? fields[^1].Span.End
                : nameToken.Span.End;

            return new StructDefinitionStatementSyntax(
                nameToken.Text,
                fields,
                members,
                modifier,
                isSealed,
                isFluid,
                isPartial,
                TextSpan.FromBounds(declarationStart, end),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
        }

        private StatementSyntax ParseTraitDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            NextToken(); // consume 'trait'
            var nameToken = ExpectVariableName();

            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_trait_body",
                    Title: "Trait definitions require a body.",
                    Span: Current.Span,
                    Label: $"write '{{ ... }}' after trait '{nameToken.Text}'"));
                return new TraitDefinitionStatementSyntax(
                    nameToken.Text,
                    Array.Empty<TraitMethodSignatureSyntax>(),
                    Array.Empty<TraitPropertySignatureSyntax>(),
                    modifier,
                    TextSpan.FromBounds(declarationStart, nameToken.Span.End),
                    DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
            }

            NextToken(); // consume '{'
            var methods = new List<TraitMethodSignatureSyntax>();
            var properties = new List<TraitPropertySignatureSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind is SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Bareword && string.Equals(Current.Text, "prop", StringComparison.OrdinalIgnoreCase))
                {
                    var propStart = Current.Span.Start;
                    NextToken(); // consume 'prop'
                    var propName = ExpectVariableName();

                    // Optional type: prop name: Type
                    string? typeName = null;
                    if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
                    {
                        NextToken(); // consume ':'
                        var typeToken = ExpectVariableName();
                        typeName = typeToken.Text;
                    }

                    // Optional default value: prop name = expr
                    PipelineSyntax? defaultValue = null;
                    if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == "=")
                    {
                        NextToken(); // consume '='
                        defaultValue = ParsePipeline(untilCloseParen: false, untilCloseBrace: true, untilSemicolon: true, allowExpressionStart: true);
                    }

                    properties.Add(new TraitPropertySignatureSyntax(
                        propName.Text,
                        typeName,
                        defaultValue,
                        TextSpan.FromBounds(propStart, propName.Span.End)));
                }
                else if (Current.Kind == SyntaxTokenKind.Bareword && string.Equals(Current.Text, "func", StringComparison.OrdinalIgnoreCase))
                {
                    var methodStart = Current.Span.Start;
                    NextToken(); // consume 'func'
                    var methodName = ExpectVariableName();
                    var parameters = Current.Kind == SyntaxTokenKind.OpenParen
                        ? ParseFunctionParameters()
                        : Array.Empty<FunctionParameterSyntax>();

                    // Optional return type: func name(params): Type
                    string? returnTypeName = null;
                    if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
                    {
                        NextToken(); // consume ':'
                        var typeToken = ExpectVariableName();
                        returnTypeName = typeToken.Text;
                    }

                    // Optional default body: func name(params) { ... }
                    BlockSyntax? defaultBody = null;
                    if (Current.Kind == SyntaxTokenKind.OpenBrace)
                    {
                        defaultBody = ParseRequiredBlock("trait method default body");
                    }

                    methods.Add(new TraitMethodSignatureSyntax(
                        methodName.Text,
                        parameters,
                        returnTypeName,
                        defaultBody,
                        TextSpan.FromBounds(methodStart, parameters.Count > 0 ? parameters[^1].Span.End : methodName.Span.End)));
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_trait_member",
                        Title: "Trait bodies can contain method signatures (func) and property declarations (prop).",
                        Span: Current.Span,
                        Label: "expected 'func' or 'prop'"));
                    NextToken();
                }
            }

            var closeBrace = Current;
            if (Current.Kind == SyntaxTokenKind.CloseBrace) NextToken();

            return new TraitDefinitionStatementSyntax(
                nameToken.Text,
                methods,
                properties,
                modifier,
                TextSpan.FromBounds(declarationStart, closeBrace.Span.End),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
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

                var arg = ParsePipeline(untilCloseParen: true, untilCloseBrace: false, untilSemicolon: false, allowExpressionStart: true);
                args.Add(arg);
            }

            if (Current.Kind == SyntaxTokenKind.CloseParen)
            {
                NextToken(); // consume ')'
            }

            return args;
        }

        private StatementSyntax ParseEventDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
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
                    TextSpan.FromBounds(declarationStart, nameToken.Span.End),
                    DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
            }

            var closeBraceEnd = Current.Span.End;
            var fields = ParseEventDefinitionFields(out closeBraceEnd);

            return new EventDefinitionStatementSyntax(
                nameToken.Text,
                fields,
                isRequired,
                isLocal,
                modifier,
                TextSpan.FromBounds(declarationStart, closeBraceEnd),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
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

            while (Current.Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Current.Text, "shy", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "static", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "shared", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "public", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "proud", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "hollow", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "fixed", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "vital", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "overrule", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "guarded", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "lazy", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "fading", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "local", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "raw", StringComparison.Ordinal)))
            {
                isShy |= string.Equals(Current.Text, "shy", StringComparison.Ordinal);
                isStatic |= string.Equals(Current.Text, "static", StringComparison.Ordinal) ||
                            string.Equals(Current.Text, "shared", StringComparison.Ordinal);
                isAbstract |= string.Equals(Current.Text, "hollow", StringComparison.Ordinal);
                isFixed |= string.Equals(Current.Text, "fixed", StringComparison.Ordinal);
                isVital |= string.Equals(Current.Text, "vital", StringComparison.Ordinal);
                isOverride |= string.Equals(Current.Text, "overrule", StringComparison.Ordinal);
                isGuarded |= string.Equals(Current.Text, "guarded", StringComparison.Ordinal);
                isLazy |= string.Equals(Current.Text, "lazy", StringComparison.Ordinal);
                isFading |= string.Equals(Current.Text, "fading", StringComparison.Ordinal);
                isLocal |= string.Equals(Current.Text, "local", StringComparison.Ordinal);
                isRaw |= string.Equals(Current.Text, "raw", StringComparison.Ordinal);
                // 'public'/'proud' recognized and consumed but has no effect (it's the default)
                NextToken();
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "prop", StringComparison.Ordinal))
            {
                return ParseClassPropertyMember(isShy, isStatic, isFixed, isVital, isGuarded, isLazy, isFading, isLocal, isAbstract, memberStart, docTokens);
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "func", StringComparison.Ordinal))
            {
                var method = ParseFunctionDefinitionStatement(docTokens, allowOperatorName: true) as FunctionDefinitionStatementSyntax
                             ?? throw new InvalidOperationException("Expected a function definition while parsing a class method.");
                return new ClassMethodMemberSyntax(method, isStatic, isShy, isAbstract, isOverride, isGuarded, isFading, isLocal, isRaw, TextSpan.FromBounds(memberStart, method.Span.End));
            }

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, className, StringComparison.Ordinal) &&
                Peek(1).Kind == SyntaxTokenKind.OpenParen)
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
                        Code: "tosh.parser.expected_property_accessor",
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
                    Code: "tosh.parser.expected_anonymous_function_expression",
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
                Code: "tosh.parser.expected_anonymous_function_body",
                Title: "Anonymous functions require `=>` or a block body.",
                Span: Current.Span,
                Label: "write `=> <expression>` or `{ ... }` after the parameter list"));

            return new AnonymousFunctionArgumentSyntax(
                parameters,
                new BlockSyntax(Array.Empty<StatementSyntax>(), funcToken.Span),
                funcToken.Span);
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
            if (Current.Kind != SyntaxTokenKind.LessThan)
            {
                return Array.Empty<string>();
            }

            var open = NextToken();
            var parameters = new List<string>();

            while (Current.Kind is not SyntaxTokenKind.GreaterThan and not SyntaxTokenKind.EndOfFile)
            {
                var nameToken = ExpectVariableName();

                if (!string.IsNullOrWhiteSpace(nameToken.Text))
                {
                    parameters.Add(nameToken.Text);
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

            return parameters;
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

        /// <summary>
        /// Like <see cref="ExpectCommandName"/> but also accepts overloadable operator symbols as
        /// method names (used when parsing methods inside a class body).
        /// </summary>
        private SyntaxToken ExpectCommandOrOperatorName(out bool openParenConsumed)
        {
            openParenConsumed = false;

            if (Current.Kind == SyntaxTokenKind.Bareword && IsValidCommandName(Current.Text))
                return NextToken();

            if (IsOverloadableOperatorToken(Current, out var operatorName))
            {
                var tok = NextToken();
                return new SyntaxToken(SyntaxTokenKind.Bareword, tok.Span.Start, operatorName, operatorName);
            }

            // "<(" is lexed as a single LessThanOpenParen token; treat as "<" operator with "(" already consumed.
            if (Current.Kind == SyntaxTokenKind.LessThanOpenParen)
            {
                openParenConsumed = true;
                var tok = NextToken();
                return new SyntaxToken(SyntaxTokenKind.Bareword, tok.Span.Start, "<", "<");
            }

            var token = Current;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_command_name",
                Title: "Expected a method name or operator.",
                Span: token.Span,
                Label: "use letters/hyphens for method names, or an operator symbol (+, -, *, ==, >, etc.)"));

            if (Current.Kind != SyntaxTokenKind.EndOfFile)
                NextToken();

            return new SyntaxToken(SyntaxTokenKind.Bareword, token.Span.Start, string.Empty, string.Empty);
        }

        /// <summary>
        /// Returns true when <paramref name="token"/> is an operator symbol that can be used as a
        /// class method name for operator overloading.  Sets <paramref name="operatorName"/> to the
        /// canonical operator string (e.g. ">").
        /// </summary>
        private static bool IsOverloadableOperatorToken(SyntaxToken token, out string operatorName)
        {
            // Bareword operator symbols (+, -, *, /, //, %, **, ==, =~, !~)
            if (token.Kind == SyntaxTokenKind.Bareword && token.Text is
                "+" or "-" or "*" or "/" or "//" or "%" or "**" or "==" or "=~" or "!~")
            {
                operatorName = token.Text;
                return true;
            }

            // Dedicated token kinds
            operatorName = token.Kind switch
            {
                SyntaxTokenKind.GreaterThan => ">",
                SyntaxTokenKind.GreaterThanEqual => ">=",
                SyntaxTokenKind.LessThan => "<",
                SyntaxTokenKind.LessThanEqual => "<=",
                SyntaxTokenKind.BangEqual => "!=",
                SyntaxTokenKind.BangTilde => "!~",
                _ => string.Empty,
            };
            return operatorName.Length > 0;
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

        private SyntaxToken ExpectAssignmentOperatorToken(string title)
        {
            if (IsAssignmentOperatorToken(Current))
            {
                return NextToken();
            }

            var token = Current;
            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.expected_assignment_operator",
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

            // Function-call syntax: command immediately followed by '(' with no space.
            // e.g. test_args(1, 2, 3) — parse the parenthesized list as individual arguments,
            // not as a tuple literal.
            if (Current.Kind == SyntaxTokenKind.OpenParen &&
                Current.Span.Start == nameToken.Span.End)
            {
                var invocationArgs = ParseInvocationArguments();
                arguments = new List<ArgumentSyntax>(invocationArgs.arguments);
            }
            else if (TryGetCurrentItemExpressionArgumentIndex(nameToken.Text, out var expressionArgumentIndex))
            {
                arguments = ParseCurrentItemExpressionCommandArguments(
                    nameToken.Text,
                    expressionArgumentIndex,
                    nameToken.Span.End,
                    stopAtCloseParen,
                    stopAtCloseBrace,
                    stopAtSemicolon);
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
                if (Peek(1).Kind != SyntaxTokenKind.CloseBrace && LooksLikeRecordLiteral())
                {
                    arguments.Add(WrapExpressionInBlockArgument(ParseBraceLiteralArgument(implicitCurrentItem: true)));
                }
                else
                {
                    arguments.Add(ParseMemberProjectionArgument());
                }

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
                    !(expressionArgument is not null && HasImplicitStatementBoundaryAfter(expressionArgument.Span.End)))
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

        private List<ArgumentSyntax> ParseCurrentItemExpressionCommandArguments(
            string commandName,
            int expressionArgumentIndex,
            int commandEnd,
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var arguments = new List<ArgumentSyntax>();
            var lastConsumedEnd = commandEnd;

            ParseCurrentItemExpressionCommandOptions(commandName, arguments, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            if (arguments.Count > 0)
            {
                lastConsumedEnd = arguments[^1].Span.End;
            }

            while (arguments.Count < expressionArgumentIndex &&
                   !IsCurrentItemExpressionCommandBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon))
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

                var argument = ParseArgument(commandName);

                if (argument is not null)
                {
                    arguments.Add(argument);
                    lastConsumedEnd = argument.Span.End;
                }
            }

            if (IsCurrentItemExpressionCommandBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon))
            {
                return arguments;
            }

            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                var blockArgument = IsPredicateExpressionCommand(commandName)
                    ? ParsePredicateBlockArgument()
                    : ParseBlockArgument();
                arguments.Add(blockArgument);

                // `assert <block> <message>` accepts an optional trailing message argument.
                if (string.Equals(commandName, "assert", StringComparison.OrdinalIgnoreCase) &&
                    !IsCurrentItemExpressionCommandBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                    !HasImplicitStatementBoundaryAfter(blockArgument.Span.End))
                {
                    var messageArgument = ParseArgument(commandName);
                    if (messageArgument is not null)
                    {
                        arguments.Add(messageArgument);
                    }
                }

                if (!IsCurrentItemExpressionCommandBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                    !HasImplicitStatementBoundaryAfter(arguments[^1].Span.End))
                {
                    AddUnexpectedCurrentItemExpressionTokensDiagnostic(commandName, Current.Span);
                    SkipToStageBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
                }

                return arguments;
            }

            var expressionArgument = ParseCurrentItemExpressionArgument();

            if (expressionArgument is not null)
            {
                arguments.Add(expressionArgument);
            }

            // `assert <expr> <message>` accepts an optional trailing message argument.
            if (string.Equals(commandName, "assert", StringComparison.OrdinalIgnoreCase) &&
                expressionArgument is not null &&
                !IsCurrentItemExpressionCommandBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                !HasImplicitStatementBoundaryAfter(expressionArgument.Span.End))
            {
                var messageArgument = ParseArgument(commandName);
                if (messageArgument is not null)
                {
                    arguments.Add(messageArgument);
                }
            }

            if (!IsCurrentItemExpressionCommandBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                !(arguments.Count > 0 && HasImplicitStatementBoundaryAfter(arguments[^1].Span.End)))
            {
                AddUnexpectedCurrentItemExpressionTokensDiagnostic(commandName, Current.Span);
                SkipToStageBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            return arguments;
        }

        private void AddUnexpectedCurrentItemExpressionTokensDiagnostic(string commandName, TextSpan span)
        {
            if (string.Equals(commandName, "assert", StringComparison.OrdinalIgnoreCase))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.assert_does_not_accept_message",
                    Title: "Assert no longer accepts a trailing custom message.",
                    Span: span,
                    Label: "this extra argument is not part of the assertion",
                    Help: "rely on the predicate text and diagnostic info instead."));
                return;
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh.parser.unexpected_current_item_expression_tokens",
                Title: "This current-item expression has extra tokens after it.",
                Span: span,
                Label: "current-item command expressions must be a single expression"));
        }

        private void ParseCurrentItemExpressionCommandOptions(
            string commandName,
            List<ArgumentSyntax> arguments,
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            if (!CommandAllowsOptionsBeforeCurrentItemExpression(commandName))
            {
                return;
            }

            while (!IsCurrentItemExpressionCommandBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                   Current.Kind == SyntaxTokenKind.Bareword &&
                   Current.Text.StartsWith("-", StringComparison.Ordinal))
            {
                var option = ParseArgument(commandName: commandName, implicitCurrentItem: false);
                if (option is not null)
                {
                    arguments.Add(option);
                }

                if (CommandOptionConsumesFollowingValue(commandName, option) &&
                    !IsCurrentItemExpressionCommandBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon))
                {
                    var value = ParseArgument(commandName: commandName, implicitCurrentItem: false);
                    if (value is not null)
                    {
                        arguments.Add(value);
                    }
                }
            }
        }

        private bool IsCurrentItemExpressionCommandBoundary(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            return IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) ||
                   Current.Kind == SyntaxTokenKind.Pipe ||
                   (Current.Kind == SyntaxTokenKind.Ampersand && !LooksLikeFunctionReferenceArgument()) ||
                   LooksLikeRedirectionOperator();
        }

        private bool LooksLikeFunctionReferenceArgument()
        {
            return Current.Kind == SyntaxTokenKind.Ampersand &&
                   Peek(1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidCommandName(Peek(1).Text) &&
                   Current.Span.End == Peek(1).Span.Start;
        }

        private static bool CommandAllowsOptionsBeforeCurrentItemExpression(string commandName)
        {
            return string.Equals(commandName, "sort", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "sort-by", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "parallel", StringComparison.OrdinalIgnoreCase);
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

        private static bool IsPredicateExpressionCommand(string commandName)
        {
            return string.Equals(commandName, "assert", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "where", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "filter", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "take-while", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "skip-while", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "take-until", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "skip-until", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "partition", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "find-index", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "any", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "all", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "none", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "group-while", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetCurrentItemExpressionArgumentIndex(string commandName, out int argumentIndex)
        {
            if (IsPredicateExpressionCommand(commandName) ||
                string.Equals(commandName, "map", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "each", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "foreach", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "flat-map", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "group-by", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "distinct", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "dedup", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "frequencies", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "sort", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "sort-by", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "parallel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "sum", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "average", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "avg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "min", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "max", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "median", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "stdev", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "stddev", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "variance", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "describe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "repeatedly", StringComparison.OrdinalIgnoreCase))
            {
                argumentIndex = 0;
                return true;
            }

            if (string.Equals(commandName, "reduce", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "scan", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "iterate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "converge", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "unfold", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "percentile", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "zip", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "window", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "cartesian-product", StringComparison.OrdinalIgnoreCase))
            {
                argumentIndex = 1;
                return true;
            }

            argumentIndex = -1;
            return false;
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
                        Code: "tosh.parser.accidental_double_dot",
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

            if (!CanStartPrimaryArgument())
            {
                // Open-ended range: start.. (infinite)
                var span = new TextSpan(start.Span.Start, Current.Span.Start - start.Span.Start);
                return new RangeArgumentSyntax(start, Step: null, End: null, span);
            }

            var second = ParsePrimaryArgument(implicitCurrentItem: implicitCurrentItem);

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

                var third = ParsePrimaryArgument(implicitCurrentItem: implicitCurrentItem);

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

            return Current.Kind switch
            {
                SyntaxTokenKind.Number => true,
                SyntaxTokenKind.String => true,
                SyntaxTokenKind.InterpolatedString => true,
                SyntaxTokenKind.Boolean => true,
                SyntaxTokenKind.Null => true,
                SyntaxTokenKind.UnitLiteral => true,
                SyntaxTokenKind.OpenParen => true,
                SyntaxTokenKind.OpenBracket => true,
                SyntaxTokenKind.OpenBrace => true,
                SyntaxTokenKind.DollarOpenParen => true,
                SyntaxTokenKind.LessThanOpenParen => true,
                SyntaxTokenKind.Ampersand => true,
                SyntaxTokenKind.Bang => true,
                _ => false,
            };
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

                    if (commandName is not null && (LooksLikeSetLiteral() || LooksLikeDictLiteral()))
                    {
                        return ParsePostfixChain(ParseBraceLiteralArgument(implicitCurrentItem), implicitCurrentItem);
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
                        Code: "tosh.parser.unexpected_token",
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
                Code: "tosh.parser.nameof_missing_close_paren",
                Title: "Expected ')' after nameof identifier.",
                Span: Current.Span,
                Label: "expected ')'"));

            return new NameOfArgumentSyntax(identifier, isVariableReference, TextSpan.FromBounds(start, identifierToken.Span.End));
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
                var token = NextToken();
                var text = token.Text;
                var separatorIndex = text.IndexOf('.');

                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.variable_references_require_dollar",
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

            if (expression is not MemberAccessArgumentSyntax)
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

        private PipelineSyntax ParseParenthesizedPipeline(string owner)
        {
            // Accept both parenthesized `(source)` and bare `source` (stops before `{`).
            if (Current.Kind == SyntaxTokenKind.OpenParen)
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
                        Code: "tosh.parser.missing_block_separator",
                        Title: "Block statements must be separated by a newline or ';'.",
                        Span: Current.Span,
                        Label: "insert a newline or ';' between block statements"));
                    SkipToBlockBoundary();
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this block never closes",
                    Help: "close the block with '}' after the last statement."));
                return new BlockSyntax(statements, openBrace.Span);
            }

            var closeBrace = NextToken();
            return new BlockSyntax(statements, TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
        }

        // ── Comprehension clause parsing ──
        // Parses: for $x in <source> [where <condition>] [let $y = <expr>] [for ...]
        private ComprehensionClauseSyntax ParseComprehensionClause()
        {
            var forToken = Current;

            if (!(forToken.Kind == SyntaxTokenKind.Bareword &&
                  string.Equals(forToken.Text, "for", StringComparison.OrdinalIgnoreCase)))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_comprehension_for",
                    Title: "Comprehensions require a 'for' clause after '<|'.",
                    Span: forToken.Span,
                    Label: "expected 'for $variable in source'"));
                return new ComprehensionClauseSyntax("_", new BarewordArgumentSyntax("_", forToken.Span), Array.Empty<ComprehensionModifierSyntax>(), null, forToken.Span);
            }

            NextToken(); // consume 'for'
            var nameToken = ExpectVariableName();

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

            // Parse any number of `where` / `let` clauses in any order, preserving declared order
            // so each clause observes bindings from earlier lets.
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

            // Parse optional nested 'for' clause
            ComprehensionClauseSyntax? innerClause = null;

            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "for", StringComparison.OrdinalIgnoreCase))
            {
                innerClause = ParseComprehensionClause();
            }

            var endPos = innerClause?.Span.End
                         ?? (modifiers.Count > 0 ? modifiers[^1].Span.End : source.Span.End);

            return new ComprehensionClauseSyntax(
                nameToken.Text,
                source,
                modifiers,
                innerClause,
                TextSpan.FromBounds(forToken.Span.Start, endPos));
        }

        private bool IsComprehensionOperator()
        {
            return Current.Kind == SyntaxTokenKind.LessThanPipe;
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
                        depth++;
                        break;

                    case SyntaxTokenKind.CloseParen:
                    case SyntaxTokenKind.CloseBrace:
                    case SyntaxTokenKind.CloseBracket:
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

            // Expect closing ':' before '}'
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, ":", StringComparison.Ordinal))
            {
                NextToken();
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing ':}' is required after set comprehension.",
                    Span: openBrace.Span,
                    Label: "this set comprehension never closes"));
                return new SetComprehensionArgumentSyntax(body, clause, openBrace.Span);
            }

            var closeBrace = NextToken();
            return new SetComprehensionArgumentSyntax(
                body,
                clause,
                TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
        }

        private ArgumentSyntax ParseDictComprehension(SyntaxToken openBrace, bool implicitCurrentItem)
        {
            var key = ParseArgument(implicitCurrentItem: implicitCurrentItem)
                      ?? new BarewordArgumentSyntax("_", Current.Span);

            if (!IsFatArrowToken(Current, Peek(1)))
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

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing '}' is required after dict comprehension.",
                    Span: openBrace.Span,
                    Label: "this dict comprehension never closes"));
                return new DictComprehensionArgumentSyntax(key, value, clause, openBrace.Span);
            }

            var closeBrace = NextToken();
            return new DictComprehensionArgumentSyntax(
                key,
                value,
                clause,
                TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
        }

        private bool HasTopLevelOperatorBeforeComprehension()
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
                    case SyntaxTokenKind.CloseBrace:
                    case SyntaxTokenKind.CloseBracket:
                        if (depth > 0) depth--;
                        else return false;
                        break;

                    case SyntaxTokenKind.LessThanPipe:
                        if (depth == 0) return false;
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
                             IsExponentiationOperatorToken(token)))
                        {
                            return true;
                        }
                        break;
                }
            }

            return false;
        }

        private bool HasTopLevelOperatorBeforeComprehensionKeywordOrClose()
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
                    case SyntaxTokenKind.CloseBrace:
                    case SyntaxTokenKind.CloseBracket:
                        if (depth > 0) depth--;
                        else return false;
                        break;

                    case SyntaxTokenKind.LessThanPipe:
                        if (depth == 0) return false;
                        break;

                    case SyntaxTokenKind.Bareword when depth == 0:
                        if (token.Text is "for" or "let" or "where")
                            return false;
                        if (IsTernaryQuestionToken(token) ||
                            IsNullCoalescingOperatorToken(token) ||
                            IsLogicalOrOperatorToken(token) ||
                            IsLogicalAndOperatorToken(token) ||
                            IsComparisonOperatorToken(token) ||
                            IsAdditiveOperatorToken(token) ||
                            IsMultiplicativeOperatorToken(token) ||
                            IsExponentiationOperatorToken(token))
                        {
                            return true;
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
                             IsExponentiationOperatorToken(token)))
                        {
                            return true;
                        }
                        break;
                }
            }

            return false;
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

        private ArgumentSyntax ParseBraceLiteralArgument(bool implicitCurrentItem = false)
        {
            if (LooksLikeSetLiteral())
            {
                return ParseSetLiteralArgument(implicitCurrentItem);
            }

            if (LooksLikeDictLiteral())
            {
                return ParseDictLiteralArgument(implicitCurrentItem);
            }

            return LooksLikeRecordLiteral()
                ? ParseRecordLiteralArgument(implicitCurrentItem)
                : ParseBraceCollectionLiteralArgument(implicitCurrentItem);
        }

        private bool LooksLikeSetLiteral()
        {
            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                return false;
            }

            var next = Peek(1);

            if (next.Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            // {::} — empty set
            if (string.Equals(next.Text, "::", StringComparison.Ordinal) && Peek(2).Kind == SyntaxTokenKind.CloseBrace)
            {
                return true;
            }

            // {: ... :} — set with items
            return string.Equals(next.Text, ":", StringComparison.Ordinal);
        }

        private ArgumentSyntax ParseSetLiteralArgument(bool implicitCurrentItem)
        {
            var openBrace = NextToken();

            // {::} — empty set
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "::", StringComparison.Ordinal) &&
                Peek(1).Kind == SyntaxTokenKind.CloseBrace)
            {
                NextToken(); // consume ::
                var emptyCloseBrace = NextToken(); // consume }
                return new SetLiteralArgumentSyntax(
                    Array.Empty<ArgumentSyntax>(),
                    TextSpan.FromBounds(openBrace.Span.Start, emptyCloseBrace.Span.End));
            }

            // Consume opening ':'
            NextToken();

            // Check for set comprehension: {: body <| for $x in source ... :}
            if (HasTopLevelComprehensionBeforeClose(SyntaxTokenKind.CloseBrace))
            {
                return ParseSetComprehension(openBrace);
            }

            var items = new List<ArgumentSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                // Check for closing ':' delimiter
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

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile &&
                    !(Current.Kind == SyntaxTokenKind.Bareword && string.Equals(Current.Text, ":", StringComparison.Ordinal)))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_set_separator",
                        Title: "Set items must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between set items"));
                }
            }

            // Expect closing ':' before '}'
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, ":", StringComparison.Ordinal))
            {
                NextToken(); // consume closing ':'
            }
            else
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_set_closing_colon",
                    Title: "A closing ':' is required before '}'.",
                    Span: Current.Span,
                    Label: "set literals use '{: ... :}' syntax"));
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this set literal never closes",
                    Help: "close the set literal with ':}' after the last item."));
                return new SetLiteralArgumentSyntax(items, openBrace.Span);
            }

            var closeBrace = NextToken();
            return new SetLiteralArgumentSyntax(
                items,
                TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
        }

        private bool LooksLikeDictLiteral()
        {
            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                return false;
            }

            var first = Peek(1);

            // Empty braces are a record, not a dict.
            if (first.Kind == SyntaxTokenKind.CloseBrace)
            {
                return false;
            }

            // { <expr> => ... }  —  the key can be bareword, string, number, or variable.
            if (first.Kind is SyntaxTokenKind.Bareword or SyntaxTokenKind.String or SyntaxTokenKind.Number)
            {
                return IsFatArrowToken(Peek(2), Peek(3));
            }

            return false;
        }

        private ArgumentSyntax ParseDictLiteralArgument(bool implicitCurrentItem = false)
        {
            var openBrace = NextToken();

            // Check for dict comprehension: { key => value <| for $x in source ... }
            if (HasTopLevelComprehensionBeforeClose(SyntaxTokenKind.CloseBrace))
            {
                return ParseDictComprehension(openBrace, implicitCurrentItem);
            }

            var entries = new List<DictEntrySyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
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

                if (!IsFatArrowToken(Current, Peek(1)))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_fat_arrow",
                        Title: "Dict entries require '=>' between key and value.",
                        Span: Current.Span,
                        Label: "write '=>' after the key expression"));
                    break;
                }

                ConsumeFatArrow();

                var value = ParseArgument(implicitCurrentItem: implicitCurrentItem);

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

                if (value is not null && HasImplicitStatementBoundaryAfter(value.Span.End))
                {
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_dict_entry_separator",
                        Title: "Dict entries must be separated by ',' or a newline.",
                        Span: Current.Span,
                        Label: "insert ',' or a newline between dict entries"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_dict_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: "this dict literal never closes",
                    Help: "close the dict literal with '}' after the last entry."));
                return new DictLiteralArgumentSyntax(entries, openBrace.Span);
            }

            var closeBrace = NextToken();
            return new DictLiteralArgumentSyntax(entries, TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
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
                        Code: "tosh.parser.unexpected_list_separator",
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
                        Code: "tosh.parser.missing_list_separator",
                        Title: "Collection items must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between collection items"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
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
                            Code: "tosh.parser.expected_closing_paren",
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
                        Code: "tosh.parser.expected_record_field_name",
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
                        Code: "tosh.parser.missing_record_field_separator",
                        Title: "Record fields must be separated by ',' or a newline.",
                        Span: Current.Span,
                        Label: "insert ',' or a newline between record fields"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_record_closing_brace",
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
                    Code: "tosh.parser.expected_type_name",
                    Title: "Object construction requires a CLR type name.",
                    Span: Current.Span,
                    Label: "write a type after 'new', like 'new string(\"hello\")'"));
                return new NewObjectArgumentSyntax(string.Empty, Array.Empty<ArgumentSyntax>(), newToken.Span);
            }

            var typeToken = NextToken();
            var typeName = ParseTypeNameSuffix(typeToken.Text) ?? string.Empty;

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_constructor_parenthesis",
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
                        Code: "tosh.parser.unexpected_constructor_separator",
                        Title: "A constructor argument is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add an argument here"));
                    NextToken();
                    continue;
                }

                var argument = HasTopLevelOperatorBeforeCommaOrCloseParen()
                    ? ParseOperatorExpression(Current.Span.Start, implicitCurrentItem)
                    : ParseArgument(implicitCurrentItem: implicitCurrentItem);

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
            if (allowMethodCall && Current.Kind == SyntaxTokenKind.OpenParen)
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
                        Code: "tosh.parser.unexpected_argument_separator",
                        Title: "An argument is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add an argument here"));
                    NextToken();
                    continue;
                }

                // Named argument: identifier = value
                if (Current.Kind == SyntaxTokenKind.Bareword &&
                    IsValidIdentifier(Current.Text) &&
                    !Current.Text.StartsWith("$", StringComparison.Ordinal) &&
                    Peek(1).Kind == SyntaxTokenKind.Bareword && Peek(1).Text == "=")
                {
                    var nameToken = NextToken();
                    NextToken(); // consume '='
                    var value = HasTopLevelOperatorBeforeCommaOrCloseParen()
                        ? ParseOperatorExpression(Current.Span.Start, implicitCurrentItem)
                        : ParseArgument(implicitCurrentItem: implicitCurrentItem);

                    if (value is not null)
                    {
                        arguments.Add(new NamedArgumentSyntax(nameToken.Text, value,
                            TextSpan.FromBounds(nameToken.Span.Start, value.Span.End)));
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
                !HasTopLevelPipeBeforeCloseParen() &&
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

        private bool LooksLikeParenthesizedCommandSubexpression()
        {
            if (Current.Kind != SyntaxTokenKind.Bareword ||
                !IsValidCommandName(Current.Text) ||
                LooksLikeAnonymousFunctionExpression() ||
                LooksLikeMatchExpression() ||
                LooksLikeIfExpression() ||
                LooksLikeNameOfExpression() ||
                LooksLikeNewObjectExpression() ||
                LooksLikeStaticMethodCallExpression() ||
                LooksLikeStaticMemberAccessExpression() ||
                LooksLikeIntrinsicLiteralExpression())
            {
                return false;
            }

            var next = Peek(1);

            if (next.Kind == SyntaxTokenKind.CloseParen)
            {
                return Current.Text.Contains('-', StringComparison.Ordinal);
            }

            if (HasLineBreakBetween(Current.Span.End, next.Span.Start))
            {
                return false;
            }

            if (next.Kind == SyntaxTokenKind.OpenParen && next.Span.Start == Current.Span.End)
            {
                return false;
            }

            return CanStartCommandSubexpressionArgument(next);
        }

        private static bool CanStartCommandSubexpressionArgument(SyntaxToken token)
        {
            return token.Kind is SyntaxTokenKind.Bareword
                or SyntaxTokenKind.String
                or SyntaxTokenKind.InterpolatedString
                or SyntaxTokenKind.Number
                or SyntaxTokenKind.Boolean
                or SyntaxTokenKind.Null
                or SyntaxTokenKind.UnitLiteral
                or SyntaxTokenKind.OpenParen
                or SyntaxTokenKind.OpenBracket
                or SyntaxTokenKind.OpenBrace
                or SyntaxTokenKind.DollarOpenParen
                or SyntaxTokenKind.LessThanOpenParen
                or SyntaxTokenKind.Ampersand
                or SyntaxTokenKind.Bang;
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
                if (Current.Kind == SyntaxTokenKind.Bareword &&
                    IsValidIdentifier(Current.Text) &&
                    !Current.Text.StartsWith("$", StringComparison.Ordinal) &&
                    Peek(1).Kind == SyntaxTokenKind.Bareword && Peek(1).Text == "=")
                {
                    var nameToken = NextToken();
                    NextToken(); // consume '='
                    var value = HasTopLevelOperatorBeforeCommaOrCloseParen()
                        ? ParseOperatorExpression(Current.Span.Start, implicitCurrentItem)
                        : ParseArgument(implicitCurrentItem: implicitCurrentItem);

                    if (value is not null)
                    {
                        items.Add(new NamedArgumentSyntax(nameToken.Text, value,
                            TextSpan.FromBounds(nameToken.Span.Start, value.Span.End)));
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
                    Code: "tosh.parser.missing_closing_parenthesis",
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
                    Code: "tosh.parser.missing_closing_parenthesis",
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
                Code: "tosh.parser.missing_closing_parenthesis",
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
            var whenTrue = ParseTernaryBranch(questionToken.Span.End, implicitCurrentItem);

            SyntaxToken colonToken;
            if (IsTernaryColonToken(Current))
            {
                colonToken = NextToken();
            }
            else
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_ternary_colon",
                    Title: "A ternary expression requires ':'.",
                    Span: questionToken.Span,
                    Label: "this ternary expression is missing its ':' branch separator",
                    Help: "write `condition ? whenTrue : whenFalse` here."));
                colonToken = questionToken;
            }

            var whenFalse = ParseTernaryBranch(colonToken.Span.End, implicitCurrentItem);
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
                        Code: "tosh.parser.assignment_in_predicate",
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
            var left = ParseExponentiationExpression(startPosition, implicitCurrentItem);

            while (IsMultiplicativeOperatorToken(Current))
            {
                var operatorToken = NextToken();
                var right = ParseExponentiationExpression(startPosition, implicitCurrentItem);
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

        private ArgumentSyntax? ParseExponentiationExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseUnaryExpression(startPosition, implicitCurrentItem);

            // ** is right-associative: 2 ** 3 ** 2 == 2 ** (3 ** 2) == 512
            if (IsExponentiationOperatorToken(Current))
            {
                var operatorToken = NextToken();
                var right = ParseExponentiationExpression(startPosition, implicitCurrentItem);
                var end = right?.Span.End ?? operatorToken.Span.End;

                left = new OperatorArgumentSyntax(
                    left ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    "**",
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

                var operatorText = string.Equals(operatorToken.Text, "not", StringComparison.OrdinalIgnoreCase)
                    ? "not"
                    : operatorToken.Text;

                return new UnaryOperatorArgumentSyntax(
                    operatorText,
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
                    Code: "tosh.parser.expected_operand",
                    Title: "Expected an operand in this expression.",
                    Span: Current.Span,
                    Label: "operators need a value on both sides"));
                return null;
            }

            return ParseArgument(implicitCurrentItem: implicitCurrentItem);
        }

        private ArgumentSyntax? ParseCurrentItemExpressionArgument()
        {
            var expression = ParseWherePredicateExpression();

            if (expression is null)
            {
                return null;
            }

            return WrapExpressionInBlockArgument(expression);
        }

        private static BlockArgumentSyntax WrapExpressionInBlockArgument(ArgumentSyntax expression)
        {
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

                if (expression is not null && HasImplicitStatementBoundaryAfter(expression.Span.End))
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

        private PipelineSyntax ParsePipeline(
            bool untilCloseParen,
            bool untilCloseBrace,
            bool untilSemicolon,
            bool allowExpressionStart,
            bool untilOpenBrace = false)
        {
            var stages = new List<PipelineStageSyntax>();
            List<RedirectionSyntax>? redirections = null;
            InputRedirectionSyntax? inputRedirection = null;
            var isBackground = false;

            while (!IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon, untilOpenBrace))
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

                if (Current.Kind == SyntaxTokenKind.Pipe &&
                    !(Peek(1).Kind == SyntaxTokenKind.GreaterThan && Current.Span.End == Peek(1).Span.Start))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_pipeline_separator",
                        Title: "Unexpected pipeline separator.",
                        Span: Current.Span,
                        Label: "remove this '|' or put a stage before it"));
                    NextToken();
                    continue;
                }

                // |> pipe-forward at the top of the loop (for chained |> operators)
                if (stages.Count > 0 && Current.Kind == SyntaxTokenKind.Pipe &&
                    Peek(1).Kind == SyntaxTokenKind.GreaterThan &&
                    Current.Span.End == Peek(1).Span.Start)
                {
                    var pipeToken = NextToken();
                    NextToken(); // consume >

                    if (IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon))
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.missing_command_after_pipe_forward",
                            Title: "A command is required after '|>'.",
                            Span: pipeToken.Span,
                            Label: "a pipeline cannot end here",
                            Help: "add a command after '|>'."));
                    }
                    else
                    {
                        var nextStage = ParsePipelineStage(
                            allowExpressionStage: false,
                            stopAtCloseParen: untilCloseParen,
                            stopAtCloseBrace: untilCloseBrace,
                            stopAtSemicolon: untilSemicolon);

                        if (nextStage is CommandSyntax cmd)
                        {
                            stages.Add(new PipeForwardStageSyntax(cmd, cmd.Span));
                        }
                        else if (nextStage is not null)
                        {
                            stages.Add(nextStage);
                        }
                    }

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
                    NextToken();
                    isBackground = true;
                    break;
                }

                // |> pipe-forward operator — passes previous value as first argument
                if (Current.Kind == SyntaxTokenKind.Pipe && Peek(1).Kind == SyntaxTokenKind.GreaterThan &&
                    Current.Span.End == Peek(1).Span.Start)
                {
                    var pipeToken = NextToken();
                    NextToken(); // consume >

                    if (IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon))
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.missing_command_after_pipe_forward",
                            Title: "A command is required after '|>'.",
                            Span: pipeToken.Span,
                            Label: "a pipeline cannot end here",
                            Help: "add a command after '|>'."));
                    }
                    else
                    {
                        var nextStage = ParsePipelineStage(
                            allowExpressionStage: false,
                            stopAtCloseParen: untilCloseParen,
                            stopAtCloseBrace: untilCloseBrace,
                            stopAtSemicolon: untilSemicolon);

                        if (nextStage is CommandSyntax cmd)
                        {
                            stages.Add(new PipeForwardStageSyntax(cmd, cmd.Span));
                        }
                        else if (nextStage is not null)
                        {
                            stages.Add(nextStage);
                        }
                    }

                    continue;
                }

                if (Current.Kind == SyntaxTokenKind.Pipe)
                {
                    var pipe = NextToken();

                    if (IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon))
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.missing_command_after_pipe",
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

                if (IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon, untilOpenBrace))
                {
                    break;
                }

                if (stage is ExpressionPipelineStageSyntax)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_pipeline_separator",
                        Title: "Expression pipeline stages must be separated by '|'.",
                        Span: Current.Span,
                        Label: "insert '|' before the next command"));
                    SkipToStageBoundary(untilCloseParen, untilCloseBrace, untilSemicolon);
                    continue;
                }
            }

            return new PipelineSyntax(stages, redirections, inputRedirection, isBackground);
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
                SyntaxTokenKind.DocComment => true,
                _ => false,
            };
        }

        private bool HasTopLevelOperatorBeforeCloseParen() => HasTopLevelOperatorBeforeCloseParen(_position);

        private bool HasTopLevelCommaBeforeCloseParen()
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

        private bool HasTopLevelOperatorBeforeCommaOrCloseParen()
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

                    case SyntaxTokenKind.Comma:
                        if (depth == 0)
                        {
                            return false;
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
                             IsExponentiationOperatorToken(token) ||
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
                             IsExponentiationOperatorToken(token) ||
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
            return LooksLikeVariableDeclarationCore("var") || LooksLikeVariableDeclarationCore("const");
        }

        private bool LooksLikeVariableDeclarationCore(string keyword)
        {
            var offset = GetDeclarationModifierOffset();

            if (!MatchesKeywordAtOffset(offset, keyword))
            {
                return false;
            }

            // Destructuring: var { ... } = ... or var [ ... ] = ...
            var afterVar = Peek(offset + 1);
            if (afterVar.Kind is SyntaxTokenKind.OpenBrace or SyntaxTokenKind.OpenBracket)
            {
                return true;
            }

            if (afterVar.Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            // Extract just the name part — handles plain names as well as name:type and name: forms.
            ParseTypedIdentifierToken(afterVar.Text, out var varName, out var inlineType, out var expectsFollowingType);
            if (!IsValidIdentifier(varName))
            {
                return false;
            }

            // Plain untyped: var name =
            if (inlineType == null && !expectsFollowingType)
            {
                return IsVariableDeclarationTailTerminator(offset + 1, offset + 2);
            }

            // Inline type: var name:type [where <predicate>] [= value]
            if (inlineType != null)
            {
                return MatchesKeywordAtOffset(offset + 2, "where") ||
                       IsVariableDeclarationTailTerminator(offset + 1, offset + 2);
            }

            // Trailing colon: var name: Type [where <predicate>] [= value]
            if (!TryGetTypeNameEndOffset(offset + 2, out var typeEndOffset))
            {
                return false;
            }

            return MatchesKeywordAtOffset(typeEndOffset + 1, "where") ||
                   IsVariableDeclarationTailTerminator(typeEndOffset, typeEndOffset + 1);
        }

        private bool IsVariableDeclarationTailTerminator(int previousOffset, int currentOffset)
        {
            var current = Peek(currentOffset);
            return IsEqualsToken(current) ||
                   current.Kind is SyntaxTokenKind.EndOfFile or SyntaxTokenKind.Semicolon or SyntaxTokenKind.CloseBrace or SyntaxTokenKind.CloseParen ||
                   HasLineBreakBetween(Peek(previousOffset).Span.End, current.Span.Start);
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

        private bool LooksLikeScriptInputDeclaration()
        {
            return (Current.Kind == SyntaxTokenKind.Bareword &&
                    Current.Text is "flags" or "args" &&
                    Peek(1).Kind == SyntaxTokenKind.OpenParen) ||
                   (Current.Kind == SyntaxTokenKind.Bareword &&
                    Current.Text is "flag" or "arg" &&
                    Peek(1).Kind == SyntaxTokenKind.Bareword);
        }

        private bool LooksLikeRuneDefinition()
        {
            var offset = GetDeclarationModifierOffset();

            // Skip optional rune-level modifiers: sealed, leaky, fixed, lazy
            while (Peek(offset).Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Peek(offset).Text, "sealed", StringComparison.Ordinal) ||
                    string.Equals(Peek(offset).Text, "leaky", StringComparison.Ordinal) ||
                    string.Equals(Peek(offset).Text, "fixed", StringComparison.Ordinal) ||
                    string.Equals(Peek(offset).Text, "lazy", StringComparison.Ordinal)))
            {
                offset++;
            }

            return MatchesKeywordAtOffset(offset, "rune") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidCommandName(Peek(offset + 1).Text) &&
                   (Peek(offset + 2).Kind == SyntaxTokenKind.OpenParen ||
                    Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace);
        }

        private bool LooksLikeClassDefinition()
        {
            var offset = GetDeclarationModifierOffset();

            // Skip optional class-level modifiers: sealed, hollow, hermit, strict, partial
            while (Peek(offset).Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Peek(offset).Text, "sealed", StringComparison.Ordinal) ||
                    string.Equals(Peek(offset).Text, "hollow", StringComparison.Ordinal) ||
                    string.Equals(Peek(offset).Text, "hermit", StringComparison.Ordinal) ||
                    string.Equals(Peek(offset).Text, "strict", StringComparison.Ordinal) ||
                    string.Equals(Peek(offset).Text, "partial", StringComparison.Ordinal)))
            {
                offset++;
            }

            return MatchesKeywordAtOffset(offset, "class") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   (Peek(offset + 2).Kind == SyntaxTokenKind.OpenParen ||
                    Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace ||
                    (Peek(offset + 2).Kind == SyntaxTokenKind.Bareword &&
                     (string.Equals(Peek(offset + 2).Text, "fulfills", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(Peek(offset + 2).Text, "implements", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(Peek(offset + 2).Text, "uses", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(Peek(offset + 2).Text, "extends", StringComparison.OrdinalIgnoreCase))));
        }

        private bool LooksLikeInterfaceDefinition()
        {
            var offset = GetDeclarationModifierOffset();
            return MatchesKeywordAtOffset(offset, "interface") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace;
        }

        private bool LooksLikeUnionDefinition()
        {
            var offset = GetDeclarationModifierOffset();
            return MatchesKeywordAtOffset(offset, "union") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace;
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

            // Skip optional record-level modifiers: sealed, strict, partial
            while (Peek(offset).Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Peek(offset).Text, "sealed", StringComparison.Ordinal) ||
                    string.Equals(Peek(offset).Text, "strict", StringComparison.Ordinal) ||
                    string.Equals(Peek(offset).Text, "partial", StringComparison.Ordinal)))
            {
                offset++;
            }

            return MatchesKeywordAtOffset(offset, "record") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   Peek(offset + 2).Kind == SyntaxTokenKind.OpenParen;
        }

        private bool LooksLikeTypeAliasDeclaration()
        {
            var offset = GetDeclarationModifierOffset();

            if (!(MatchesKeywordAtOffset(offset, "type") &&
                  Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                  IsValidIdentifier(Peek(offset + 1).Text)))
            {
                return false;
            }

            var cursor = offset + 2;
            if (Peek(cursor).Kind == SyntaxTokenKind.LessThan)
            {
                var depth = 0;

                while (Peek(cursor).Kind is not SyntaxTokenKind.EndOfFile)
                {
                    if (Peek(cursor).Kind == SyntaxTokenKind.LessThan)
                    {
                        depth++;
                    }
                    else if (Peek(cursor).Kind == SyntaxTokenKind.GreaterThan)
                    {
                        depth--;
                        if (depth == 0)
                        {
                            cursor++;
                            break;
                        }
                    }

                    cursor++;
                }
            }

            return IsEqualsToken(Peek(cursor));
        }

        private bool LooksLikeStructDefinition()
        {
            var offset = GetDeclarationModifierOffset();

            // Skip optional struct-level modifiers: sealed, fluid, partial
            while (Peek(offset).Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Peek(offset).Text, "sealed", StringComparison.Ordinal) ||
                    string.Equals(Peek(offset).Text, "fluid", StringComparison.Ordinal) ||
                    string.Equals(Peek(offset).Text, "partial", StringComparison.Ordinal)))
            {
                offset++;
            }

            return MatchesKeywordAtOffset(offset, "struct") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   (Peek(offset + 2).Kind == SyntaxTokenKind.OpenParen ||
                    Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace);
        }

        private bool LooksLikeTraitDefinition()
        {
            var offset = GetDeclarationModifierOffset();
            return MatchesKeywordAtOffset(offset, "trait") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace;
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

        private bool LooksLikeYieldStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "yield", StringComparison.OrdinalIgnoreCase);
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

            // When a declaration modifier keyword (export, global, shy) was NOT consumed
            // (because it isn't followed by a declaration keyword), don't misinterpret it
            // as a type name.  e.g. `export FOO = "bar"` is a command, not a typed declaration.
            if (offset == 0 && Current.Kind == SyntaxTokenKind.Bareword &&
                Current.Text is "export" or "global" or "shy")
            {
                return false;
            }

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
                    string.Equals(Peek(1).Text, "const", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "type", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "using", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "require", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "func", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "class", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "module", StringComparison.Ordinal) ||
                    string.Equals(Peek(1).Text, "enum", StringComparison.Ordinal) ||
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

        private bool IsComparisonOperatorToken(SyntaxToken token)
        {
            return token.Kind is SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanEqual
                or SyntaxTokenKind.LessThan or SyntaxTokenKind.LessThanEqual
                or SyntaxTokenKind.BangEqual or SyntaxTokenKind.BangTilde
                || (!_stopRefinementAtEquals && IsEqualsToken(token))
                || (token.Kind == SyntaxTokenKind.Bareword && token.Text is
                    "==" or "=~" or "in" or "contains" or "starts-with" or "ends-with" or "is" or "not" or "as"
                    or "is-not" or "is-in" or "is-not-in" or "not-in");
        }

        private static bool IsAdditiveOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && token.Text is "+" or "-";
        }

        private static bool IsMultiplicativeOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && token.Text is "*" or "/" or "//" or "%";
        }

        private static bool IsExponentiationOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && token.Text is "**";
        }

        private static bool IsUnaryOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(token.Text, "not", StringComparison.OrdinalIgnoreCase) ||
                    token.Text is "-" or "+");
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
                    "is" => "is",
                    "is-not" => "is-not",
                    "is-in" => "is-in",
                    "is-not-in" => "is-not-in",
                    "not" => "not",
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
                    "//" => "//",
                    "%" => "%",
                    "**" => "**",
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
                    "==" or "=" or "=~" or "in" or "contains" or "starts-with" or "ends-with");
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
                   token.Text is "=" or "+=" or "-=" or "*=" or "**=" or "/=" or "//=" or "%=" or "??=";
        }

        private static string NormalizeAssignmentOperator(SyntaxToken token)
        {
            return token.Text switch
            {
                "=" => "=",
                "+=" => "+=",
                "-=" => "-=",
                "*=" => "*=",
                "**=" => "**=",
                "/=" => "/=",
                "//=" => "//=",
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

        private bool LooksLikeInputRedirection()
        {
            // Matches: in< file, i< file (bareword "in" or "i" followed by LessThan)
            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            return Current.Text is "in" or "i"
                   && Peek(1).Kind == SyntaxTokenKind.LessThan;
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

        private bool IsDeclarationBoundary(int previousEnd, bool untilCloseParen, bool untilCloseBrace, bool untilSemicolon)
        {
            return IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon) ||
                   HasImplicitStatementBoundaryAfter(previousEnd);
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

        /// <summary>
        /// Lookahead: returns true when the tokens at the current position form
        /// a tuple-assignment pattern: ( $var [, $var]* ) =
        /// </summary>
        private bool LooksLikeTupleAssignment()
        {
            if (Current.Kind != SyntaxTokenKind.OpenParen) return false;

            var offset = 1; // skip past '('

            while (true)
            {
                var token = Peek(offset);

                // Expect a variable name ($x or bare identifier)
                if (token.Kind == SyntaxTokenKind.Bareword &&
                    (IsVariableReferenceLikeToken(token) || IsValidIdentifier(token.Text)))
                {
                    offset++;
                }
                else
                {
                    return false; // Not a variable name — not a tuple pattern
                }

                var next = Peek(offset);

                if (next.Kind == SyntaxTokenKind.Comma)
                {
                    offset++; // skip comma, expect another variable
                    continue;
                }

                if (next.Kind == SyntaxTokenKind.CloseParen)
                {
                    offset++; // skip ')'
                    var afterClose = Peek(offset);
                    return afterClose.Kind == SyntaxTokenKind.Bareword && afterClose.Text == "=";
                }

                return false; // Unexpected token
            }
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
    }
}
