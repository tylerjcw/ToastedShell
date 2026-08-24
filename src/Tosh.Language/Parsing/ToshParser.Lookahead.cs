using Tosh.Runtime;

namespace Tosh.Language.Parsing;

public static partial class ToshParser
{
    /// <summary>
    /// The lookahead predicates: fifty-nine `LooksLike*` methods that decide, from the
    /// tokens ahead, which statement or expression form is being parsed.
    ///
    /// Moved out of ToshParser.cs by `TOAST-0005`. Every member moved **verbatim**.
    ///
    /// **This file exists to make `TOAST-0002` writable.** These predicates have to
    /// agree with one another by hand, and the board records three separate defects from
    /// them failing to:
    ///
    /// * `TS-P2-105` — `as` was added to the precedence chain but not to every scan that
    ///   asks "does this look like an expression?", so a bare `$x as int` stopped parsing
    ///   as one while every case with a second operator still worked.
    /// * `TS-P2-116` — the unary operators could not open a statement at all. The binary
    ///   ones are found by scans looking for an operator *after* the leading token, and a
    ///   unary operator **is** the leading token, so no scan could ever have seen it.
    ///   `not true` was read as a command name; `var x = not true` bound nothing, printed
    ///   nothing, and exited 0.
    /// * `TS-P3-14` — adding six bitwise word operators meant editing seven separate scan
    ///   sites, and `export flags enum` still failed to parse because one modifier list
    ///   was missed. The bare form worked, which is why the corpus did not catch it.
    ///
    /// Collecting them here fixes none of that. It makes the disagreement visible for the
    /// first time, which is the step a guard has to come after.
    /// </summary>
    private sealed partial class InternalParser
    {

        private bool LooksLikeSubcommandDeclaration()
        {
            var offset = 0;
            while (Peek(offset).Kind == SyntaxTokenKind.Bareword &&
                   IsSubcommandModifierKeyword(Peek(offset).Text))
            {
                offset++;
            }

            if (Peek(offset).Kind != SyntaxTokenKind.Bareword ||
                !IsSubcommandKeyword(Peek(offset).Text) ||
                Peek(offset + 1).Kind != SyntaxTokenKind.Bareword)
            {
                return false;
            }

            // Allow the name token to have a trailing ':' or fused ':modifier' for postfix syntax.
            var nameText = Peek(offset + 1).Text;
            var colonIdx = nameText.IndexOf(':');
            var namePart = colonIdx >= 0 ? nameText[..colonIdx] : nameText;
            if (!IsValidCommandName(namePart))
            {
                return false;
            }

            return Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace ||
                   Peek(offset + 2).Kind == SyntaxTokenKind.OpenParen ||
                   IsFatArrow(Peek(offset + 2)) ||
                   colonIdx >= 0 ||
                   (Peek(offset + 2).Kind == SyntaxTokenKind.Bareword && Peek(offset + 2).Text.StartsWith(':'));
        }

        private bool LooksLikeFunctionReferenceArgument()
        {
            return Current.Kind == SyntaxTokenKind.Ampersand &&
                   Peek(1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidFunctionReferenceName(Peek(1).Text) &&
                   Current.Span.End == Peek(1).Span.Start;
        }

        private bool LooksLikeSpreadElement()
        {
            return Current.Kind == SyntaxTokenKind.Bareword &&
                   Current.Text.StartsWith("...", StringComparison.Ordinal) &&
                   Current.Text.Length > 3;
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

            // Destructuring: var { ... } = ..., var [ ... ] = ..., or var ( ... ) = ...
            //
            // `TS-P2-59`. The parenthesised spelling was the one missing, and it is the one a
            // reader writes first: `(a, b) = …` already assigns to existing variables, and
            // `(1, 2)` is how a tuple is built, so `var (a, b) = (1, 2)` is the obvious way to
            // ask for both at once. Without it the declaration had to be spelled with brackets
            // while the assignment used parentheses.
            var afterVar = Peek(offset + 1);
            if (afterVar.Kind is SyntaxTokenKind.OpenBrace or SyntaxTokenKind.OpenBracket or SyntaxTokenKind.OpenParen)
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
                   (Peek(offset + 2).Kind == SyntaxTokenKind.OpenParen ||
                    Peek(offset + 2).Kind == SyntaxTokenKind.LessThan ||
                    IsFatArrow(Peek(offset + 2)));
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

        /// <summary>
        /// <c>extend TypeName {</c>. Distinct from <c>extends</c>, which is the
        /// inheritance clause — the two are told apart by the whole word, so a class
        /// named <c>extend</c> is the only thing given up (<c>TS-P3-27</c>).
        /// </summary>
        private bool LooksLikeExtendDefinition()
        {
            var offset = GetDeclarationModifierOffset();

            return MatchesKeywordAtOffset(offset, "extend") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text);
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
                    Peek(offset + 2).Kind == SyntaxTokenKind.LessThan ||
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
                   (Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace ||
                    Peek(offset + 2).Kind == SyntaxTokenKind.LessThan);
        }

        private bool LooksLikeUnionDefinition()
        {
            var offset = GetDeclarationModifierOffset();
            return MatchesKeywordAtOffset(offset, "union") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text) &&
                   (Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace ||
                    Peek(offset + 2).Kind == SyntaxTokenKind.LessThan);
        }

