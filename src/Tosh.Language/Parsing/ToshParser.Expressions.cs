using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public static partial class ToshParser
{
    /// <summary>
    /// Expression parsing: the precedence cascade, the operator token predicates, and
    /// the top-level operator scans.
    ///
    /// Moved out of ToshParser.cs by `TOAST-0005`. Every member moved **verbatim**.
    ///
    /// **This is the other half of `TOAST-0002`.** Slice 11 collected the fifty-nine
    /// `LooksLike*` predicates; this file holds the set they have to agree with — the
    /// `Is*OperatorToken` predicates, `IsAnyOperatorToken`, and the six
    /// `HasTopLevelOperatorBefore*` scans that ask whether an operator appears before
    /// some boundary.
    ///
    /// `TS-P2-105` is precisely a disagreement between these two groups: `as` was added
    /// to the precedence chain here without being added to every scan, so a bare
    /// `$x as int` stopped parsing as an expression while every case with a second
    /// operator still worked. `TS-P2-116` is the complementary hole — these scans look
    /// for an operator *after* the leading token, and a unary operator **is** the
    /// leading token, so none of them could ever have seen one.
    ///
    /// The precedence cascade itself is deliberately conventional and was measured
    /// against a Pratt rewrite in `TS-P2-11`, which recommended against it: nine levels
    /// from `ParseTernaryExpression` down to `ParseUnaryExpression`, with correct
    /// associativity throughout. The scatter was never here — it is in the agreement
    /// between the predicates and the scans, which is now one file to read.
    /// </summary>
    private sealed partial class InternalParser
    {

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
                    commandName,
                    allowTypeNameArgument: arguments.Count == 0 ||
                                           !CommandExpectsTypeNameFirstArgumentOnly(commandName));

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
                    !IsAtElementBoundary())
                {
                    var messageArgument = ParseArgument(commandName);
                    if (messageArgument is not null)
                    {
                        arguments.Add(messageArgument);
                    }
                }

                if (!IsCurrentItemExpressionCommandBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                    !IsAtElementBoundary())
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
                !IsAtElementBoundary())
            {
                var messageArgument = ParseArgument(commandName);
                if (messageArgument is not null)
                {
                    arguments.Add(messageArgument);
                }
            }

            if (!IsCurrentItemExpressionCommandBoundary(stopAtCloseParen, stopAtCloseBrace, stopAtSemicolon) &&
                !(arguments.Count > 0 && IsAtElementBoundary()))
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
                   LooksLikeRedirectionOperator() ||
                   LooksLikeInputRedirection();
        }

        private static bool CommandAllowsOptionsBeforeCurrentItemExpression(string commandName)
        {
            return string.Equals(commandName, "sort", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "sort-by", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(commandName, "parallel", StringComparison.OrdinalIgnoreCase);
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

        private bool IsComprehensionOperator()
        {
            return Current.Kind == SyntaxTokenKind.LessThanPipe;
        }

        private bool HasTopLevelOperatorBeforeComprehension() =>
            HasTopLevelOperatorBefore(SyntaxTokenKind.LessThanPipe);

        /// <summary>
        /// True when an operator appears at depth zero before <paramref name="terminator"/>
        /// (<c>TS-P2-17</c>).
        /// </summary>
        /// <remarks>
        /// Parameterised over the terminator rather than copied per caller. The dict
        /// comprehension needs the same question asked of <c>=&gt;</c> that the body asks
        /// of <c>&lt;|</c>, and a second forty-line scan is how the two would answer
        /// differently later.
        /// </remarks>
        private bool HasTopLevelOperatorBefore(SyntaxTokenKind terminator)
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
                        if (depth > 0) depth--;
                        else return false;
                        break;

                    default:
                        if (token.Kind == terminator)
                        {
                            if (depth == 0) return false;
                            break;
                        }

                        if (depth == 0 &&
                            (IsTernaryQuestionToken(token) ||
                             IsNullCoalescingOperatorToken(token) ||
                             IsLogicalOrOperatorToken(token) ||
                             IsLogicalAndOperatorToken(token) ||
                             IsComparisonOperatorToken(token) ||
                             IsCastOperatorToken(token) ||
                             IsBitwiseOperatorToken(token) ||
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
                            IsCastOperatorToken(token) ||
                            IsBitwiseOperatorToken(token) ||
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
                             IsCastOperatorToken(token) ||
                             IsBitwiseOperatorToken(token) ||
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

            if (GroupOwnsStageDivision(openParen))
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

            var argumentStart = _position;
            var diagnosticsBefore = _diagnostics.Count;

            expression = ParseArgument();

            if (Current.Kind == SyntaxTokenKind.CloseParen)
            {
                var closeParen = NextToken();
                return expression is null
                    ? new BarewordArgumentSyntax(string.Empty, TextSpan.FromBounds(openParen.Span.Start, closeParen.Span.End))
                    : expression with { Span = TextSpan.FromBounds(openParen.Span.Start, closeParen.Span.End) };
            }

            // `TS-P2-112`. A condition is an expression, and a command call becomes
            // one by being parenthesised — so `if ((is-dir $path))` always worked
            // while `if (is-dir $path)` did not, and the diagnostic blamed the
            // condition for a defect in the call inside it.
            //
            // Reaching here means one argument was read and the `)` is still not
            // next, so the remaining tokens are either a command's arguments or an
            // error. Re-reading the group as a pipeline is the only reading that
            // makes them arguments; if that fails too, the original diagnostic is
            // still the right one and is reported below.
            //
            // The retry is gated on having consumed a bare word, because that is
            // what a command name looks like: `if (true)` and `if (Math.Sign(1))`
            // consume through to the `)` and never arrive here at all.
            if (expression is BarewordArgumentSyntax { Value.Length: > 0 })
            {
                _position = argumentStart;
                _diagnostics.RemoveRange(diagnosticsBefore, _diagnostics.Count - diagnosticsBefore);

                var invocation = ParsePipeline(
                    untilCloseParen: true,
                    untilCloseBrace: false,
                    untilSemicolon: false,
                    allowExpressionStart: true);

                if (Current.Kind == SyntaxTokenKind.CloseParen)
                {
                    var invocationClose = NextToken();
                    return new SubexpressionArgumentSyntax(
                        invocation,
                        TextSpan.FromBounds(openParen.Span.Start, invocationClose.Span.End));
                }
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

            while (IsLogicalOrOperatorToken(Current) && !CurrentBeginsLineAsWordOperator())
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

            while (IsLogicalAndOperatorToken(Current) && !CurrentBeginsLineAsWordOperator())
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

        /// <summary>
        /// The range level: looser than arithmetic, tighter than comparison
        /// (<c>TS-P2-76</c>).
        /// </summary>
        /// <remarks>
        /// `..` used to be consumed by <see cref="ParseArgument"/> at the primary level,
        /// which made it bind tighter than everything — so `1 .. $n + 1`, the natural way
        /// to write a computed bound, failed. Placed here it matches the languages a
        /// reader is likely to know: `1 + 2 .. 5` is `3 .. 5`, and `1 .. 5 == $x`
        /// compares the range.
        /// </remarks>
        private ArgumentSyntax? ParseRangeExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseBitwiseOrExpression(startPosition, implicitCurrentItem);

            if (left is not null && Current.Kind == SyntaxTokenKind.DotDot)
            {
                var range = ParseRangeArgument(left, implicitCurrentItem, operandsAreExpressions: true);
                ValidateLiteralRangeOperands(range);
                return range;
            }

            return left;
        }

        private ArgumentSyntax? ParseComparisonExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseRangeExpression(startPosition, implicitCurrentItem);
            List<ArgumentSyntax>? chainOperands = null;
            List<string>? chainOperators = null;
            List<TextSpan>? chainOperatorSpans = null;
            var chainIsPure = true;
            var chainEnd = startPosition;

            while (IsComparisonOperatorToken(Current) && !CurrentBeginsLineAsWordOperator())
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
                var leftOperand = left ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span);
                var rightOperand = right ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span);

                // TS-P1-22: record the run so `a < b < c` can become one
                // chained comparison. Only relational operators chain;
                // `is`, `in`, `contains` and friends stay
                // left-associative, so a mixed run falls back below.
                if (chainOperands is null)
                {
                    chainOperands = [leftOperand];
                    chainOperators = [];
                    chainOperatorSpans = [];
                }

                chainOperands.Add(rightOperand);
                chainOperators!.Add(normalizedOperator);
                chainOperatorSpans!.Add(operatorToken.Span);
                if (!IsChainableComparisonOperator(normalizedOperator))
                {
                    chainIsPure = false;
                }

                chainEnd = end;

                left = new OperatorArgumentSyntax(
                    leftOperand,
                    normalizedOperator,
                    operatorToken.Span,
                    rightOperand,
                    TextSpan.FromBounds(startPosition, end));
            }

            if (chainIsPure && chainOperators is { Count: >= 2 })
            {
                return new ChainedComparisonArgumentSyntax(
                    chainOperands!,
                    chainOperators,
                    chainOperatorSpans!,
                    TextSpan.FromBounds(startPosition, chainEnd));
            }

            return left;
        }

        /// <summary>
        /// Operators that participate in chained comparison. Membership
        /// and type-test operators are deliberately excluded: `a is b is
        /// c` has no useful chained reading.
        /// </summary>
        private static bool IsChainableComparisonOperator(string normalizedOperator)
            => normalizedOperator is "<" or "<=" or ">" or ">=" or "==" or "!=";

        /// <summary>
        /// The bitwise levels, between additive and range (`TS-P3-14`).
        /// </summary>
        /// <remarks>
        /// <para>
        /// All of them bind <em>tighter than comparison</em>, which is a deliberate
        /// departure from C: there `a &amp; b == c` means `a &amp; (b == c)`, the
        /// language's best-known precedence trap and the reason C programmers
        /// parenthesise out of habit. Here `$flags band Mask == 0` means
        /// `($flags band Mask) == 0` — what the text looks like it says. `as` was
        /// moved for the same reason in `TS-P2-105`.
        /// </para>
        /// <para>
        /// The order among themselves — shifts, then `band`, then `bxor`, then
        /// `bor` — is C's, because that part of C is not a trap and is what anyone
        /// porting a flags expression expects.
        /// </para>
        /// </remarks>
        private ArgumentSyntax? ParseBitwiseOrExpression(int startPosition, bool implicitCurrentItem)
            => ParseBinaryOperatorLevel(
                startPosition,
                implicitCurrentItem,
                IsBitwiseOrOperatorToken,
                ParseBitwiseXorExpression);

        private ArgumentSyntax? ParseBitwiseXorExpression(int startPosition, bool implicitCurrentItem)
            => ParseBinaryOperatorLevel(
                startPosition,
                implicitCurrentItem,
                IsBitwiseXorOperatorToken,
                ParseBitwiseAndExpression);

        private ArgumentSyntax? ParseBitwiseAndExpression(int startPosition, bool implicitCurrentItem)
            => ParseBinaryOperatorLevel(
                startPosition,
                implicitCurrentItem,
                IsBitwiseAndOperatorToken,
                ParseShiftExpression);

        private ArgumentSyntax? ParseShiftExpression(int startPosition, bool implicitCurrentItem)
            => ParseBinaryOperatorLevel(
                startPosition,
                implicitCurrentItem,
                IsShiftOperatorToken,
                ParseAdditiveExpression);

        /// <summary>
        /// One left-associative binary level: parse the tighter side, then fold
        /// while the operator matches.
        /// </summary>
        /// <remarks>
        /// Shared by the four bitwise levels rather than written out four times.
        /// The additive and multiplicative levels keep their own copies — each
        /// carries a condition this does not (`IsSpacedDictCloser`, the `TS-P2-02`
        /// exponentiation note), and flattening them into this would hide those.
        /// </remarks>
        private ArgumentSyntax? ParseBinaryOperatorLevel(
            int startPosition,
            bool implicitCurrentItem,
            Func<SyntaxToken, bool> matches,
            Func<int, bool, ArgumentSyntax?> parseTighter)
        {
            var left = parseTighter(startPosition, implicitCurrentItem);

            while (matches(Current))
            {
                var operatorToken = NextToken();
                var right = parseTighter(startPosition, implicitCurrentItem);
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

        private ArgumentSyntax? ParseAdditiveExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseMultiplicativeExpression(startPosition, implicitCurrentItem);

            while (IsAdditiveOperatorToken(Current) && !CurrentBeginsLineAsWordOperator())
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
            // `TS-P2-02`. Unary sits *above* exponentiation, not below it, so `-2 ** 2`
            // is `-(2 ** 2)` = -4 rather than `(-2) ** 2` = 4 — the reading Python and
            // Ruby give, and the one the item calls the right side of the operator.
            // Exponentiation still takes a unary *right* operand, so `2 ** -1` parses.
            var left = ParseUnaryExpression(startPosition, implicitCurrentItem);

            while (IsMultiplicativeOperatorToken(Current) && !IsSpacedDictCloser(Current) && !CurrentBeginsLineAsWordOperator())
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

        /// <summary>
        /// `x as T`, binding tighter than every binary operator and looser than a
        /// primary term. The type operand is a single primary — never an
        /// arithmetic expression — which is what stops the cast consuming the
        /// operator that follows it (`TS-P2-105`).
        ///
        /// Sitting *below* exponentiation rather than above multiplicative
        /// matters: `x as int ** 2` must still find its `**`, and it only does if
        /// the cast is folded into the exponentiation's left operand.
        /// </summary>
        private ArgumentSyntax? ParseCastExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseArgumentOperand(implicitCurrentItem);

            while (IsCastOperatorToken(Current) && !CurrentBeginsLineAsWordOperator())
            {
                var operatorToken = NextToken();
                var right = ParseArgumentOperand(implicitCurrentItem: false);
                var end = right?.Span.End ?? operatorToken.Span.End;

                left = new OperatorArgumentSyntax(
                    left ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    "as",
                    operatorToken.Span,
                    right ?? new BarewordArgumentSyntax(string.Empty, operatorToken.Span),
                    TextSpan.FromBounds(startPosition, end));
            }

            return left;
        }

        private ArgumentSyntax? ParseExponentiationExpression(int startPosition, bool implicitCurrentItem)
        {
            var left = ParseCastExpression(startPosition, implicitCurrentItem);

            // ** is right-associative: 2 ** 3 ** 2 == 2 ** (3 ** 2) == 512. The right
            // operand is a *unary* expression so `2 ** -1` still parses, which is the
            // same asymmetry Python has.
            if (IsExponentiationOperatorToken(Current))
            {
                var operatorToken = NextToken();
                var right = ParseUnaryExpression(startPosition, implicitCurrentItem);
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

            return ParseExponentiationExpression(startPosition, implicitCurrentItem);
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

        private bool HasTopLevelOperatorBeforeCloseParen() => HasTopLevelOperatorBeforeCloseParen(_position);

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
                             IsCastOperatorToken(token) ||
                             IsBitwiseOperatorToken(token) ||
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

        private bool HasTopLevelOperatorBeforeStageBoundary(
            bool stopAtCloseParen,
            bool stopAtCloseBrace,
            bool stopAtSemicolon)
        {
            var depth = 0;

            for (var index = _position; index < _tokens.Count; index++)
            {
                if (depth == 0)
                {
                    var skipped = SkipAdjacentGenericTypeArguments(index);
                    if (skipped > index) { index = skipped - 1; continue; }
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
                    case SyntaxTokenKind.ColonCloseBrace:
                    case SyntaxTokenKind.PipeCloseBrace:
                    case SyntaxTokenKind.PercentCloseBrace:
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
                             IsCastOperatorToken(token) ||
                             IsBitwiseOperatorToken(token) ||
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
                // Phase 3.3 — skip past adjacent generic type-argument
                // lists (`name<T1, T2>`) so the `<`/`>` inside don't
                // trigger comparison-expression detection.
                if (depth == 0)
                {
                    var skipped = SkipAdjacentGenericTypeArguments(index);
                    if (skipped > index) { index = skipped - 1; continue; }
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
                             IsCastOperatorToken(token) ||
                             IsBitwiseOperatorToken(token) ||
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

        /// <summary>
        /// Whether <see cref="Current"/> is a word operator that *begins a line*, and so
        /// starts a new statement rather than continuing the previous one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// `TS-P2-117`. `var r = true` followed by `not $r` parsed as one expression across
        /// the break, so the binding never happened and the error named `r` as undeclared —
        /// on the line that declared it. `and`, `not`, `bnot` and a leading `+` all did it.
        /// </para>
        /// <para>
        /// The language's continuation rule is that **the line that ends signals it**: a
        /// trailing operator continues (`1 +` then `2` is 3) and so does an unclosed
        /// bracket. A leading operator was continuing too, which is the inconsistency —
        /// and the reason this needed no new rule, only the existing one applied.
        /// </para>
        /// <para>
        /// An unclosed `(` or `[` exempts it, because there the expression genuinely is
        /// still open and a leading operator is ordinary style. Braces are **not** counted:
        /// a block holds statements, so a line inside one begins a statement exactly as a
        /// line outside it does.
        /// </para>
        /// </remarks>
        private bool CurrentBeginsLineAsWordOperator()
        {
            if (Current.Kind != SyntaxTokenKind.Bareword || _position <= 0)
            {
                return false;
            }

            var previous = _tokens[_position - 1];
            var gapStart = Math.Min(previous.Span.End, _sourceText.Length);
            var gapEnd = Math.Min(Current.Span.Start, _sourceText.Length);

            if (gapEnd <= gapStart ||
                _sourceText.AsSpan(gapStart, gapEnd - gapStart).IndexOf('\n') < 0)
            {
                return false;
            }

            var depth = 0;

            for (var index = 0; index < _position; index++)
            {
                switch (_tokens[index].Kind)
                {
                    case SyntaxTokenKind.OpenParen:
                    case SyntaxTokenKind.OpenBracket:
                        depth++;
                        break;
                    case SyntaxTokenKind.CloseParen:
                    case SyntaxTokenKind.CloseBracket:
                        if (depth > 0) depth--;
                        break;
                }
            }

            return depth == 0;
        }

        private bool IsAnyOperatorToken(SyntaxToken token)
        {
            return IsComparisonOperatorToken(token)
                || IsCastOperatorToken(token)
                || IsBitwiseOperatorToken(token)
                || IsAdditiveOperatorToken(token)
                || IsMultiplicativeOperatorToken(token)
                || IsExponentiationOperatorToken(token)
                || IsLogicalAndOperatorToken(token)
                || IsLogicalOrOperatorToken(token)
                || IsUnaryOperatorToken(token)
                || IsTernaryQuestionToken(token)
                || IsTernaryColonToken(token)
                || IsNullCoalescingOperatorToken(token);
        }

        private static bool IsLogicalOrOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.DoublePipe
                || (token.Kind == SyntaxTokenKind.Bareword &&
                    string.Equals(token.Text, "or", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsNullCoalescingOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.QuestionQuestion;
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
                    "==" or "=~" or "in" or "contains" or "starts-with" or "ends-with" or "is" or "not"
                    or "is-not" or "is-in" or "is-not-in" or "not-in" or "has");
        }

        /// <summary>
        /// `TS-P2-105`. `as` is a cast, not a comparison, and it parses at its own
        /// level just above the primary term — so `x as int % 2` is `(x as int) % 2`.
        ///
        /// It used to sit in <see cref="IsComparisonOperatorToken"/>, whose right
        /// operand is a full additive expression, so the cast swallowed the
        /// arithmetic after it and read `int % 2` as the *type*. The diagnostic
        /// that came back named `%` and a `String` operand, pointing nowhere near
        /// the cast.
        ///
        /// `is` deliberately stays at comparison level: it yields a boolean, so
        /// `1 + 2 is int` should test the sum rather than cast-then-add.
        /// </summary>
        private static bool IsCastOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(token.Text, "as", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The bitwise operators are words, not symbols (`TS-P3-14`).
        /// </summary>
        /// <remarks>
        /// `&amp;` is the background operator and the function-reference sigil, and
        /// `|` separates pipeline stages, so neither is available. Word forms match
        /// the family the language already has — `and`, `or`, `not`, `is`, `in`,
        /// `contains` — and all six were confirmed unclaimed: no builtin, no
        /// function, and no occurrence anywhere except the comment in the user's
        /// own library explaining that they did not exist.
        /// </remarks>
        /// <summary>
        /// The bitwise operators are words, not symbols (`TS-P3-14`).
        /// </summary>
        /// <remarks>
        /// <para>
        /// `&amp;` is the background operator and the function-reference sigil, and
        /// `|` separates pipeline stages, so neither is available. Word forms match
        /// the family the language already has — `and`, `or`, `not`, `is`, `in`,
        /// `contains` — and all six were confirmed unclaimed.
        /// </para>
        /// <para>
        /// Written as block bodies with the words inline, matching
        /// `IsComparisonOperatorToken`, because `OperatorSurfaceParityTests` reads
        /// these methods as *text* to check every operator the parser accepts is in
        /// the registry. An expression body hides the literals from it, and a guard
        /// that cannot see a predicate cannot police it.
        /// </para>
        /// <para>
        /// Matched case-sensitively, as `in`, `contains`, `is` and `as` are.
        /// </para>
        /// </remarks>
        private static bool IsShiftOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && token.Text is "shl" or "shr";
        }

        private static bool IsBitwiseAndOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && token.Text is "band";
        }

        private static bool IsBitwiseXorOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && token.Text is "bxor";
        }

        private static bool IsBitwiseOrOperatorToken(SyntaxToken token)
        {
            return token.Kind == SyntaxTokenKind.Bareword && token.Text is "bor";
        }

        /// <summary>
        /// Any of the four bitwise levels, for the scans that enumerate operators.
        /// </summary>
        /// <remarks>
        /// These scans decide "does this look like an expression?", and they are
        /// written out by hand in seven places. `TS-P2-105` is what happens when
        /// one of them is missed: `as` was moved out of the comparison set, six
        /// scans silently lost it, and a cast with no *other* operator beside it
        /// stopped parsing as an expression at all — while every case with a second
        /// operator kept working, so the natural corpus missed it entirely.
        /// </remarks>
        private static bool IsBitwiseOperatorToken(SyntaxToken token)
            => IsShiftOperatorToken(token)
               || IsBitwiseAndOperatorToken(token)
               || IsBitwiseXorOperatorToken(token)
               || IsBitwiseOrOperatorToken(token);

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
                    token.Text is "bnot" ||
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
                    "band" => "band",
                    "bor" => "bor",
                    "bxor" => "bxor",
                    "bnot" => "bnot",
                    "shl" => "shl",
                    "shr" => "shr",
                    "has" => "has",
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
    }
}
