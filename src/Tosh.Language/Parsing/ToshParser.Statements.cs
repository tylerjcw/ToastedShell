using System.Text;
using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public static partial class ToshParser
{
    /// <summary>
    /// Statement parsing: the statement dispatcher, every statement and declaration
    /// form, block parsing, and the recovery helpers that find a statement boundary.
    ///
    /// Moved out of ToshParser.cs by `TOAST-0005`. Every member moved **verbatim**.
    ///
    /// The declaration forms live here rather than in a file of their own because
    /// `class`, `enum`, `record`, `struct`, `union`, `trait`, `interface`, `module`,
    /// `rune`, `event` and the raw FFI declarations are all reached from
    /// `ParseStatement`. They are statements that happen to declare something, and
    /// splitting them out would separate them from the dispatcher that chooses between
    /// them.
    /// </summary>
    private sealed partial class InternalParser
    {

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

            if (LooksLikeExtendDefinition())
            {
                return ParseExtendStatement(docTokens);
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

            // `raw struct` / `raw union` must be tested before plain `struct`:
            // they are a different declaration kind, not a modified one.
            if (LooksLikeRawStructDefinition())
            {
                return ParseRawStructDefinitionStatement(docTokens);
            }

            // `raw callback` is likewise its own declaration kind, and must be
            // tested before `raw func` so the shared `raw` prefix does not send
            // it down the binding path.
            if (LooksLikeRawCallbackDefinition())
            {
                return ParseRawCallbackDefinitionStatement(docTokens);
            }

            // Top-level `raw func name(...) -> ret from "lib"`.
            if (MatchesKeywordAtOffset(GetDeclarationModifierOffset(), "raw"))
            {
                var rawOffset = GetDeclarationModifierOffset();

                if (MatchesKeywordAtOffset(rawOffset + 1, "func"))
                {
                    var rawStart = Current.Span.Start;
                    var rawModifier = ParseDeclarationModifier();
                    NextToken(); // consume 'raw'

                    if (LooksLikeRawNativeFunction())
                    {
                        var binding = ParseNativeBindingFunction();
                        return new RawFunctionStatementSyntax(
                            binding,
                            rawModifier,
                            TextSpan.FromBounds(rawStart, binding.Span.End));
                    }

                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.raw_func_requires_library",
                        Title: "A top-level 'raw func' needs a library.",
                        Span: Current.Span,
                        Label: "write 'raw func name(...) -> type from \"libc.so.6\"'"));
                }
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

            // Destructuring: var { a, b } = ..., var [a, b] = ..., or var (a, b) = ...
            if (Current.Kind is SyntaxTokenKind.OpenBrace or SyntaxTokenKind.OpenBracket or SyntaxTokenKind.OpenParen)
            {
                return ParseDestructuringDeclaration(declarationStart, modifier, isConst, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);
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

            if (IsDeclarationBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon))
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

            // An initializer may legitimately continue onto the next line — `var x =` then an
            // indented `(1 + 2)` is a required-operand continuation, and is tested as such by
            // LiteParserTests. But the continuation consumed *anything* that followed, including
            // lines that can only be new statements: `var x =` followed by `var y =` ate the
            // second declaration, so `y` was never declared and the binder then reported that
            // `var` looked like an unknown command — a diagnostic two lines from the mistake.
            // And `var x =` with nothing after it at all bound null silently.
            //
            // So the rule is narrow: reject only when there is no operand to find (end of input)
            // or when what follows can only be another declaration. Everything the continuation
            // rule was built for still works (TS-P1-41).
            if (Current.Kind is SyntaxTokenKind.EndOfFile || LooksLikeVariableDeclaration())
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_initializer",
                    Title: $"Expected an expression after '=' in the declaration of '{declaredName}'.",
                    Span: equalsToken.Span,
                    Label: "write the value here, or drop the '=' to declare without initializing"));
            }

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
            var assignmentToken = ExpectAssignmentOperatorToken(
                "Assignments use '=', '+=', '-=', '*=', '**=', '/=', '//=', '%=', or '??=' between the variable name and the value.");
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
            var assignmentToken = ExpectAssignmentOperatorToken(
                "Assignments use '=', '+=', '-=', '*=', '**=', '/=', '//=', '%=', or '??=' between the member path and the value.");
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
            // Taken raw when it is a bareword, the way `enum` takes its name token: with the
            // colon spelling the token is `Name:` or `Name:Base`, which is not a valid
            // identifier until the colon is split off below.
            var nameToken = Current.Kind == SyntaxTokenKind.Bareword ? NextToken() : ExpectVariableName();

            // `TOAST-0112`. `type Name: Base` reads the way `enum Name: int` does, and the lexer
            // hands both over the same way: the colon rides on the name token.
            ParseTypedIdentifierToken(
                nameToken.Text,
                out var aliasName,
                out var inlineBaseTypeName,
                out var expectsFollowingBaseType);

            var usedColonSpelling = nameToken.Text.Contains(':', StringComparison.Ordinal);
            var typeParameters = usedColonSpelling ? Array.Empty<string>() : ParseTypeParameterList();

            // `TOAST-0112`. The base type is what a refinement refines, so it cannot be left out
            // — but the omission is an easy one to make when the body is a brace block, and the
            // message should show both spellings rather than only complain.
            // Only when the base is genuinely absent. Under the colon spelling with no space —
            // `type A:int { … }` — the brace is the body and the base is already in hand.
            var missingBaseType = Current.Kind == SyntaxTokenKind.OpenBrace &&
                (!usedColonSpelling || expectsFollowingBaseType);

            if (missingBaseType)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_alias_base_type",
                    Title: $"Type '{aliasName}' does not say what it refines.",
                    Span: Current.Span,
                    Label: "a refinement needs the type it narrows",
                    Help: $"write `type {aliasName}: int {{ … }}` or "
                        + $"`type {aliasName} = int {{ … }}`, naming the base type."));
            }

            SyntaxToken equalsToken;
            string baseTypeName;

            if (missingBaseType)
            {
                // Reported once, above. Expecting a separator here as well would add a second
                // error for the same missing word.
                equalsToken = nameToken;
                baseTypeName = string.Empty;
            }
            else if (usedColonSpelling)
            {
                equalsToken = nameToken;
                baseTypeName = expectsFollowingBaseType
                    ? ParseTypeName("alias base type")
                    : inlineBaseTypeName ?? string.Empty;
            }
            else
            {
                equalsToken = IsColonToken(Current)
                    ? NextToken()
                    : ExpectEqualsToken(
                        "Type aliases use '=' or ':' between the alias name and the base type.");

                baseTypeName = ParseTypeName("alias base type");
            }
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
                aliasName,
                typeParameters,
                baseTypeName,
                refinement,
                modifier,
                TextSpan.FromBounds(declarationStart, Math.Max(typeToken.Span.End, end)),
                docComment);
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

        private StatementSyntax ParseThrowStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var throwToken = NextToken();

            if (IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) ||
                Current.Kind == SyntaxTokenKind.Pipe ||
                IsAtElementBoundary())
            {
                return TryWrapPostfixConditional(new ThrowStatementSyntax(null, throwToken.Span));
            }

            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, throwToken.Span.End);
            return TryWrapPostfixConditional(new ThrowStatementSyntax(
                value.Stages.Count == 0 ? null : value,
                TextSpan.FromBounds(throwToken.Span.Start, end)));
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

            if (FindYieldInDeferredBlock(body) is { } strayYield)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.yield_in_defer",
                    Title: "A deferred block cannot yield.",
                    Span: strayYield,
                    Label: "this value would have nowhere to go",
                    Help: "a deferred block runs while the function unwinds, after the consumer may "
                        + "have stopped pulling, so there is no stream left for it to join. Move the "
                        + "yield into the body, or collect the value and yield it before returning."));
            }

            return new DeferStatementSyntax(
                body,
                TextSpan.FromBounds(deferToken.Span.Start, body.Span.End));
        }

        private static TextSpan? FindYieldInDeferredStatement(StatementSyntax statement)
        {
            switch (statement)
            {
                case YieldStatementSyntax yieldStatement:
                    return yieldStatement.Span;

                // A nested declaration owns its own yields.
                case FunctionDefinitionStatementSyntax:
                case RuneDefinitionStatementSyntax:
                case ClassDefinitionStatementSyntax:
                    return null;

                case IfStatementSyntax ifStatement:
                    return FindYieldInDeferredBlock(ifStatement.ThenBlock)
                        ?? (ifStatement.ElseBlock is not null ? FindYieldInDeferredBlock(ifStatement.ElseBlock) : null);
                case ForStatementSyntax forStatement:
                    return FindYieldInDeferredBlock(forStatement.Body);
                case WhileStatementSyntax whileStatement:
                    return FindYieldInDeferredBlock(whileStatement.Body);
                case UntilStatementSyntax untilStatement:
                    return FindYieldInDeferredBlock(untilStatement.Body);
                case DeferStatementSyntax nestedDefer:
                    return FindYieldInDeferredBlock(nestedDefer.Body);
                case TryStatementSyntax tryStatement:
                    return FindYieldInDeferredBlock(tryStatement.TryBlock)
                        ?? (tryStatement.CatchClause is not null ? FindYieldInDeferredBlock(tryStatement.CatchClause.Body) : null)
                        ?? (tryStatement.FinallyBlock is not null ? FindYieldInDeferredBlock(tryStatement.FinallyBlock) : null);
                case SwitchStatementSyntax switchStatement:
                    foreach (var switchCase in switchStatement.Cases)
                    {
                        if (FindYieldInDeferredBlock(switchCase.Body) is { } caseSpan)
                        {
                            return caseSpan;
                        }
                    }

                    return switchStatement.DefaultBlock is not null
                        ? FindYieldInDeferredBlock(switchStatement.DefaultBlock)
                        : null;
                default:
                    return null;
            }
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

        private static bool IsJumpStatementKeyword(string text) =>
            string.Equals(text, "throw", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "return", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "yield", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "break", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(text, "continue", StringComparison.OrdinalIgnoreCase);

        // Parse a jump statement appearing as the head of a match-arm body. Mirrors
        // the dispatch in ParseStatement but uses arm-appropriate stop conditions
        // (stop at close-brace and semicolon, not close-paren since arm bodies are
        // not enclosed in parens).
        private StatementSyntax ParseJumpStatementForArm()
        {
            var text = Current.Text;
            if (string.Equals(text, "throw", StringComparison.OrdinalIgnoreCase))
            {
                return ParseThrowStatement(stopAtCloseParen: false, stopAtCloseBrace: true, stopAtSemicolon: true);
            }
            if (string.Equals(text, "return", StringComparison.OrdinalIgnoreCase))
            {
                return ParseReturnStatement(stopAtCloseParen: false, stopAtCloseBrace: true, stopAtSemicolon: true);
            }
            if (string.Equals(text, "yield", StringComparison.OrdinalIgnoreCase))
            {
                return ParseYieldStatement(stopAtCloseParen: false, stopAtCloseBrace: true, stopAtSemicolon: true);
            }
            if (string.Equals(text, "break", StringComparison.OrdinalIgnoreCase))
            {
                return ParseLoopControlStatement(isBreak: true);
            }
            // continue
            return ParseLoopControlStatement(isBreak: false);
        }

        private StatementSyntax ParseReturnStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var returnToken = NextToken();

            if (IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) ||
                Current.Kind == SyntaxTokenKind.Pipe ||
                IsAtElementBoundary())
            {
                return TryWrapPostfixConditional(new ReturnStatementSyntax(null, returnToken.Span));
            }

            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, returnToken.Span.End);

            return TryWrapPostfixConditional(new ReturnStatementSyntax(
                value.Stages.Count == 0 ? null : value,
                TextSpan.FromBounds(returnToken.Span.Start, end)));
        }

        private StatementSyntax ParseYieldStatement(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var yieldToken = NextToken();

            if (IsPipelineTerminator(Current.Kind, stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) ||
                Current.Kind == SyntaxTokenKind.Pipe ||
                IsAtElementBoundary())
            {
                return TryWrapPostfixConditional(new YieldStatementSyntax(null, yieldToken.Span));
            }

            var value = ParsePipeline(
                untilCloseParen: stopAtCloseParen,
                untilCloseBrace: stopAtCloseBrace,
                untilSemicolon: stopAtSemicolon,
                allowExpressionStart: true);
            var end = GetPipelineEnd(value, yieldToken.Span.End);

            return TryWrapPostfixConditional(new YieldStatementSyntax(
                value.Stages.Count == 0 ? null : value,
                TextSpan.FromBounds(yieldToken.Span.Start, end)));
        }

        private StatementSyntax ParseLoopControlStatement(bool isBreak)
        {
            var keyword = NextToken();
            StatementSyntax stmt = isBreak
                ? new BreakStatementSyntax(keyword.Span)
                : new ContinueStatementSyntax(keyword.Span);
            return TryWrapPostfixConditional(stmt);
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
                // A multi-input declaration takes each description from its own named tag; there
                // is no single prose body that could describe all of them.
                if (docComment is { Parameters.Count: > 0 })
                {
                    parameters = parameters
                        .Select(parameter =>
                            docComment.Parameters.TryGetValue(parameter.Name, out var tagged) &&
                            !string.IsNullOrWhiteSpace(tagged)
                                ? parameter with { Description = tagged.Trim() }
                                : parameter)
                        .ToArray();
                }

                return new ScriptInputStatementSyntax(
                    kind,
                    parameters,
                    TextSpan.FromBounds(keyword.Span.Start, end),
                    docComment);
            }

            var parameter = ParseFunctionParameter();

            // Attach the doc-comment's description for single-flag/arg declarations. A named tag
            // for this input wins over the block's prose: `## @arg target - what to build` above
            // an `arg target` used to leave the summary in place, so a comment documenting three
            // inputs described all three with the same sentence (`TS-P2-67`).
            var description = docComment is not null &&
                              docComment.Parameters.TryGetValue(parameter.Name, out var tagged) &&
                              !string.IsNullOrWhiteSpace(tagged)
                ? tagged.Trim()
                : docComment?.Description?.Trim();

            if (!string.IsNullOrEmpty(description))
                parameter = parameter with { Description = description };
            return new ScriptInputStatementSyntax(
                kind,
                [parameter],
                TextSpan.FromBounds(keyword.Span.Start, parameter.Span.End),
                docComment);
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
                    "default" => SubcommandModifier.Default,
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

            if ((modifiers & SubcommandModifier.Default) != 0 &&
                (modifiers & SubcommandModifier.Hollow) != 0)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.incompatible_subcommand_modifiers",
                    Title: "'default' and 'hollow' cannot be combined on a subcommand.",
                    Span: Current.Span,
                    Label: "remove one of these modifiers",
                    Help: "'hollow' subcommands cannot accept arguments, but 'default' subcommands receive the unmatched positional."));
            }

            var keyword = NextToken();
            string nameForValidation = Current.Kind == SyntaxTokenKind.Bareword ? Current.Text : string.Empty;
            var nameColonIdx = nameForValidation.IndexOf(':');
            if (nameColonIdx >= 0)
            {
                nameForValidation = nameForValidation[..nameColonIdx];
            }
            if (Current.Kind != SyntaxTokenKind.Bareword || !IsValidCommandName(nameForValidation))
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
            var resolvedName = nameToken.Text;
            string? fusedFirstModifier = null;
            var sawPostfixColon = false;
            var postfixColonIdx = resolvedName.IndexOf(':');
            if (postfixColonIdx >= 0)
            {
                sawPostfixColon = true;
                if (postfixColonIdx + 1 < resolvedName.Length)
                {
                    fusedFirstModifier = resolvedName[(postfixColonIdx + 1)..];
                }
                resolvedName = resolvedName[..postfixColonIdx];
            }
            else if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text.StartsWith(':'))
            {
                var colonTok = NextToken();
                sawPostfixColon = true;
                if (colonTok.Text.Length > 1)
                {
                    fusedFirstModifier = colonTok.Text[1..];
                }
            }

            if (sawPostfixColon)
            {
                void ApplyPostfixModifier(string text, TextSpan span)
                {
                    if (!IsSubcommandModifierKeyword(text))
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.unknown_subcommand_modifier",
                            Title: $"Unknown subcommand modifier '{text}'.",
                            Span: span,
                            Label: "expected one of: eager, hidden, hollow, vital, default"));
                        return;
                    }
                    if (!seenModifiers.Add(text))
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.duplicate_subcommand_modifier",
                            Title: $"Subcommand modifier '{text}' is repeated.",
                            Span: span,
                            Label: "remove the duplicate modifier"));
                        return;
                    }
                    modifiers |= text switch
                    {
                        "eager" => SubcommandModifier.Eager,
                        "hidden" => SubcommandModifier.Hidden,
                        "hollow" => SubcommandModifier.Hollow,
                        "vital" => SubcommandModifier.Vital,
                        "default" => SubcommandModifier.Default,
                        _ => SubcommandModifier.None,
                    };
                }

                if (fusedFirstModifier is not null)
                {
                    ApplyPostfixModifier(fusedFirstModifier, nameToken.Span);
                }

                while (Current.Kind == SyntaxTokenKind.Bareword && IsValidCommandName(Current.Text))
                {
                    var modTok = NextToken();
                    ApplyPostfixModifier(modTok.Text, modTok.Span);
                }

                if ((modifiers & SubcommandModifier.Eager) != 0 &&
                    (modifiers & SubcommandModifier.Hollow) != 0)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.incompatible_subcommand_modifiers",
                        Title: "'eager' and 'hollow' cannot be combined on a subcommand.",
                        Span: nameToken.Span,
                        Label: "remove one of these modifiers",
                        Help: "'hollow' forbids a body, but 'eager' requires one to run."));
                }
                if ((modifiers & SubcommandModifier.Default) != 0 &&
                    (modifiers & SubcommandModifier.Hollow) != 0)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.incompatible_subcommand_modifiers",
                        Title: "'default' and 'hollow' cannot be combined on a subcommand.",
                        Span: nameToken.Span,
                        Label: "remove one of these modifiers",
                        Help: "'hollow' subcommands cannot accept arguments, but 'default' subcommands receive the unmatched positional."));
                }
            }
            nameToken = nameToken with { Text = resolvedName };

            IReadOnlyList<FunctionParameterSyntax> parameters = Array.Empty<FunctionParameterSyntax>();
            if (Current.Kind == SyntaxTokenKind.OpenParen)
            {
                parameters = ParseFunctionParameters();
            }

            BlockSyntax body;

            if (IsFatArrow(Current))
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

            var typeParameters = opNameOpenParenConsumed ? Array.Empty<string>() : ParseTypeParameterList();

            IReadOnlyList<FunctionParameterSyntax> parameters = Array.Empty<FunctionParameterSyntax>();

            if (opNameOpenParenConsumed || Current.Kind == SyntaxTokenKind.OpenParen)
            {
                parameters = ParseFunctionParameters(skipOpenParen: opNameOpenParenConsumed);
            }
            else if (!IsFatArrow(Current))
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

            // Optional `where T: Constraint[, ...]` clauses (one per type parameter).
            List<TypeParameterConstraintSyntax>? typeParameterConstraints = null;
            while (MatchesKeyword(Current, "where"))
            {
                if (TryParseWhereClause(out var clause))
                {
                    typeParameterConstraints ??= new List<TypeParameterConstraintSyntax>();
                    typeParameterConstraints.Add(clause);
                }
                else
                {
                    break;
                }
            }

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

            if (IsFatArrow(Current))
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
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()),
                TypeParameters: typeParameters.Count > 0 ? typeParameters : null,
                TypeParameterConstraints: typeParameterConstraints);
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
                    string.Equals(Current.Text, "abstract", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "hermit", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "static", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "strict", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "partial", StringComparison.Ordinal)))
            {
                isSealed |= string.Equals(Current.Text, "sealed", StringComparison.Ordinal);
                isAbstract |= string.Equals(Current.Text, "hollow", StringComparison.Ordinal) ||
                              string.Equals(Current.Text, "abstract", StringComparison.Ordinal);
                isHermit |= string.Equals(Current.Text, "hermit", StringComparison.Ordinal) ||
                            string.Equals(Current.Text, "static", StringComparison.Ordinal);
                isStrict |= string.Equals(Current.Text, "strict", StringComparison.Ordinal);
                isPartial |= string.Equals(Current.Text, "partial", StringComparison.Ordinal);
                NextToken();
            }

            var classToken = NextToken();
            var nameToken = ExpectVariableName();
            var typeParameters = ParseTypeParameterList();
            var primaryConstructorParameters = Current.Kind == SyntaxTokenKind.OpenParen
                ? ParseFunctionParameters()
                : Array.Empty<FunctionParameterSyntax>();

            // Parse optional 'extends BaseClass' with optional constructor args
            string? baseClassName = null;
            IReadOnlyList<PipelineSyntax>? baseConstructorArgs = null;
            IReadOnlyList<string>? baseTypeArgs = null;
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "extends", StringComparison.OrdinalIgnoreCase))
            {
                NextToken(); // consume 'extends'
                var baseNameStart = Current.Span;
                // Parse a dotted bareword (e.g. 'System.Uri') but stop
                // before any generic-argument list so that
                // 'extends Foo<X, Y>' splits cleanly into name + type-args.
                var baseNameBuilder = new StringBuilder();
                if (Current.Kind == SyntaxTokenKind.Bareword)
                {
                    baseNameBuilder.Append(NextToken().Text);
                }
                else
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.expected_type_name",
                        Title: "Expected a base class name.",
                        Span: Current.Span,
                        Label: "write a class or CLR type after 'extends'"));
                }
                baseClassName = baseNameBuilder.ToString();
                if (Current.Kind == SyntaxTokenKind.LessThan)
                {
                    var (_, args, hasAngles) = ParseGenericTypeArgumentsStructured();
                    if (hasAngles) baseTypeArgs = args;
                }

                // Parse optional base constructor args: extends Parent($x, $y)
                if (Current.Kind == SyntaxTokenKind.OpenParen)
                {
                    baseConstructorArgs = ParseBaseConstructorArguments();
                }
            }

            // Parse optional 'fulfills' / 'uses' / 'where' clauses. They may
            // appear in any order, and `where` clauses may even be interleaved
            // (each occurrence is accumulated). We loop until none of the
            // three keywords matches the current token.
            List<string>? implementedInterfaces = null;
            List<string>? usedTraits = null;
            List<TypeParameterConstraintSyntax>? typeParameterConstraints = null;

            while (Current.Kind == SyntaxTokenKind.Bareword)
            {
                var text = Current.Text;
                if (string.Equals(text, "fulfills", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "implements", StringComparison.OrdinalIgnoreCase))
                {
                    NextToken(); // consume 'fulfills'/'implements'
                    implementedInterfaces ??= new List<string>();
                    implementedInterfaces.Add(ParseTypeName("interface"));
                    while (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken();
                        implementedInterfaces.Add(ParseTypeName("interface"));
                    }
                    continue;
                }

                if (string.Equals(text, "uses", StringComparison.OrdinalIgnoreCase))
                {
                    NextToken(); // consume 'uses'
                    usedTraits ??= new List<string>();
                    usedTraits.Add(ParseTypeName("trait"));
                    while (Current.Kind == SyntaxTokenKind.Comma)
                    {
                        NextToken();
                        usedTraits.Add(ParseTypeName("trait"));
                    }
                    continue;
                }

                if (string.Equals(text, "where", StringComparison.Ordinal))
                {
                    typeParameterConstraints ??= new List<TypeParameterConstraintSyntax>();
                    if (TryParseWhereClause(out var clause))
                    {
                        typeParameterConstraints.Add(clause);
                    }
                    else
                    {
                        break;
                    }
                    continue;
                }

                break;
            }

            var body = ParseClassBody(nameToken.Text);

            return new ClassDefinitionStatementSyntax(
                nameToken.Text,
                primaryConstructorParameters,
                body,
                modifier,
                TextSpan.FromBounds(declarationStart, body.Count == 0 ? nameToken.Span.End : body[^1].Span.End),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()),
                TypeParameters: typeParameters.Count > 0 ? typeParameters : null,
                BaseClassName: baseClassName,
                BaseConstructorArgs: baseConstructorArgs,
                ImplementedInterfaces: implementedInterfaces,
                UsedTraits: usedTraits,
                IsSealed: isSealed,
                IsAbstract: isAbstract,
                IsHermit: isHermit,
                IsStrict: isStrict,
                IsPartial: isPartial,
                BaseTypeArguments: baseTypeArgs,
                TypeParameterConstraints: typeParameterConstraints);
        }

        private StatementSyntax ParseInterfaceDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            NextToken(); // consume 'interface'
            var nameToken = ExpectVariableName();
            var typeParameters = ParseTypeParameterList(out var typeParameterVariances);

            // Optional `where T: Constraint[, ...]` clauses.
            List<TypeParameterConstraintSyntax>? typeParameterConstraints = null;
            while (Current.Kind == SyntaxTokenKind.Bareword
                && string.Equals(Current.Text, "where", StringComparison.Ordinal))
            {
                typeParameterConstraints ??= new List<TypeParameterConstraintSyntax>();
                if (TryParseWhereClause(out var clause))
                {
                    typeParameterConstraints.Add(clause);
                }
                else
                {
                    break;
                }
            }

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
                    DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()),
                    TypeParameters: typeParameters.Count > 0 ? typeParameters : null,
                    TypeParameterConstraints: typeParameterConstraints,
                    TypeParameterVariances: typeParameterVariances.Count > 0 ? typeParameterVariances : null);
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

                    // Allow operator-symbol names (e.g. 'func +(other)') so that
                    // interfaces can describe operator-overload contracts. The
                    // '<(' token (LessThanOpenParen) eats the opening paren, so
                    // tell ParseFunctionParameters to skip it in that case.
                    var openParenConsumed = false;
                    var methodName = ExpectCommandOrOperatorName(out openParenConsumed);

                    IReadOnlyList<FunctionParameterSyntax> parameters = Array.Empty<FunctionParameterSyntax>();
                    if (openParenConsumed || Current.Kind == SyntaxTokenKind.OpenParen)
                    {
                        parameters = ParseFunctionParameters(skipOpenParen: openParenConsumed);
                    }

                    // Optional return type annotation. Accept the same forms
                    // that class/free functions accept: '->', '- >', '->T'.
                    // Tolerate the older 'func name(): T' shorthand by also
                    // matching a freestanding ':' bareword.
                    string? returnTypeName = TryParseReturnTypeAnnotation();
                    if (returnTypeName is null &&
                        Current.Kind == SyntaxTokenKind.Bareword &&
                        Current.Text == ":")
                    {
                        NextToken(); // consume ':'
                        returnTypeName = ParseTypeName("return type");
                    }

                    var methodEnd = Current.Span.Start;
                    if (methodEnd <= methodName.Span.End)
                    {
                        methodEnd = parameters.Count > 0 ? parameters[^1].Span.End : methodName.Span.End;
                    }
                    methods.Add(new InterfaceMethodSignatureSyntax(
                        methodName.Text,
                        parameters,
                        returnTypeName,
                        TextSpan.FromBounds(methodStart, methodEnd)));
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
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()),
                TypeParameters: typeParameters.Count > 0 ? typeParameters : null,
                TypeParameterConstraints: typeParameterConstraints,
                TypeParameterVariances: typeParameterVariances.Count > 0 ? typeParameterVariances : null);
        }

        private StatementSyntax ParseUnionDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();
            NextToken(); // consume 'union'
            var nameToken = ExpectVariableName();
            var typeParameters = ParseTypeParameterList();

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
                    DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()),
                    TypeParameters: typeParameters.Count > 0 ? typeParameters : null);
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
                    ? ParseUnionVariantFields(typeParameters)
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
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()),
                TypeParameters: typeParameters.Count > 0 ? typeParameters : null);
        }

        /// <summary>
        /// Parses a union variant's payload. Existing named fields remain function-parameter
        /// shaped (<c>Some(value)</c> / <c>Lit(value: double)</c>), while an unmistakable type
        /// in the field position creates a tuple-style field named <c>Item1</c>, <c>Item2</c>,
        /// and so on (<c>Ok(T)</c>, <c>Pair(string, int)</c>).
        /// </summary>
        private IReadOnlyList<FunctionParameterSyntax> ParseUnionVariantFields(
            IReadOnlyList<string> typeParameters)
        {
            var openParen = NextToken();
            var fields = new List<FunctionParameterSyntax>();

            while (Current.Kind is not SyntaxTokenKind.EndOfFile and not SyntaxTokenKind.CloseParen)
            {
                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.unexpected_union_field_separator",
                        Title: "A union variant field is required between commas.",
                        Span: Current.Span,
                        Label: "remove this comma or add a field here"));
                    NextToken();
                    continue;
                }

                if (LooksLikePositionalUnionFieldType(typeParameters))
                {
                    var start = Current.Span.Start;
                    var typeName = ParseTypeName("union variant field type");
                    var end = _tokens[Math.Max(0, _position - 1)].Span.End;
                    fields.Add(new FunctionParameterSyntax(
                        $"Item{fields.Count + 1}",
                        typeName,
                        IsOptional: false,
                        IsRest: false,
                        DefaultValue: null,
                        Span: TextSpan.FromBounds(start, end)));
                }
                else
                {
                    fields.Add(ParseFunctionParameter());
                }

                if (Current.Kind == SyntaxTokenKind.Comma)
                {
                    NextToken();
                    continue;
                }

                if (Current.Kind is not SyntaxTokenKind.CloseParen and not SyntaxTokenKind.EndOfFile)
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.missing_union_field_separator",
                        Title: "Union variant fields must be separated by ','.",
                        Span: Current.Span,
                        Label: "insert ',' between union variant fields"));
                }
            }

            if (Current.Kind == SyntaxTokenKind.CloseParen)
            {
                NextToken();
            }
            else
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_parenthesis",
                    Title: "A closing ')' is required here.",
                    Span: openParen.Span,
                    Label: "this union variant field list never closes"));
            }

            return fields;
        }

        private bool LooksLikePositionalUnionFieldType(IReadOnlyList<string> typeParameters)
        {
            if (Current.Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            // A colon makes this the compatible named form: `value: Type`.
            if (Current.Text.Contains(':', StringComparison.Ordinal) ||
                (Peek(1).Kind == SyntaxTokenKind.Bareword &&
                 (Peek(1).Text == ":" || Peek(1).Text.StartsWith(":", StringComparison.Ordinal))))
            {
                return false;
            }

            var candidate = Current.Text.TrimEnd('?');
            if (typeParameters.Contains(candidate, StringComparer.Ordinal))
            {
                return true;
            }

            if (Peek(1).Kind == SyntaxTokenKind.LessThan ||
                candidate.Contains('.', StringComparison.Ordinal) ||
                (candidate.Length > 0 && char.IsUpper(candidate[0])))
            {
                return true;
            }

            return candidate.ToLowerInvariant() is
                "any" or "bool" or "boolean" or "byte" or "sbyte" or
                "short" or "ushort" or "int" or "uint" or "long" or "ulong" or
                "float" or "double" or "decimal" or "half" or "string" or "str" or
                "char" or "object" or "list" or "array" or "dict" or "set";
        }

        private StatementSyntax ParseModuleDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();

            var isPartial = false;
            if (Current.Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Current.Text, "partial", StringComparison.Ordinal))
            {
                isPartial = true;
                NextToken();
            }

            NextToken(); // module

            // The lexer keeps `Foo.Bar.Baz` as a single bareword. Split into
            // segments so that dotted module names compile to nested modules.
            var nameToken = Current.Kind == SyntaxTokenKind.Bareword
                ? NextToken()
                : ExpectVariableName();
            var segments = nameToken.Text.Split('.');
            var body = ParseRequiredBlock("module");
            var fullSpan = TextSpan.FromBounds(declarationStart, body.Span.End);
            var docComment = DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>());

            // Innermost module owns the body and the doc-comment; intermediate
            // wrappers are partial Default-scope shells that just propagate
            // declarations outward.
            var innerSpan = TextSpan.FromBounds(nameToken.Span.End, body.Span.End);
            StatementSyntax current = new ModuleDefinitionStatementSyntax(
                Name: segments[^1],
                Body: body,
                Modifier: segments.Length == 1 ? modifier : DeclarationModifier.Export,
                Span: segments.Length == 1 ? fullSpan : innerSpan,
                DocComment: docComment,
                IsPartial: isPartial);

            for (var index = segments.Length - 2; index >= 0; index--)
            {
                var wrapperBody = new BlockSyntax(
                    new[] { current },
                    current.Span);
                current = new ModuleDefinitionStatementSyntax(
                    Name: segments[index],
                    Body: wrapperBody,
                    Modifier: index == 0 ? modifier : DeclarationModifier.Export,
                    Span: index == 0 ? fullSpan : current.Span,
                    DocComment: index == 0 ? docComment : null,
                    // Dotted segments imply partial wrapping so multiple files
                    // (or multiple declarations) can contribute siblings under
                    // the same parent without colliding.
                    IsPartial: true);
            }

            return current;
        }

        private StatementSyntax ParseEnumDefinitionStatement(
            IReadOnlyList<SyntaxToken>? docTokens,
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();

            var isFlags = Current.Kind == SyntaxTokenKind.Bareword &&
                          string.Equals(Current.Text, "flags", StringComparison.OrdinalIgnoreCase);
            if (isFlags)
            {
                NextToken(); // flags
            }

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
                return new EnumDefinitionStatementSyntax(enumName, underlyingTypeName, Array.Empty<EnumMemberSyntax>(), modifier, IsFlags: isFlags, TextSpan.FromBounds(declarationStart, nameToken.Span.End),
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

                if (IsAtElementBoundary())
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
                return new EnumDefinitionStatementSyntax(enumName, underlyingTypeName, members, modifier, IsFlags: isFlags, TextSpan.FromBounds(declarationStart, members.Count == 0 ? nameToken.Span.End : members[^1].Span.End),
                    DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
            }

            var closeBrace = NextToken();
            return new EnumDefinitionStatementSyntax(
                enumName,
                underlyingTypeName,
                members,
                modifier,
                isFlags,
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
            var typeParameters = ParseTypeParameterList();

            // Optional `where T: Constraint[, ...]` clauses (between
            // type parameter list and field list).
            List<TypeParameterConstraintSyntax>? typeParameterConstraints = null;
            while (Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "where", StringComparison.Ordinal))
            {
                typeParameterConstraints ??= new List<TypeParameterConstraintSyntax>();
                if (TryParseWhereClause(out var clause))
                {
                    typeParameterConstraints.Add(clause);
                }
                else
                {
                    break;
                }
            }

            if (Current.Kind != SyntaxTokenKind.OpenParen)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_record_fields",
                    Title: "Record definitions require a field list.",
                    Span: Current.Span,
                    Label: $"write '(...)' after record '{nameToken.Text}'"));
                return new RecordDefinitionStatementSyntax(nameToken.Text, Array.Empty<RecordFieldDefinitionSyntax>(), modifier, isSealed, isStrict, isPartial, TextSpan.FromBounds(declarationStart, nameToken.Span.End),
                    DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()),
                    TypeParameters: typeParameters.Count > 0 ? typeParameters : null,
                    TypeParameterConstraints: typeParameterConstraints);
            }

            var fields = ParseRecordDefinitionFields(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon);

            // Also accept `where` clauses after the field list (more
            // natural placement, matches class-style ordering).
            while (Current.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Current.Text, "where", StringComparison.Ordinal))
            {
                typeParameterConstraints ??= new List<TypeParameterConstraintSyntax>();
                if (TryParseWhereClause(out var clause))
                {
                    typeParameterConstraints.Add(clause);
                }
                else
                {
                    break;
                }
            }

            var end = fields.Count == 0 ? nameToken.Span.End : fields[^1].Span.End;
            return new RecordDefinitionStatementSyntax(
                nameToken.Text,
                fields,
                modifier,
                isSealed,
                isStrict,
                isPartial,
                TextSpan.FromBounds(declarationStart, end),
                DocComment: DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()),
                TypeParameters: typeParameters.Count > 0 ? typeParameters : null,
                TypeParameterConstraints: typeParameterConstraints);
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

        /// <summary>
        /// <c>raw callback Name(param: type, …) -&gt; ret [callconv name]</c>
        ///
        /// Reuses the bind-block parameter and return parsers verbatim: a
        /// callback signature is the same grammar as the native function
        /// signature it is passed to, read in the opposite direction.
        /// </summary>
        private StatementSyntax ParseRawCallbackDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();

            NextToken(); // consume 'raw'
            NextToken(); // consume 'callback'

            var nameToken = ExpectVariableName();
            var parameters = ParseNativeBindingParameters();
            var returnTypeName = TryParseReturnTypeAnnotation();

            string? callingConventionName = null;

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
                        Code: "tosh.parser.expected_calling_convention",
                        Title: "A callback needs a calling convention name after 'callconv'.",
                        Span: Current.Span,
                        Label: "write something like 'callconv stdcall'"));
                }
            }

            return new RawCallbackDefinitionStatementSyntax(
                nameToken.Text,
                parameters,
                returnTypeName,
                callingConventionName,
                modifier,
                TextSpan.FromBounds(declarationStart, Math.Max(Peek(-1).Span.End, nameToken.Span.End)),
                DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
        }

        /// <summary>
        /// <c>raw struct Name [pack n] [size n] { name: type[count] = default ... }</c>
        /// </summary>
        private StatementSyntax ParseRawStructDefinitionStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var declarationStart = Current.Span.Start;
            var modifier = ParseDeclarationModifier();

            NextToken(); // consume 'raw'

            var isUnion = string.Equals(Current.Text, "union", StringComparison.Ordinal);
            NextToken(); // consume 'struct' / 'union'

            var nameToken = ExpectVariableName();

            // Optional header clauses, order-free. Both are rare; `size` is an
            // assertion rather than a requirement, and neither belongs in a
            // docs example lest they read as mandatory.
            int? pack = null;
            int? declaredSize = null;

            while (Current.Kind == SyntaxTokenKind.Bareword &&
                   (string.Equals(Current.Text, "pack", StringComparison.Ordinal) ||
                    string.Equals(Current.Text, "size", StringComparison.Ordinal)))
            {
                var clause = Current.Text;
                NextToken();

                if (!TryReadRawStructInteger(out var value))
                {
                    _diagnostics.Add(new SyntaxDiagnostic(
                        Code: "tosh.parser.raw_struct_clause_requires_integer",
                        Title: $"'{clause}' requires a byte count.",
                        Span: Current.Span,
                        Label: $"write an integer after '{clause}'"));
                    break;
                }

                if (string.Equals(clause, "pack", StringComparison.Ordinal)) pack = value;
                else declaredSize = value;
            }

            var fields = new List<RawStructFieldSyntax>();

            if (Current.Kind != SyntaxTokenKind.OpenBrace)
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.expected_raw_struct_body",
                    Title: $"Raw struct '{nameToken.Text}' requires a body.",
                    Span: Current.Span,
                    Label: $"write '{{ ... }}' after '{nameToken.Text}'"));

                return new RawStructDefinitionStatementSyntax(
                    nameToken.Text, fields, modifier, isUnion, pack, declaredSize,
                    TextSpan.FromBounds(declarationStart, nameToken.Span.End),
                    DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
            }

            var openBraceTokenIndex = _position;
            var openBrace = NextToken(); // consume '{'
            using var boundaryOwner = PushBoundaryOwner(openBraceTokenIndex);

            while (Current.Kind != SyntaxTokenKind.CloseBrace &&
                   Current.Kind != SyntaxTokenKind.EndOfFile)
            {
                if (Current.Kind == SyntaxTokenKind.Semicolon)
                {
                    NextToken();
                    continue;
                }

                var field = ParseRawStructField();

                if (field is null)
                {
                    SkipToBlockBoundary();
                    continue;
                }

                fields.Add(field);

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
                        Code: "tosh.parser.missing_raw_struct_field_separator",
                        Title: "Raw struct fields must be separated by a newline or ';'.",
                        Span: Current.Span,
                        Label: "insert a newline or ';' between fields"));
                    SkipToBlockBoundary();
                }
            }

            var end = Current.Span.End;

            if (Current.Kind == SyntaxTokenKind.CloseBrace)
            {
                NextToken();
            }
            else
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.missing_closing_brace",
                    Title: "A closing '}' is required here.",
                    Span: openBrace.Span,
                    Label: $"raw struct '{nameToken.Text}' never closes"));
            }

            return new RawStructDefinitionStatementSyntax(
                nameToken.Text,
                fields,
                modifier,
                isUnion,
                pack,
                declaredSize,
                TextSpan.FromBounds(declarationStart, end),
                DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
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

                    // Read the name the way a *class* property does, rather than with a
                    // bare `ExpectVariableName`. `TOAST-0019`: the lexer glues `X:` into
                    // one bareword, so `prop X: int` arrived here as the name `X:` and was
                    // rejected as an invalid identifier — a trait could not type a required
                    // property at all, while a class two lines away could.
                    var propNameToken = Current.Kind == SyntaxTokenKind.Bareword
                        ? NextToken()
                        : ExpectVariableName();

                    ParseTypedIdentifierToken(
                        propNameToken.Text,
                        out var propName,
                        out var inlineTypeName,
                        out var expectsFollowingTypeName);

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
                    else if (Current.Kind == SyntaxTokenKind.Bareword &&
                             Current.Text.StartsWith(":", StringComparison.Ordinal))
                    {
                        typeName = NextToken().Text[1..];
                    }

                    typeName = ParseTypeNameSuffix(typeName);

                    // Optional default value: prop name = expr
                    PipelineSyntax? defaultValue = null;
                    if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text == "=")
                    {
                        NextToken(); // consume '='
                        defaultValue = ParsePipeline(untilCloseParen: false, untilCloseBrace: true, untilSemicolon: true, allowExpressionStart: true);
                    }

                    properties.Add(new TraitPropertySignatureSyntax(
                        propName,
                        typeName,
                        defaultValue,
                        TextSpan.FromBounds(propStart, propNameToken.Span.End)));
                }
                else if (Current.Kind == SyntaxTokenKind.Bareword && string.Equals(Current.Text, "func", StringComparison.OrdinalIgnoreCase))
                {
                    var methodStart = Current.Span.Start;
                    NextToken(); // consume 'func'
                    var methodName = ExpectVariableName();
                    var parameters = Current.Kind == SyntaxTokenKind.OpenParen
                        ? ParseFunctionParameters()
                        : Array.Empty<FunctionParameterSyntax>();

                    // Optional return type. `->` first, through the same helper every
                    // other declaration uses, so `func render() -> string` means here what
                    // it means on a class (`TOAST-0019`). The `:` form is still accepted:
                    // it was the only spelling traits took, and breaking it would be a
                    // second defect rather than a fix.
                    var returnTypeName = TryParseReturnTypeAnnotation();

                    if (returnTypeName is null &&
                        Current.Kind == SyntaxTokenKind.Bareword && Current.Text == ":")
                    {
                        NextToken(); // consume ':'
                        returnTypeName = ParseTypeName("return type");
                    }

                    // Optional default body, in either form a class method accepts.
                    BlockSyntax? defaultBody = null;
                    if (IsFatArrow(Current))
                    {
                        defaultBody = ParseFunctionArrowBody(methodName.Text, allowExpressionStart: true);
                    }
                    else if (Current.Kind == SyntaxTokenKind.OpenBrace)
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

        private BlockSyntax ParseArrowStatementBlock(string owner)
        {
            var arrowStart = Current.Span.Start;
            ConsumeFatArrow();
            var statement = ParseStatement(stopAtCloseParen: false, stopAtCloseBrace: true, stopAtSemicolon: true);
            return new BlockSyntax([statement], TextSpan.FromBounds(arrowStart, statement.Span.End));
        }

        private BlockSyntax ParseBlock()
        {
            var openBraceTokenIndex = _position;
            var openBrace = NextToken();
            _elementBoundaryOwnerTokenIndices.Push(openBraceTokenIndex);

            try
            {
                var statements = new List<StatementSyntax>();

                while (Current.Kind != SyntaxTokenKind.EndOfFile && Current.Kind != SyntaxTokenKind.CloseBrace)
                {
                    if (Current.Kind == SyntaxTokenKind.Semicolon)
                    {
                        NextToken();
                        continue;
                    }

                    var positionBeforeStatement = _position;
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

                    if (IsCurrentPromotedElementBoundary())
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

                        // As at top level, an already-advanced parser is
                        // positioned at the next construct. Only scan when
                        // ParseStatement made no progress; otherwise a
                        // same-line declaration such as `func a() {} func
                        // b() {}` would be discarded during recovery.
                        if (_position == positionBeforeStatement)
                        {
                            SkipToCurrentStatementBlockBoundary();
                        }
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
            finally
            {
                _elementBoundaryOwnerTokenIndices.Pop();
            }
        }

        private void SkipToCurrentStatementBlockBoundary()
        {
            if (Current.Kind is SyntaxTokenKind.EndOfFile
                or SyntaxTokenKind.Semicolon
                or SyntaxTokenKind.CloseBrace)
            {
                return;
            }

            // The caller invokes this only after ParseStatement made no
            // progress. Consume the offending token before looking for a
            // promoted boundary so recovery itself always advances.
            NextToken();

            while (Current.Kind != SyntaxTokenKind.EndOfFile &&
                   Current.Kind != SyntaxTokenKind.Semicolon &&
                   Current.Kind != SyntaxTokenKind.CloseBrace &&
                   !IsCurrentPromotedElementBoundary())
            {
                NextToken();
            }
        }

        private bool IsCurrentTopLevelLiteStatementStart()
        {
            return _liteTopLevelStatementStartTokenIndices.Contains(_position);
        }

        private static bool IsLiteStatementEndingSeparator(
            LiteSeparatorKind separator)
        {
            return separator is LiteSeparatorKind.LineBreak
                or LiteSeparatorKind.Semicolon
                or LiteSeparatorKind.Background
                or LiteSeparatorKind.EndOfInput;
        }

        private StatementSyntax ParseExtendStatement(IReadOnlyList<SyntaxToken>? docTokens = null)
        {
            var start = Current.Span.Start;
            var modifier = ParseDeclarationModifier();

            NextToken(); // extend

            var nameToken = Current.Kind == SyntaxTokenKind.Bareword ? NextToken() : ExpectVariableName();
            var members = ParseClassBody(nameToken.Text);

            return new ExtendStatementSyntax(
                nameToken.Text,
                members,
                modifier,
                TextSpan.FromBounds(start, Current.Span.Start),
                DocComment.Parse(docTokens ?? Array.Empty<SyntaxToken>()));
        }

        /// <summary>
        /// Wraps a single <c>raw func … from "lib"</c> as a one-function bind
        /// statement, so it takes exactly the same evaluation path as a block.
        /// </summary>
        private static BindStatementSyntax SynthesizeBindStatement(NativeFunctionBindingSyntax binding)
        {
            var target = binding.NativeTarget ?? string.Empty;

            return new BindStatementSyntax(
                GetDefaultNativeBindModuleName(target),
                target,
                [binding],
                binding.Span);
        }

        private static bool IsExplicitBackgroundStatementBoundary(StatementSyntax statement)
        {
            return statement is PipelineStatementSyntax { Pipeline.IsBackground: true };
        }
    }
}