        private bool LooksLikeModuleDefinition()
        {
            var offset = GetDeclarationModifierOffset();

            // Skip optional 'partial' modifier (allows partial modules to span files).
            if (Peek(offset).Kind == SyntaxTokenKind.Bareword &&
                string.Equals(Peek(offset).Text, "partial", StringComparison.Ordinal))
            {
                offset++;
            }

            return MatchesKeywordAtOffset(offset, "module") &&
                   Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidQualifiedIdentifier(Peek(offset + 1).Text) &&
                   Peek(offset + 2).Kind == SyntaxTokenKind.OpenBrace;
        }

        private bool LooksLikeEnumDefinition()
        {
            var offset = GetEnumKeywordOffset();
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

            if (!(MatchesKeywordAtOffset(offset, "record") &&
                  Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                  IsValidIdentifier(Peek(offset + 1).Text)))
            {
                return false;
            }

            var cursor = offset + 2;

            // Optional generic type-parameter list <T, U, …>.
            if (Peek(cursor).Kind == SyntaxTokenKind.LessThan)
            {
                var depth = 0;
                while (Peek(cursor).Kind is not SyntaxTokenKind.EndOfFile)
                {
                    if (Peek(cursor).Kind == SyntaxTokenKind.LessThan) depth++;
                    else if (Peek(cursor).Kind == SyntaxTokenKind.GreaterThan)
                    {
                        depth--;
                        if (depth == 0) { cursor++; break; }
                    }
                    cursor++;
                }
            }

            // Optional `where` clauses before the field list.
            while (Peek(cursor).Kind == SyntaxTokenKind.Bareword &&
                   string.Equals(Peek(cursor).Text, "where", StringComparison.Ordinal))
            {
                cursor++;
                while (Peek(cursor).Kind is not SyntaxTokenKind.EndOfFile
                    && Peek(cursor).Kind != SyntaxTokenKind.OpenParen
                    && !(Peek(cursor).Kind == SyntaxTokenKind.Bareword
                         && string.Equals(Peek(cursor).Text, "where", StringComparison.Ordinal)))
                {
                    cursor++;
                }
            }

            return Peek(cursor).Kind == SyntaxTokenKind.OpenParen;
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

        /// <summary>
        /// A <c>raw func</c> is a <c>func</c> with no body: the signature is
        /// followed by <c>from</c>, not by <c>{</c> or <c>=&gt;</c>. Scanning for
        /// <c>from</c> is what separates it from an ordinary method that merely
        /// carries the <c>raw</c> documentation marker.
        /// </summary>
        private bool LooksLikeRawNativeFunction()
        {
            if (!MatchesKeyword(Current, "func")) return false;

            // A signature is short, so a bounded scan is enough and cannot run
            // away on malformed input.
            for (var offset = 1; offset < 64; offset++)
            {
                var token = Peek(offset);

                if (token.Kind is SyntaxTokenKind.EndOfFile
                    or SyntaxTokenKind.OpenBrace
                    or SyntaxTokenKind.Semicolon)
                {
                    return false;
                }

                if (token.Kind != SyntaxTokenKind.Bareword) continue;

                if (string.Equals(token.Text, "from", StringComparison.OrdinalIgnoreCase)) return true;
                if (token.Text.StartsWith("=>", StringComparison.Ordinal)) return false;
            }

            return false;
        }

        /// <summary>
        /// <c>raw struct Name { ... }</c> or <c>raw union Name { ... }</c>.
        /// Only a brace body is accepted — a raw struct has no primary
        /// constructor, because a fourteen-field C struct in parentheses is
        /// unreadable and defeats the one-field-per-line transcription that
        /// makes these declarations match their man page.
        /// </summary>
        private bool LooksLikeRawStructDefinition()
        {
            var offset = GetDeclarationModifierOffset();

            if (!MatchesKeywordAtOffset(offset, "raw")) return false;
            offset++;

            if (!MatchesKeywordAtOffset(offset, "struct") &&
                !MatchesKeywordAtOffset(offset, "union"))
            {
                return false;
            }

            return Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text);
        }

