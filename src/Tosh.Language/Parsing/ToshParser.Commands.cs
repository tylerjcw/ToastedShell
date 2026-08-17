using System.Text;
using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public static partial class ToshParser
{
    /// <summary>
    /// Commands, pipelines, class members and type names — the forms that make up a
    /// pipeline stage and the members of a class body.
    ///
    /// Moved out of ToshParser.cs by `TOAST-0005`. Every member moved **verbatim**.
    ///
    /// `ParseClassMember` is worth knowing about before editing it. It used to name
    /// every C#-familiar alias in a twenty-two-branch `string.Equals` chain;
    /// `LanguageSurfaceParityTests` grants it a documented exemption for exactly that
    /// reason, and the exemption is one of the things that test checks has not quietly
    /// widened.
    /// </summary>
    private sealed partial class InternalParser
    {

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

        private ArgumentSyntax ParseNameOfCommandStyle()
        {
            var start = Current.Span.Start;
            NextToken(); // consume "name-of"

            var identifierToken = NextToken();
            TryReduceNameOfOperand(identifierToken, out var identifier, out var isVariableReference, out var isMemberChain);

            return new NameOfArgumentSyntax(identifier, isVariableReference, TextSpan.FromBounds(start, identifierToken.Span.End), isMemberChain);
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
    }
}
