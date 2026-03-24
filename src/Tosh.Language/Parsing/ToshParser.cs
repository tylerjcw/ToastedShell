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

            if (LooksLikeIfStatement())
            {
                return ParseIfStatement();
            }

            if (LooksLikeVariableDeclaration())
            {
                return ParseVariableDeclarationStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeAliasStatement())
            {
                return ParseAliasStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
            }

            if (LooksLikeUsingStatement())
            {
                return ParseUsingStatement();
            }

            if (LooksLikeFunctionDefinition())
            {
                return ParseFunctionDefinitionStatement();
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

            if (LooksLikeVariableAssignment())
            {
                return ParseVariableAssignmentStatement(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
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
            var whileToken = NextToken();

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_while_condition",
                    Title: "While loops require a parenthesized condition.",
                    Span: whileToken.Span,
                    Label: "write a condition in parentheses after 'while'",
                    Help: "try 'while (<condition>) { ... }'."));
                return new WhileStatementSyntax(
                    new BarewordArgumentSyntax(string.Empty, whileToken.Span),
                    new BlockSyntax(Array.Empty<StatementSyntax>(), whileToken.Span),
                    whileToken.Span);
            }

            var openParen = NextToken();
            var condition = ParseConditionalExpression(openParen);
            var body = ParseRequiredBlock("while");
            return new WhileStatementSyntax(
                condition,
                body,
                TextSpan.FromBounds(whileToken.Span.Start, body.Span.End));
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
            var varToken = NextToken();
            var nameToken = ExpectVariableName();
            var equalsToken = ExpectEqualsToken("Variable declarations use '=' after the variable name.");
            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, equalsToken.Span.End);
            return new VariableDeclarationStatementSyntax(
                nameToken.Text,
                value,
                TextSpan.FromBounds(varToken.Span.Start, end));
        }

        private StatementSyntax ParseVariableAssignmentStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var nameToken = ExpectVariableName();
            var equalsToken = ExpectEqualsToken("Assignments use '=' between the variable name and the value.");
            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, equalsToken.Span.End);
            return new VariableAssignmentStatementSyntax(
                nameToken.Text,
                value,
                TextSpan.FromBounds(nameToken.Span.Start, end));
        }

        private StatementSyntax ParseAliasStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var aliasToken = NextToken();
            var nameToken = ExpectVariableName();
            var equalsToken = ExpectEqualsToken("Alias definitions use '=' after the alias name.");
            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: false);
            var end = GetPipelineEnd(value, equalsToken.Span.End);
            return new AliasStatementSyntax(
                nameToken.Text,
                value,
                TextSpan.FromBounds(aliasToken.Span.Start, end));
        }

        private StatementSyntax ParseUsingStatement()
        {
            var usingToken = NextToken();

            if (Current.Kind is not SyntaxTokenKind.Bareword and not SyntaxTokenKind.String)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_using_target",
                    Title: "Using statements require a namespace, type, or script path.",
                    Span: Current.Span,
                    Label: "write something like 'using System.IO' or 'using \"./common.tosh\"'"));
                return new UsingStatementSyntax(string.Empty, null, IsFileImport: false, usingToken.Span);
            }

            var targetToken = NextToken();
            string? alias = null;
            var end = targetToken.Span.End;

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
                IsFileImportTarget(targetToken),
                TextSpan.FromBounds(usingToken.Span.Start, end));
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
            var defToken = NextToken();
            var nameToken = ExpectVariableName();

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_function_parameter_list",
                    Title: "Function definitions require a parameter list.",
                    Span: Current.Span,
                    Label: "write '(...)' after the function name"));
                return new FunctionDefinitionStatementSyntax(
                    nameToken.Text,
                    Array.Empty<FunctionParameterSyntax>(),
                    null,
                    new BlockSyntax(Array.Empty<StatementSyntax>(), nameToken.Span),
                    TextSpan.FromBounds(defToken.Span.Start, nameToken.Span.End));
            }

            var parameters = ParseFunctionParameters();
            var returnTypeName = TryParseReturnTypeAnnotation();
            var body = ParseRequiredBlock("def");

            return new FunctionDefinitionStatementSyntax(
                nameToken.Text,
                parameters,
                returnTypeName,
                body,
                TextSpan.FromBounds(defToken.Span.Start, body.Span.End));
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

                return new FunctionParameterSyntax(string.Empty, null, token.Span);
            }

            var nameToken = NextToken();
            ParseTypedIdentifierToken(nameToken.Text, out var name, out var inlineTypeName, out var expectsFollowingTypeName);

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
            }
            else
            {
                typeName = ParseTypeName("parameter type");
            }

            return new FunctionParameterSyntax(name, string.IsNullOrWhiteSpace(typeName) ? null : typeName, nameToken.Span);
        }

        private string? TryParseReturnTypeAnnotation()
        {
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
                return NextToken().Text;
            }

            _diagnostics.Add(new SyntaxDiagnostic(
                Code: "tosh::parser::expected_type_name",
                Title: $"Expected a {label}.",
                Span: Current.Span,
                Label: $"write a CLR type name for the {label}"));
            return string.Empty;
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

        private PipelineStageSyntax? ParsePipelineStage(
            bool allowExpressionStage,
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            if (allowExpressionStage && LooksLikeExpressionStage())
            {
                var expression = ParseArgument();
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
                       Current.Kind != SyntaxTokenKind.Pipe)
                {
                    if (HasImplicitStatementBoundaryAfter(lastConsumedEnd))
                    {
                        break;
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

        private List<ArgumentSyntax> ParseGetArguments(
            int commandEnd,
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var arguments = new List<ArgumentSyntax>();
            var lastConsumedEnd = commandEnd;

            if (IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) ||
                Current.Kind == SyntaxTokenKind.Pipe)
            {
                return arguments;
            }

            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                arguments.Add(ParseMemberProjectionArgument());
                return arguments;
            }

            while (!IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                   Current.Kind != SyntaxTokenKind.Pipe)
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
                Current.Kind == SyntaxTokenKind.Pipe)
            {
                return arguments;
            }

            if (Current.Kind == SyntaxTokenKind.OpenBrace)
            {
                arguments.Add(ParsePredicateBlockArgument());
                return arguments;
            }

            if (TryParseLegacyWhereClause(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon, out var legacyArguments))
            {
                arguments.AddRange(legacyArguments);
                return arguments;
            }

            var predicateArgument = ParseWherePredicateExpressionArgument();

            if (predicateArgument is not null)
            {
                arguments.Add(predicateArgument);
            }

            if (!IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                Current.Kind != SyntaxTokenKind.Pipe)
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

        private bool TryParseLegacyWhereClause(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon,
            out IReadOnlyList<ArgumentSyntax> arguments)
        {
            arguments = Array.Empty<ArgumentSyntax>();

            if (Current.Kind != SyntaxTokenKind.Bareword ||
                IsVariableReferenceLikeToken(Current) ||
                !IsWhereComparisonOperator(Peek(1)))
            {
                return false;
            }

            var startPosition = _position;
            var diagnosticCount = _diagnostics.Count;

            var memberPath = ParseArgument();
            var @operator = ParseArgument();
            var expected = ParseArgument();

            var completed = memberPath is not null &&
                            @operator is not null &&
                            expected is not null &&
                            (IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) ||
                             Current.Kind == SyntaxTokenKind.Pipe);

            if (completed)
            {
                arguments = new[] { memberPath!, @operator!, expected! };
                return true;
            }

            _position = startPosition;

            if (_diagnostics.Count > diagnosticCount)
            {
                _diagnostics.RemoveRange(diagnosticCount, _diagnostics.Count - diagnosticCount);
            }

            return false;
        }

        private ArgumentSyntax? ParseArgument(string? commandName = null, bool implicitCurrentItem = false)
        {
            switch (Current.Kind)
            {
                case SyntaxTokenKind.Bareword:
                {
                    if (IsVariableReferenceLikeToken(Current))
                    {
                        return ParsePostfixChain(ParseVariableReferenceArgument(), implicitCurrentItem);
                    }

                    if (LooksLikeNewObjectExpression())
                    {
                        return ParsePostfixChain(ParseNewObjectArgument(implicitCurrentItem), implicitCurrentItem);
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

                    if (implicitCurrentItem)
                    {
                        return ParsePostfixChain(ParseImplicitCurrentItemArgument(), implicitCurrentItem);
                    }

                    var token = NextToken();
                    return new BarewordArgumentSyntax(token.Text, token.Span);
                }

                case SyntaxTokenKind.String:
                case SyntaxTokenKind.Number:
                case SyntaxTokenKind.Boolean:
                case SyntaxTokenKind.Null:
                {
                    var token = NextToken();
                    return ParsePostfixChain(new LiteralArgumentSyntax(token.Value, token.Span), implicitCurrentItem);
                }

                case SyntaxTokenKind.OpenParen:
                    return ParsePostfixChain(ParseParenthesizedArgument(implicitCurrentItem), implicitCurrentItem);

                case SyntaxTokenKind.OpenBracket:
                    return ParsePostfixChain(ParseListLiteralArgument(implicitCurrentItem), implicitCurrentItem);

                case SyntaxTokenKind.OpenBrace:
                    return string.Equals(commandName, "where", StringComparison.OrdinalIgnoreCase)
                        ? ParsePredicateBlockArgument()
                        : ParseBlockArgument();

                default:
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::unexpected_token",
                        Title: $"Unexpected token '{Current.Text}'.",
                        Span: Current.Span,
                        Label: "this token does not fit here"));
                    NextToken();
                    return null;
            }
        }

        private ArgumentSyntax ParseVariableReferenceArgument()
        {
            var variableToken = NextToken();
            ParseVariableReferenceToken(variableToken, out var name, out var memberPath);

            ArgumentSyntax expression = new VariableReferenceArgumentSyntax(name, variableToken.Span);

            if (!string.IsNullOrEmpty(memberPath))
            {
                expression = ApplyMemberOrMethodPostfix(expression, memberPath, variableToken.Span);
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
            ArgumentSyntax expression = new VariableReferenceArgumentSyntax("it", memberToken.Span);
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

        private ArgumentSyntax ParseListLiteralArgument(bool implicitCurrentItem = false)
        {
            var openBracket = NextToken();
            var items = new List<ArgumentSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBracket)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::unexpected_list_separator",
                        Title: "A list item is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add a list item here"));
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

                if (Current.Kind is not SyntaxTokenKind.CloseBracket and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_list_separator",
                        Title: "List items must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between list items"));
                }
            }

            if (Current.Kind != SyntaxTokenKind.CloseBracket)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::missing_closing_bracket",
                    Title: "A closing ']' is required here.",
                    Span: openBracket.Span,
                    Label: "this list literal never closes",
                    Help: "close the list literal with ']' after the last item."));
                return new ListLiteralArgumentSyntax(items, openBracket.Span);
            }

            var closeBracket = NextToken();
            return new ListLiteralArgumentSyntax(items, TextSpan.FromBounds(openBracket.Span.Start, closeBracket.Span.End));
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

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_constructor_parenthesis",
                    Title: "Object construction uses C#-style parentheses.",
                    Span: typeToken.Span,
                    Label: "add '(' after the type name",
                    Help: "try 'new SomeType(...)' instead of command-style construction."));
                return new NewObjectArgumentSyntax(typeToken.Text, Array.Empty<ArgumentSyntax>(), TextSpan.FromBounds(newToken.Span.Start, typeToken.Span.End));
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
                    typeToken.Text,
                    arguments,
                    TextSpan.FromBounds(newToken.Span.Start, arguments.Count > 0 ? arguments[^1].Span.End : typeToken.Span.End));
            }

            var constructorCloseParen = NextToken();
            return new NewObjectArgumentSyntax(
                typeToken.Text,
                arguments,
                TextSpan.FromBounds(newToken.Span.Start, constructorCloseParen.Span.End));
        }

        private ArgumentSyntax ParsePostfixChain(ArgumentSyntax expression, bool implicitCurrentItem = false)
        {
            while (TryConsumePostfixToken(out var postfixToken, out var postfixText))
            {
                expression = ApplyQualifiedMemberChain(expression, postfixText, postfixToken.Span, implicitCurrentItem);
            }

            return expression;
        }

        private ArgumentSyntax ApplyQualifiedMemberChain(
            ArgumentSyntax expression,
            string qualifiedText,
            TextSpan qualifiedSpan,
            bool implicitCurrentItem = false)
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
                    allowMethodCall: index == segments.Length - 1);
            }

            return expression;
        }

        private ArgumentSyntax ApplyMemberOrMethodPostfix(
            ArgumentSyntax expression,
            string postfixText,
            TextSpan postfixSpan,
            bool implicitCurrentItem = false,
            bool allowMethodCall = true)
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
                    TextSpan.FromBounds(expression.Span.Start, end));
            }

            return new MemberAccessArgumentSyntax(
                expression,
                postfixText,
                TextSpan.FromBounds(expression.Span.Start, postfixSpan.End));
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
            var left = ParseArgumentOperand(implicitCurrentItem);

            while (IsBinaryOperatorToken(Current))
            {
                var operatorToken = NextToken();
                var right = ParseArgumentOperand(implicitCurrentItem);
                var end = right?.Span.End ?? operatorToken.Span.End;

                left = new OperatorArgumentSyntax(
                    left ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    operatorToken.Text,
                    operatorToken.Span,
                    right ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    TextSpan.FromBounds(startPosition, end));

                if (right is null)
                {
                    break;
                }
            }

            return left ?? new BarewordArgumentSyntax(string.Empty, new TextSpan(startPosition, 0));
        }

        private ArgumentSyntax? ParseArgumentOperand(bool implicitCurrentItem = false)
        {
            if (Current.Kind == SyntaxTokenKind.CloseParen)
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

        private PredicateBlockArgumentSyntax ParsePredicateBlockArgument()
        {
            var openBrace = NextToken();
            var clauses = new List<PredicateClauseSyntax>();

            while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                var clause = ParsePredicateClause();

                if (clause is not null)
                {
                    clauses.Add(clause);
                }

                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                if (clause is not null && HasImplicitStatementBoundaryAfter(clause.Span.End))
                {
                    continue;
                }

                if (Current.Kind != SyntaxTokenKind.CloseBrace && Current.Kind != SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh::parser::missing_predicate_separator",
                        Title: "Predicate clauses must be separated by ';'.",
                        Span: Current.Span,
                        Label: "insert ';' between predicate clauses"));
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
                return new PredicateBlockArgumentSyntax(clauses, openBrace.Span);
            }

            var closeBrace = NextToken();
            return new PredicateBlockArgumentSyntax(clauses, TextSpan.FromBounds(openBrace.Span.Start, closeBrace.Span.End));
        }

        private PredicateClauseSyntax? ParsePredicateClause()
        {
            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_predicate_member",
                    Title: "Expected a member path in this predicate clause.",
                    Span: Current.Span,
                    Label: "predicate clauses start with a member path like 'Size' or 'Modified'"));
                NextToken();
                return null;
            }

            var memberToken = NextToken();

            if (!IsWhereComparisonOperator(Current))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh::parser::expected_predicate_operator",
                    Title: "Expected a comparison operator in this predicate clause.",
                    Span: Current.Span,
                    Label: "use operators like '==', '>=', '<', or 'contains'"));

                if (Current.Kind is not SyntaxTokenKind.CloseBrace and not SyntaxTokenKind.EndOfFile and not SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                }

                return null;
            }

            var operatorToken = NextToken();
            var expected = ParseArgument();

            if (expected is null)
            {
                return null;
            }

            return new PredicateClauseSyntax(
                memberToken.Text,
                memberToken.Span,
                operatorToken.Text,
                operatorToken.Span,
                expected,
                TextSpan.FromBounds(memberToken.Span.Start, expected.Span.End));
        }

        private PipelineSyntax ParsePipeline(
            bool untilCloseParen,
            bool untilCloseBrace,
            bool untilSemicolon,
            bool allowExpressionStart)
        {
            var stages = new List<PipelineStageSyntax>();

            while (!IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon))
            {
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

            return new PipelineSyntax(stages);
        }

        private void SkipToStageBoundary(bool untilCloseParen, bool untilCloseBrace, bool untilSemicolon)
        {
            while (!IsPipelineTerminator(Current.Kind, untilCloseParen, untilCloseBrace, untilSemicolon) &&
                   Current.Kind != SyntaxTokenKind.Pipe)
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
                SyntaxTokenKind.OpenParen or
                SyntaxTokenKind.OpenBracket => true,
                SyntaxTokenKind.Bareword => true,
                _ => false,
            };
        }

        private bool HasTopLevelOperatorBeforeCloseParen()
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
                            return false;
                        }
                        break;

                    case SyntaxTokenKind.Bareword when depth == 0 && IsBinaryOperatorToken(token):
                    case SyntaxTokenKind.GreaterThan when depth == 0:
                    case SyntaxTokenKind.GreaterThanEqual when depth == 0:
                    case SyntaxTokenKind.LessThan when depth == 0:
                    case SyntaxTokenKind.LessThanEqual when depth == 0:
                    case SyntaxTokenKind.BangEqual when depth == 0:
                        return true;
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
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Current.Text, "var", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "set", StringComparison.Ordinal)) &&
                   Peek(1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(1).Text) &&
                   IsEqualsToken(Peek(2));
        }

        private bool LooksLikeAliasStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "alias", StringComparison.Ordinal) &&
                   Peek(1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(1).Text) &&
                   IsEqualsToken(Peek(2));
        }

        private bool LooksLikeUsingStatement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "using", StringComparison.OrdinalIgnoreCase);
        }

        private bool LooksLikeFunctionDefinition()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "def", StringComparison.Ordinal) &&
                   Peek(1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(1).Text) &&
                   Peek(2).Kind == SyntaxTokenKind.OpenParen;
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

        private bool LooksLikeVariableAssignment()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Current.Text) &&
                   IsEqualsToken(Peek(1));
        }

        private bool LooksLikeExpressionStage()
        {
            return Current.Kind switch
            {
                SyntaxTokenKind.String or SyntaxTokenKind.Number or SyntaxTokenKind.Boolean or SyntaxTokenKind.Null or SyntaxTokenKind.OpenParen or SyntaxTokenKind.OpenBracket => true,
                SyntaxTokenKind.Bareword => IsVariableReferenceLikeToken(Current) || LooksLikeNewObjectExpression() || LooksLikeStaticMethodCallExpression() || LooksLikeStaticMemberAccessExpression(),
                _ => false,
            };
        }

        private bool LooksLikeNewObjectExpression()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "new", StringComparison.Ordinal) &&
                   Peek(1).Kind == SyntaxTokenKind.Bareword &&
                   Peek(2).Kind == SyntaxTokenKind.OpenParen;
        }

        private bool LooksLikeStaticMethodCallExpression()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   Peek(1).Kind == SyntaxTokenKind.OpenParen &&
                   LooksLikeQualifiedDotNetAccess(Current.Text);
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
                    or SyntaxTokenKind.BangEqual
                || (token.Kind == SyntaxTokenKind.Bareword && IsBinaryOperator(token.Text));
        }

        private static bool IsWhereComparisonOperator(SyntaxToken token)
        {
            return token.Kind is SyntaxTokenKind.GreaterThan or SyntaxTokenKind.GreaterThanEqual
                    or SyntaxTokenKind.LessThan or SyntaxTokenKind.LessThanEqual
                    or SyntaxTokenKind.BangEqual
                || (token.Kind == SyntaxTokenKind.Bareword && token.Text is
                    "==" or "=" or "eq" or "ne" or "contains" or "starts-with" or "ends-with");
        }

        private static bool IsBinaryOperator(string text)
        {
            return text is "+" or "-" or "*" or "/" or "==" or "and" or "or";
        }

        private static bool IsEqualsToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && string.Equals(token.Text, "=", StringComparison.Ordinal);
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

        private bool TryConsumePostfixToken(out SyntaxToken token, out string postfixText)
        {
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                Current.Text.Length > 1 &&
                Current.Text[0] == '.')
            {
                token = NextToken();
                postfixText = token.Text[1..];
                return true;
            }

            token = Current;
            postfixText = string.Empty;
            return false;
        }

        private static bool IsVariableReferenceLikeToken(SyntaxToken token)
        {
            if (token.Kind != SyntaxTokenKind.Bareword ||
                token.Text.Length <= 1 ||
                token.Text[0] != '$')
            {
                return false;
            }

            ParseVariableReferenceToken(token, out var name, out _);
            return IsValidIdentifier(name);
        }

        private static void ParseVariableReferenceToken(SyntaxToken token, out string name, out string? memberPath)
        {
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

            return TextSpan.FromBounds(pipeline.Stages[0].Span.Start, pipeline.Stages[^1].Span.End);
        }

        private static int GetPipelineEnd(PipelineSyntax pipeline, int fallbackEnd)
        {
            return pipeline.Stages.Count > 0 ? pipeline.Stages[^1].Span.End : fallbackEnd;
        }

        private SyntaxToken Peek(int offset)
        {
            var index = Math.Min(_position + offset, _tokens.Count - 1);
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