        /// <c>raw callback Name(…) -&gt; ret</c>.
        private bool LooksLikeRawCallbackDefinition()
        {
            var offset = GetDeclarationModifierOffset();

            if (!MatchesKeywordAtOffset(offset, "raw")) return false;
            offset++;

            if (!MatchesKeywordAtOffset(offset, "callback")) return false;

            return Peek(offset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(offset + 1).Text);
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

        private bool LooksLikeTypedVariableDeclaration()
        {
            // Pattern: TypeName identifier =
            // By this point all keyword-led statements (for, while, until, if, var, alias,
            // using, require, func, return, throw, break, continue) have already been checked,
            // so any remaining Bareword Bareword = must be a typed declaration.
            var offset = GetDeclarationModifierOffset();

            // When a declaration modifier keyword was NOT consumed (because it isn't
            // followed by a declaration keyword), don't misinterpret it as a type name:
            // `export FOO = "bar"` is a command, not a typed declaration.
            //
            // `TS-P2-23`. The visibility family comes from `LanguageSurface`, not from a
            // second spelling of it here. `ParseDeclarationModifier` is the other place
            // that decides this, and `LanguageSurfaceParityTests` already asserts the two
            // agree — that guard was only checking one of them while this copy sat a few
            // thousand lines away.
            if (offset == 0 && Current.Kind == SyntaxTokenKind.Bareword &&
                LanguageSurface.Words.TryGetValue(Current.Text, out var visibilityKind) &&
                visibilityKind.HasFlag(LanguageWordKind.VisibilityModifier))
            {
                return false;
            }

            return TryGetTypeNameEndOffset(offset, out var typeNameEndOffset) &&
                   Peek(typeNameEndOffset + 1).Kind == SyntaxTokenKind.Bareword &&
                   IsValidIdentifier(Peek(typeNameEndOffset + 1).Text) &&
                   IsEqualsToken(Peek(typeNameEndOffset + 2));
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

            while (true)
            {
                if (IsPostfixToken(Peek(offset)))
                {
                    hasMemberPath = true;
                    offset++;
                    continue;
                }

                // Allow `[...]` index segments in the LHS chain so
                // expressions like `$x["key"] = value` are recognised as
                // member assignment rather than a predicate expression.
                if (Peek(offset).Kind == SyntaxTokenKind.OpenBracket)
                {
                    var depth = 1;
                    offset++;
                    while (depth > 0)
                    {
                        var tok = Peek(offset);
                        if (tok.Kind == SyntaxTokenKind.EndOfFile) return false;
                        if (tok.Kind == SyntaxTokenKind.OpenBracket) depth++;
                        else if (tok.Kind == SyntaxTokenKind.CloseBracket) depth--;
                        offset++;
                    }
                    hasMemberPath = true;
                    continue;
                }

                break;
            }

            return hasMemberPath && IsAssignmentOperatorToken(Peek(offset));
        }

        /// <summary>
        /// A stage that opens with a unary operator word: `not $ready`,
        /// `bnot $mask`, `- $x`.
        ///
        /// `TS-P2-116`. Without this the leading word is taken as a command name,
        /// so `bnot 5` reported `Command 'bnot' was not found` and — worse —
        /// `var x = not true` bound nothing at all and exited cleanly. The unary
        /// operators worked only inside parentheses, which is why the gap survived:
        /// every test and every probe wrote `echo (not true)`.
        ///
        /// This is the unary half of the trap `TS-P2-105` describes. The binary
        /// operators are found by the `HasTopLevelOperator…` scans, which look for
        /// an operator *after* the leading token; a unary operator is the leading
        /// token, so no scan could see it.
        ///
        /// Requiring an operand is what keeps `-` usable as an ordinary argument:
        /// a lone `-` with nothing after it on the line is a word, not an
        /// expression.
        /// </summary>
        private bool LooksLikeUnaryOperatorExpression()
        {
            if (!IsUnaryOperatorToken(Current))
            {
                return false;
            }

            var next = Peek(1);

            if (HasLineBreakBetween(Current.Span.End, next.Span.Start))
            {
                return false;
            }

            return next.Kind switch
            {
                SyntaxTokenKind.Number or
                SyntaxTokenKind.String or
                SyntaxTokenKind.InterpolatedString or
                SyntaxTokenKind.Boolean or
                SyntaxTokenKind.Null or
                SyntaxTokenKind.OpenParen or
                SyntaxTokenKind.DollarOpenParen or
                SyntaxTokenKind.OpenBracket => true,

                // `not $ready`, and `bnot bnot $x` — a unary operand is itself a
                // unary expression.
                SyntaxTokenKind.Bareword => IsVariableReferenceLikeToken(next) ||
                                            IsUnaryOperatorToken(next),
                _ => false,
            };
        }

        private bool LooksLikeExpressionStage()
        {
            return Current.Kind switch
            {
                SyntaxTokenKind.String or
                SyntaxTokenKind.Number or
                SyntaxTokenKind.Boolean or
                SyntaxTokenKind.Null or
                SyntaxTokenKind.UnitLiteral or
                SyntaxTokenKind.OpenParen or
                SyntaxTokenKind.DollarOpenParen or
                SyntaxTokenKind.LessThanOpenParen or
                SyntaxTokenKind.OpenBracket or
                SyntaxTokenKind.OpenBrace or
                SyntaxTokenKind.OpenBraceColon or
                SyntaxTokenKind.OpenBracePipe or
                SyntaxTokenKind.OpenBracePercent or
                SyntaxTokenKind.InterpolatedString => true,
                SyntaxTokenKind.Ampersand => LooksLikeFunctionReferenceArgument(),
                SyntaxTokenKind.Bareword => IsVariableReferenceLikeToken(Current) ||
                                            LooksLikeAnonymousFunctionExpression() ||
                                            LooksLikeMatchExpression() ||
                                            LooksLikeIfExpression() ||
                                            LooksLikeNameOfExpression() ||
                                            LooksLikeNewObjectExpression() ||
                                            LooksLikeStaticMethodCallExpression() ||
                                            LooksLikeStaticMemberAccessExpression(inCommandPosition: true) ||
                                            LooksLikeIntrinsicLiteralExpression() ||
                                            LooksLikeUnaryOperatorExpression(),
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

        private bool LooksLikePotentialTypeName(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text[0] == '$')
            {
                return false;
            }

            // `TS-P2-120`. A nullable suffix does not change whether a name *is* a type
            // name, and the lexer keeps it inside the bareword — so `int?` arrived here
            // whole and failed `IsValidIdentifier` on the `?`. `Int32?` passed only because
            // the CLR-name heuristic below accepts a capitalised word, which is why the
            // defect looked like it was about built-in aliases specifically: the alias
            // spellings are lowercase and nothing else would take them.
            //
            // The cost was out of all proportion to the cause. This predicate decides
            // whether `var` opens a *declaration*, so a false answer did not report a bad
            // annotation — it reported `Command 'var' is not a registered builtin`, at
            // column 1, naming neither the type nor the annotation.
            if (text.Length > 1 && text[^1] == '?')
            {
                text = text[..^1];
            }

            return IsValidIdentifier(text) ||
                   LooksLikeQualifiedDotNetAccess(text) ||
                   LooksLikePotentialClrTypeName(text);
        }

        private bool LooksLikeStaticMethodCallExpression()
        {
            // `TS-P2-82`. A type-argument list may sit between the name and the parenthesis, so
            // `Array.Empty<int>()` is a static call as much as `Array.Empty()` is. Requiring `(`
            // immediately meant the `<` ended the command and the rest reported
            // `missing_pipeline_separator` — a message about pipelines for a generic call. The
            // instance path already allowed it; only this one did not.
            if (Current.Kind != SyntaxTokenKind.Bareword ||
                (Peek(1).Kind != SyntaxTokenKind.OpenParen && !FollowedByTypeArgumentsThenCall()))
            {
                return false;
            }

            if (IsQualifiedDotNetAccess(Current.Text))
            {
                return true;
            }

            if (DeclaresModuleNamed(Current.Text))
            {
                return false;
            }

            // TS-P2-23: a name this source declares is not a CLR type,
            // whatever its capitalization. Without this, `func Foo(x)`
            // followed by `Foo(1)` was read as a static call on a type
            // named Foo and failed, while the identical lowercase
            // `foo(1)` worked — the decision rested on spelling rather
            // than on the declaration the parser had already seen.
            if (_userFunctionNames.Contains(Current.Text) ||
                (_context.IsKnownCommand(Current.Text) && !_context.IsKnownType(Current.Text)))
            {
                return false;
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

        private bool LooksLikeStaticMemberAccessExpression(bool inCommandPosition = false)
        {
            if (Current.Kind != SyntaxTokenKind.Bareword ||
                !IsQualifiedDotNetAccess(Current.Text))
            {
                return false;
            }

            // TS-P2-16: capitalization alone cannot decide this. The
            // parser has no module table, so `Geo.area 2` looked like a
            // static CLR member access and left the `2` with nowhere to
            // go, while `geo.area 2` dispatched fine. At the start of a
            // stage, a dotted name followed by a value argument on the
            // same line is a command invocation; the engine then
            // resolves it against modules and CLR types alike.
            //
            // The check is confined to command position on purpose. In
            // argument position a following bareword is a *sibling*
            // argument, so `echo Config.version Config.maxRetries` must
            // still read both as static member accesses.
            return !inCommandPosition || !NextTokenStartsCommandArgument();
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

        private bool LooksLikeSplatArgument()
        {
            if (Current.Kind != SyntaxTokenKind.Bareword ||
                !Current.Text.StartsWith("...", StringComparison.Ordinal))
            {
                return false;
            }

            // `...$values` — the target is glued into the same token, which is the
            // realistic spelling and the only one that ever worked.
            if (Current.Text.Length > 3)
            {
                return true;
            }

            // `...[1, 2]` — a collection *literal* breaks the bareword at its opening
            // bracket, so `...` arrives alone and the length test above rejected it
            // (`TS-P2-104`). Narrowed to an opening delimiter on purpose: a bare `...` is
            // also a rest-parameter marker and a native binding's variadic tail, and
            // neither of those is followed by one.
            return Peek(1).Kind is SyntaxTokenKind.OpenBracket
                or SyntaxTokenKind.OpenParen;
        }

        /// <summary>
        /// Whether <paramref name="text"/>'s leading dotted segment names a type,
        /// asked of the host's type table first and of capitalization only when
        /// the table has nothing to say (<c>TS-P2-23</c>).
        /// </summary>
        /// <remarks>
        /// The table is what makes a *lower-case* type work. Capitalization can
        /// never recognise `string.Join`, `int.Parse`, or a `using Alias = …` the
        /// user chose to spell in lower case — which is why `string` was hardcoded
        /// here as a one-name exception. It is not an exception now; it is one
        /// entry in <c>DotNetTypeResolver.BuiltInAliases</c>, alongside every
        /// other lower-case alias that used to fail.
        ///
        /// Casing survives as the fallback because the table is necessarily
        /// partial: the platform type index holds thousands of names and is not
        /// worth materializing per parse, so an unqualified `System.Text.Json`
        /// still resolves the old way. Deleting the fallback outright is safe only
        /// once shape-driven argument parsing removes the need to guess at all.
        /// </remarks>
        private bool LooksLikeQualifiedDotNetAccess(string text)
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

            if (_context.IsKnownType(firstSegment))
            {
                return true;
            }

            return char.IsUpper(firstSegment[0]);
        }

        /// <summary>
        /// Whether an *unqualified* name should be read as a CLR type.
        /// </summary>
        /// <remarks>
        /// Deliberately does not consult the type table, unlike the qualified form
        /// above. The table holds every built-in alias, and those names collide
        /// with things users and hosts legitimately call: `func double(x)` then
        /// `double(5)` is a call to that function, and `map` and `set` are commands
        /// as well as aliases for `Dictionary` and `HashSet`. Consulting the table
        /// here claimed all of them for the type — caught by
        /// `Function_call_single_arg_no_tuple`, which had declared a function named
        /// `double` since long before the table existed.
        ///
        /// A bare name is where a *declaration* should win, and the qualified form
        /// is where the type table belongs: `int.Parse` names a type because of the
        /// dot, not because of the spelling. So casing remains the rule for bare
        /// names, unchanged.
        /// </remarks>
        private bool LooksLikePotentialClrTypeName(string text)
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
    }
}
