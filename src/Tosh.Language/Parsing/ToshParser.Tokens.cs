using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public static partial class ToshParser
{
    /// <summary>
    /// The token layer: predicates over the token stream, lookahead for commas,
    /// comprehensions and boundaries, the `Expect*` helpers that consume-or-diagnose,
    /// and the `Skip*` recovery helpers.
    ///
    /// Moved out of ToshParser.cs by `TOAST-0005`. Every member moved **verbatim**.
    ///
    /// **`IsModifierFollowedByDeclarationKeyword` is one of `TOAST-0002`'s scan sites.**
    /// Adding the six bitwise word operators in `TS-P3-14` meant editing seven
    /// hand-maintained scans, and `export flags enum` still failed to parse because this
    /// one was missed — the bare `flags enum` form worked, which is why the corpus did
    /// not catch it. So the scatter that item describes spans three files now
    /// (`Lookahead`, `Expressions`, and this one) rather than one, and any guard has to
    /// account for that. Recorded here so it is not rediscovered.
    /// </summary>
    private sealed partial class InternalParser
    {

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

        /// <summary>
        /// Recognises a variant pattern — <c>TOAST-0053</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Positional — <c>Ok(v)</c>, <c>Add(l, r)</c> — or by field name —
        /// <c>Lit { value }</c>, <c>Node { kind: "if", body }</c>. Sub-patterns are ordinary
        /// <see cref="ArgumentSyntax"/>, which is what makes nesting free: a nested variant
        /// pattern is one of them, and so is a literal to compare against.
        /// </para>
        /// <para>
        /// The bracket must abut the name. <c>Ok (v)</c> is a command and its argument, which
        /// is what it has always been, and stays that way.
        /// </para>
        /// </remarks>
        private VariantPatternSyntax? TryParseVariantPattern()
        {
            if (Current.Kind != SyntaxTokenKind.Bareword || IsVariableToken(Current)) { return null; }

            var open = Peek(1);

            // The paren must abut: `Ok (v)` is a command and its argument and stays that way.
            // A brace need not, because `Node { kind: "if", body }` is the spelling this form
            // is for, and a bareword followed by a block is not a thing a pattern can be.
            if (open.Kind == SyntaxTokenKind.OpenParen)
            {
                if (open.Span.Start != Current.Span.End) { return null; }
            }
            else if (open.Kind != SyntaxTokenKind.OpenBrace)
            {
                return null;
            }

            var byName = open.Kind == SyntaxTokenKind.OpenBrace;
            var closeKind = byName ? SyntaxTokenKind.CloseBrace : SyntaxTokenKind.CloseParen;

            var start = Current.Span.Start;
            var nameText = Current.Text;
            var save = _position;
            NextToken();
            NextToken();

            var positional = new List<ArgumentSyntax>();
            var named = new List<VariantFieldPatternSyntax>();

            while (Current.Kind != closeKind)
            {
                if (Current.Kind == SyntaxTokenKind.EndOfFile) { _position = save; return null; }

                if (byName)
                {
                    if (Current.Kind != SyntaxTokenKind.Bareword || IsVariableToken(Current))
                    {
                        _position = save;
                        return null;
                    }

                    var fieldToken = NextToken();
                    var fieldName = fieldToken.Text;

                    // The lexer may hand back `kind:` as one bareword or `kind` then `:`,
                    // depending on spacing, so both spellings are accepted here.
                    var attachedColon = fieldName.EndsWith(":", StringComparison.Ordinal);
                    if (attachedColon) { fieldName = fieldName[..^1]; }

                    if (attachedColon || IsColonToken(Current))
                    {
                        if (!attachedColon) { NextToken(); }
                        var inner = ParseSubPattern();
                        if (inner is null) { _position = save; return null; }
                        named.Add(new VariantFieldPatternSyntax(
                            fieldName,
                            inner,
                            TextSpan.FromBounds(fieldToken.Span.Start, inner.Span.End)));
                    }
                    else
                    {
                        // Shorthand: `{ value }` binds `value` to the field of that name.
                        named.Add(new VariantFieldPatternSyntax(
                            fieldName,
                            new BarewordArgumentSyntax(fieldName, fieldToken.Span),
                            fieldToken.Span));
                    }
                }
                else
                {
                    var element = ParseSubPattern();
                    if (element is null) { _position = save; return null; }
                    positional.Add(element);
                }

                if (Current.Kind == SyntaxTokenKind.Comma) { NextToken(); continue; }
                if (Current.Kind == closeKind) { break; }
                _position = save;
                return null;
            }

            var closeToken = NextToken();
            return new VariantPatternSyntax(
                nameText,
                positional,
                named,
                TextSpan.FromBounds(start, closeToken.Span.End));
        }

        /// <summary>
        /// A variable reference. The lexer hands one back as a <see cref="SyntaxTokenKind.Bareword"/>
        /// whose text carries the sigil, so every place that means "a plain name" has to say so.
        /// </summary>
        /// <remarks>
        /// Missing this made `Lit($x)` bind rather than compare, so it matched anything — and
        /// silently, because binding a name that already exists is legal. `Lit(5)` and
        /// `Lit((2 + 3))` compared correctly the whole time, which is what hid it.
        /// </remarks>
        private static bool IsVariableToken(SyntaxToken token)
            => token.Kind == SyntaxTokenKind.Bareword && token.Text.StartsWith('$');

        /// <summary>
        /// A list pattern in pattern position — <c>TOAST-0053</c>.
        /// </summary>
        /// <remarks>
        /// The lexer hands `...rest` back as one bareword carrying the sigil, the same way it
        /// does `$x`, so the rest is found by looking at the token's text rather than its kind.
        /// At most one rest is allowed: two would make the split between front and back
        /// ambiguous, and the second is reported rather than quietly ignored.
        /// </remarks>
        private ListPatternSyntax? TryParseListPattern()
        {
            if (Current.Kind != SyntaxTokenKind.OpenBracket) { return null; }

            var save = _position;
            var start = Current.Span.Start;
            NextToken();

            var before = new List<ArgumentSyntax>();
            var after = new List<ArgumentSyntax>();
            var restName = string.Empty;
            var hasRest = false;

            while (Current.Kind != SyntaxTokenKind.CloseBracket)
            {
                if (Current.Kind == SyntaxTokenKind.EndOfFile) { _position = save; return null; }

                if (Current.Kind == SyntaxTokenKind.Bareword && Current.Text.StartsWith("...", StringComparison.Ordinal))
                {
                    var restToken = NextToken();
                    if (hasRest)
                    {
                        _diagnostics.Add(new SyntaxDiagnostic(
                            Code: "tosh.parser.list_pattern_second_rest",
                            Title: "A list pattern may hold only one '...'.",
                            Span: restToken.Span,
                            Label: "a second rest has no unambiguous split",
                            Help: "bind the middle once, then name the elements around it."));
                        _position = save;
                        return null;
                    }

                    hasRest = true;
                    restName = restToken.Text[3..];
                }
                else
                {
                    var element = ParseSubPattern();
                    if (element is null) { _position = save; return null; }
                    (hasRest ? after : before).Add(element);
                }

                if (Current.Kind == SyntaxTokenKind.Comma) { NextToken(); continue; }
                if (Current.Kind == SyntaxTokenKind.CloseBracket) { break; }
                _position = save;
                return null;
            }

            var closeToken = NextToken();
            return new ListPatternSyntax(
                before, after, restName, hasRest, TextSpan.FromBounds(start, closeToken.Span.End));
        }

        /// <summary>One element inside a variant pattern — a nested pattern, name, or value.</summary>
        private ArgumentSyntax? ParseSubPattern()
        {
            if (TryParseVariantPattern() is { } nested) { return nested; }
            if (TryParseListPattern() is { } list) { return list; }

            // A plain name binds; `$name` is a reference, and has to be evaluated and compared
            // like any other value, so it falls through to `ParseArgument`.
            if (Current.Kind == SyntaxTokenKind.Bareword && !IsVariableToken(Current))
            {
                var token = NextToken();
                return new BarewordArgumentSyntax(token.Text, token.Span);
            }

            return ParseArgument(implicitCurrentItem: false);
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
                    pattern = TryParseVariantPattern()
                              ?? (ArgumentSyntax?)TryParseListPattern()
                              ?? ParseArgument(implicitCurrentItem: implicitCurrentItem)
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

        private bool IsVariableDeclarationTailTerminator(int previousOffset, int currentOffset)
        {
            var current = Peek(currentOffset);
            return IsEqualsToken(current) ||
                   current.Kind is SyntaxTokenKind.EndOfFile or SyntaxTokenKind.Semicolon or SyntaxTokenKind.CloseBrace or SyntaxTokenKind.CloseParen ||
                   HasLineBreakBetween(Peek(previousOffset).Span.End, current.Span.Start);
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
