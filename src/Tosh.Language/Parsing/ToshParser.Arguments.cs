using System.Globalization;
using System.Text;
using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public static partial class ToshParser
{
    /// <summary>
    /// Argument parsing: the primary-argument dispatcher, every literal form, command
    /// options and named arguments, splats, process substitution, and parameter lists.
    ///
    /// Moved out of ToshParser.cs by `TOAST-0005`. Every member moved **verbatim**.
    ///
    /// `ParseArgument` is where the argument grammar and the expression grammar meet,
    /// and the boundary is subtler than it looks: `TS-P2-76` was caused by `..` being
    /// consumed here at *primary* level, which is why it outranked every arithmetic
    /// operator until it was given a precedence level of its own. The fix left argument
    /// position with the old handling — `echo 1..3` has no surrounding expression
    /// grammar to place an operator in — so `ParseArgument` carries an `allowRange`
    /// opt-out that the operator chain passes as false. The two files are a pair.
    /// </summary>
    private sealed partial class InternalParser
    {

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

                    var typeToken = NextToken();
                    builder.Append(Current.Kind == SyntaxTokenKind.LessThan
                        ? typeToken.Text
                        : ParseTypeNameSuffix(typeToken.Text));
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
                    var priorDepth = depth;
                    depth -= 2;
                    if (depth <= 0)
                    {
                        // One closer belongs to the nested type argument and the other to
                        // this list: `Outer<Inner<T>>` must retain `Inner<T>` as the argument.
                        if (priorDepth == 2) current.Append('>');
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
                    var typeToken = NextToken();
                    var inner = Current.Kind == SyntaxTokenKind.LessThan
                        ? typeToken.Text
                        : ParseTypeNameSuffix(typeToken.Text) ?? string.Empty;
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
            // `TOAST-0024`. `ParseBitwiseOrExpression`, not `ParseAdditiveExpression`: the
            // range's *left* operand comes from the former — `ParseRangeExpression` calls
            // it — so parsing the right one at the additive level made the two sides
            // disagree about their own precedence. `1 bor 2 .. 4` parsed and
            // `1 .. 2 bor 4` did not, reporting an unclosed expression at the `bor`.
            //
            // The chain places `..` immediately looser than `bor`, so the operands belong
            // at exactly that level. The argument form is untouched: in a command's
            // arguments a range operand is primary-only, which is what keeps `seq 1..5`
            // from swallowing what follows.
            ArgumentSyntax? ParseOperand() => operandsAreExpressions
                ? ParseBitwiseOrExpression(Current.Span.Start, implicitCurrentItem)
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

        private ArgumentSyntax ParseVariableReferenceArgument()
        {
            var variableToken = NextToken();

            // `TOAST-0090`. `::` reaches into a type and a variable holds a value, so `$p::X` is
            // the operator confusion this item exists to make visible. Diagnosed rather than
            // resolved as `$p.X`: without this the whole token missed every member-access route
            // and came out the far side as the literal string "$p.X", which is worse than an
            // error because it looks like it worked.
            if (StaticPathSyntax.UsesPathOperator(variableToken.Text))
            {
                _diagnostics.Add(new SyntaxDiagnostic(
                    Code: "tosh.parser.path_operator_on_value",
                    Title: "'::' reaches into a type, not into a value.",
                    Span: variableToken.Span,
                    Label: "use '.' to read a member of a value",
                    Help: $"try '{variableToken.Text.Replace(StaticPathSyntax.PathOperator, ".", StringComparison.Ordinal)}'."));
            }

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

            // `...` alone, with its target in the tokens that follow — `...[1, 2]`.
            if (splatToken.Text.Length == 3)
            {
                var target = ParseArgument(implicitCurrentItem: false);

                return new SplatArgumentSyntax(
                    target ?? new BarewordArgumentSyntax(string.Empty, splatToken.Span),
                    splatToken.Span);
            }

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
                StaticPathSyntax.Canonicalize(methodToken.Text),
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
                    case SyntaxTokenKind.GreaterThanGreaterThan:
                        depth -= 2;
                        if (depth == 0) return Peek(offset + 1).Kind == SyntaxTokenKind.OpenParen;
                        if (depth < 0) return false;
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
            return new StaticMemberAccessArgumentSyntax(
                StaticPathSyntax.Canonicalize(token.Text),
                token.Span,
                StaticPathSyntax.UsesPathOperator(token.Text));
        }

        private ArgumentSyntax ParseImplicitCurrentItemArgument()
        {
            var memberToken = NextToken();
            ArgumentSyntax expression = new VariableReferenceArgumentSyntax("_", memberToken.Span);
            var chain = ApplyQualifiedMemberChain(expression, memberToken.Text, memberToken.Span, implicitCurrentItem: true);

            // Only the head of the chain is marked, and only when it is a call. `$_.Deep.f()`
            // is a member chain the reader wrote; `f($_)` is the one the parser invented a
            // receiver for, and the only one that may fall back to a function (`TOAST-0001`).
            return chain is MethodCallArgumentSyntax call && ReferenceEquals(call.Target, expression)
                ? call with { ImplicitCurrentItem = true }
                : chain;
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
            // `TOAST-0090`. `new Outer::Inner()` names a nested type by the path operator, and a
            // type name is exactly the place it belongs. Canonicalised here so the resolver sees
            // the dotted form it already understands.
            var bareTypeName = StaticPathSyntax.Canonicalize(typeToken.Text);
            string typeName = bareTypeName;
            IReadOnlyList<string>? typeArguments = null;

            if (Current.Kind == SyntaxTokenKind.LessThan)
            {
                var (display, args, hasAngles) = ParseGenericTypeArgumentsStructured();
                typeName = bareTypeName + display;
                if (hasAngles) typeArguments = args;
            }

            // `TOAST-0091`. `new Villager {| Name = "Steve" |}` — a typed record literal with no
            // constructor arguments. Spelled with `new` rather than the bare `Villager {| … |}`
            // the item first proposed, because the bare form is grammatically identical to a
            // command invocation passing a record (`f {| a = 7 |}` already works) and telling
            // them apart would need a type table in the parser.
            if (Current.Kind == SyntaxTokenKind.OpenBracePipe)
            {
                var bareInitializer = (RecordLiteralArgumentSyntax)ParseRecordLiteralArgument(implicitCurrentItem);
                return new NewObjectArgumentSyntax(
                    typeName,
                    Array.Empty<ArgumentSyntax>(),
                    TextSpan.FromBounds(newToken.Span.Start, bareInitializer.Span.End),
                    bareTypeName,
                    typeArguments,
                    bareInitializer);
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

                // `TS-P2-104`. A constructor spreads exactly as a method does. This loop is
                // the constructor's own, and it had no splat branch — the same "two walkers,
                // one of which knows the language has spreading" shape the item's first half
                // fixed in the evaluator, reappearing in the parser. Without it `...$pair`
                // reached the constructor as the *literal string* `"...$pair"`.
                if (LooksLikeSplatArgument())
                {
                    arguments.Add(ParseSplatArgument());
                }
                // `TS-P2-21`. A constructor takes named arguments exactly as a method does.
                else if (TryParseNamedArgument(implicitCurrentItem, out var namedConstructorArgument))
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

            // `TOAST-0091`. `new Villager("a", 1) {| Name = "Steve" |}` — the constructor runs
            // with its arguments, then the literal's remaining fields are assigned to the value
            // it produced.
            RecordLiteralArgumentSyntax? initializer = null;
            var constructionEnd = constructorCloseParen.Span.End;

            if (Current.Kind == SyntaxTokenKind.OpenBracePipe)
            {
                initializer = (RecordLiteralArgumentSyntax)ParseRecordLiteralArgument(implicitCurrentItem);
                constructionEnd = initializer.Span.End;
            }

            return new NewObjectArgumentSyntax(
                typeName,
                arguments,
                TextSpan.FromBounds(newToken.Span.Start, constructionEnd),
                bareTypeName,
                typeArguments,
                initializer);
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

        private static bool IsPositionalParameter(string name)
        {
            return name.Length > 0 && name[0] != '0' && name.All(char.IsDigit);
        }
    }
}
