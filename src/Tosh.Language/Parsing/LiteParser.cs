using Tosh.Runtime;

namespace Tosh.Language.Parsing;

/// <summary>
/// One stage of a lite pipeline: a half-open range of token indices.
/// </summary>
public sealed record LiteStage(int StartIndex, int EndIndex, TextSpan Span)
{
    /// <summary>Number of tokens in this stage.</summary>
    public int TokenCount => EndIndex - StartIndex;
}

/// <summary>
/// One lite statement: the stages a top-level <c>|</c> separates.
/// </summary>
public sealed record LiteStatement(IReadOnlyList<LiteStage> Stages, TextSpan Span);

/// <summary>
/// The structural shape of a source: its statements and their stages.
/// </summary>
public sealed record LiteScript(IReadOnlyList<LiteStatement> Statements);

/// <summary>How a candidate boundary was signalled.</summary>
public enum LiteBoundaryKind
{
    /// <summary>An explicit <c>;</c> separator.</summary>
    Explicit,

    /// <summary>A line break followed by a token that can start a statement.</summary>
    LineBreak,
}

/// <summary>
/// A position where a statement may begin. Candidates inside braces are
/// exactly that — candidates — because <c>{</c> is structurally
/// ambiguous: a newline separates statements inside a block body but must
/// not split a multi-line record literal, and the two are
/// indistinguishable without semantics. The consumer, which knows whether
/// it is reading a block or a literal, decides which candidates are real.
/// </summary>
public sealed record LiteBoundary(int TokenIndex, LiteBoundaryKind Kind, int BraceDepth);

/// <summary>
/// A structural pre-pass over the token stream (step 2 of the parser
/// roadmap, groundwork for <c>TS-P2-11</c>), modelled on Nushell's
/// <c>lite_parser</c>.
///
/// It answers only structural questions — where statements end, where
/// pipeline stages divide — and assigns no meaning to any token. That
/// separation is the point: today those questions are answered by
/// heuristics scattered through the recursive-descent parser, each
/// re-deriving the answer with local lookahead. Deciding structure once,
/// with the whole token stream in hand, is what lets those heuristics be
/// retired.
///
/// Bracket depth is tracked so a <c>|</c> inside a subexpression, list,
/// or block belongs to that nested construct rather than splitting the
/// enclosing statement. Nested content is left intact; recursion into it
/// is the parser's job.
/// </summary>
public static class LiteParser
{
    public static LiteScript Parse(IReadOnlyList<SyntaxToken> tokens, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        sourceText ??= string.Empty;

        var statements = new List<LiteStatement>();
        var stages = new List<LiteStage>();

        var depth = 0;
        var statementStart = -1;
        var stageStart = -1;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (token.Kind == SyntaxTokenKind.EndOfFile)
            {
                break;
            }

            if (IsOpeningBracket(token.Kind))
            {
                depth++;
            }
            else if (IsClosingBracket(token.Kind))
            {
                if (depth > 0)
                {
                    depth--;
                }
            }

            // A separator only divides structure at the outermost level.
            if (depth == 0)
            {
                if (token.Kind == SyntaxTokenKind.Semicolon)
                {
                    CloseStage(stages, ref stageStart, index, tokens);
                    CloseStatement(statements, stages, ref statementStart, index, tokens);
                    continue;
                }

                if (token.Kind == SyntaxTokenKind.Pipe)
                {
                    CloseStage(stages, ref stageStart, index, tokens);
                    continue;
                }

                // An implicit boundary: a line break followed by
                // something that can legally begin a statement. Uses the
                // shared expression-start table so this agrees with the
                // parser rather than keeping a second list (TS-P2-06).
                if (stageStart >= 0 &&
                    HasLineBreakBetween(sourceText, tokens[index - 1].Span.End, token.Span.Start) &&
                    (ToshParser.IsExpressionStartToken(token.Kind) ||
                     token.Kind == SyntaxTokenKind.DocComment))
                {
                    CloseStage(stages, ref stageStart, index, tokens);
                    CloseStatement(statements, stages, ref statementStart, index, tokens);
                }
            }

            if (statementStart < 0)
            {
                statementStart = index;
            }

            if (stageStart < 0)
            {
                stageStart = index;
            }
        }

        CloseStage(stages, ref stageStart, tokens.Count, tokens);
        CloseStatement(statements, stages, ref statementStart, tokens.Count, tokens);

        return new LiteScript(statements);
    }

    private static void CloseStage(
        List<LiteStage> stages,
        ref int stageStart,
        int endExclusive,
        IReadOnlyList<SyntaxToken> tokens)
    {
        if (stageStart < 0 || endExclusive <= stageStart)
        {
            stageStart = -1;
            return;
        }

        stages.Add(new LiteStage(
            stageStart,
            endExclusive,
            SpanOf(tokens, stageStart, endExclusive)));
        stageStart = -1;
    }

    private static void CloseStatement(
        List<LiteStatement> statements,
        List<LiteStage> stages,
        ref int statementStart,
        int endExclusive,
        IReadOnlyList<SyntaxToken> tokens)
    {
        if (stages.Count == 0)
        {
            statementStart = -1;
            return;
        }

        var start = statementStart < 0 ? stages[0].StartIndex : statementStart;
        statements.Add(new LiteStatement(
            stages.ToArray(),
            SpanOf(tokens, start, endExclusive)));
        stages.Clear();
        statementStart = -1;
    }

    private static TextSpan SpanOf(IReadOnlyList<SyntaxToken> tokens, int startIndex, int endExclusive)
    {
        if (tokens.Count == 0 || startIndex >= tokens.Count)
        {
            return new TextSpan(0, 0);
        }

        var start = tokens[startIndex].Span.Start;
        var lastIndex = Math.Min(endExclusive, tokens.Count) - 1;
        if (lastIndex < startIndex)
        {
            return new TextSpan(start, 0);
        }

        return TextSpan.FromBounds(start, tokens[lastIndex].Span.End);
    }

    private static bool HasLineBreakBetween(string sourceText, int start, int end)
    {
        if (end <= start || start < 0 || end > sourceText.Length)
        {
            return false;
        }

        return sourceText.AsSpan(start, end - start).IndexOfAny('\n', '\r') >= 0;
    }

    private static bool IsOpeningBracket(SyntaxTokenKind kind) => kind
        is SyntaxTokenKind.OpenParen
        or SyntaxTokenKind.OpenBrace
        or SyntaxTokenKind.OpenBracket
        or SyntaxTokenKind.DollarOpenParen
        or SyntaxTokenKind.LessThanOpenParen;

    /// <summary>
    /// What a <c>{</c> opens.
    /// </summary>
    public enum BraceRole
    {
        /// <summary>A block: line breaks inside separate statements.</summary>
        Block,

        /// <summary>A record, dict, set, or match-arm list: line breaks
        /// separate entries or arms, never statements.</summary>
        Literal,
    }

    /// <summary>
    /// Decides what the <c>{</c> at <paramref name="openBraceIndex"/>
    /// opens, using bounded lookahead (TS-P2-25).
    ///
    /// This is decidable rather than a guess. A record literal begins
    /// <c>{ name = </c>, and no block can start that way: a bare
    /// <c>name = value</c> is not a legal statement, because assignment
    /// requires <c>$name</c>. `{ echo = 5 }` is a record for the same
    /// reason — `echo = 5` is rejected as a statement.
    ///
    /// A top-level <c>=&gt;</c> marks a dict literal or a list of match
    /// arms. The two are not distinguished, and need not be: neither
    /// separates *statements* by line break, which is the only question
    /// the structural pass asks.
    /// </summary>
    public static BraceRole ClassifyBrace(IReadOnlyList<SyntaxToken> tokens, int openBraceIndex)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (openBraceIndex < 0 || openBraceIndex >= tokens.Count)
        {
            return BraceRole.Block;
        }

        var first = openBraceIndex + 1;
        if (first >= tokens.Count)
        {
            return BraceRole.Block;
        }

        // `{ name = ` or `{ "name" = ` — a record key. `==` is a
        // comparison and does not count.
        if (tokens[first].Kind is SyntaxTokenKind.Bareword or SyntaxTokenKind.String &&
            first + 1 < tokens.Count &&
            IsSingleEquals(tokens[first + 1]))
        {
            return BraceRole.Literal;
        }

        // A `=>` at this brace's own level: dict entries or match arms.
        var depth = 0;
        for (var index = first; index < tokens.Count; index++)
        {
            var kind = tokens[index].Kind;

            if (IsOpeningBracket(kind))
            {
                depth++;
                continue;
            }

            if (IsClosingBracket(kind))
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
                continue;
            }

            if (depth == 0 && IsFatArrow(tokens[index]))
            {
                return BraceRole.Literal;
            }
        }

        return BraceRole.Block;
    }

    private static bool IsSingleEquals(SyntaxToken token) =>
        token.Kind == SyntaxTokenKind.Bareword && token.Text == "=";

    private static bool IsFatArrow(SyntaxToken token) =>
        token.Kind == SyntaxTokenKind.FatArrow;

    /// <summary>
    /// Every position where a statement could begin, at any brace depth.
    /// Grouping suppression still applies inside parentheses and
    /// brackets, where a line break continues an expression rather than
    /// ending a statement.
    ///
    /// Brace depth is reported rather than filtered, because a candidate
    /// inside braces is only a real boundary when those braces delimit a
    /// block. Deciding that requires knowing whether the <c>{</c> opened
    /// a block or a record, set, or dict literal, which is a semantic
    /// question the structural pass cannot answer.
    /// </summary>
    public static IReadOnlyList<LiteBoundary> CandidateBoundaries(
        IReadOnlyList<SyntaxToken> tokens,
        string sourceText)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        sourceText ??= string.Empty;

        var boundaries = new List<LiteBoundary>();
        var groupingDepth = 0;
        var braceDepth = 0;
        // What each enclosing brace opened. Line breaks inside a literal
        // separate entries, not statements, so they yield no candidates.
        var braceRoles = new Stack<BraceRole>();

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind == SyntaxTokenKind.EndOfFile)
            {
                break;
            }

            switch (token.Kind)
            {
                case SyntaxTokenKind.OpenParen:
                case SyntaxTokenKind.OpenBracket:
                case SyntaxTokenKind.DollarOpenParen:
                case SyntaxTokenKind.LessThanOpenParen:
                    groupingDepth++;
                    continue;
                case SyntaxTokenKind.CloseParen:
                case SyntaxTokenKind.CloseBracket:
                    if (groupingDepth > 0) groupingDepth--;
                    continue;
                case SyntaxTokenKind.OpenBrace:
                    braceDepth++;
                    // Only a block's contents are statements. A record,
                    // dict, set, or match-arm list separates entries, not
                    // statements, so it contributes no candidates
                    // (TS-P2-25).
                    var role = ClassifyBrace(tokens, index);
                    braceRoles.Push(role);
                    if (groupingDepth == 0 &&
                        index + 1 < tokens.Count &&
                        role == BraceRole.Block)
                    {
                        AddCandidate(boundaries, tokens, index + 1, LiteBoundaryKind.LineBreak, braceDepth);
                    }
                    continue;
                case SyntaxTokenKind.CloseBrace:
                    if (braceDepth > 0) braceDepth--;
                    if (braceRoles.Count > 0) braceRoles.Pop();
                    continue;
            }

            if (groupingDepth > 0)
            {
                continue;
            }

            if (token.Kind == SyntaxTokenKind.Semicolon && index + 1 < tokens.Count)
            {
                AddCandidate(boundaries, tokens, index + 1, LiteBoundaryKind.Explicit, braceDepth);
                continue;
            }

            if (braceRoles.Count > 0 && braceRoles.Peek() == BraceRole.Literal)
            {
                continue;
            }

            if (index > 0 &&
                HasLineBreakBetween(sourceText, tokens[index - 1].Span.End, token.Span.Start) &&
                (ToshParser.IsExpressionStartToken(token.Kind) ||
                 token.Kind == SyntaxTokenKind.DocComment))
            {
                AddCandidate(boundaries, tokens, index, LiteBoundaryKind.LineBreak, braceDepth);
            }
        }

        return boundaries;
    }

    private static void AddCandidate(
        List<LiteBoundary> boundaries,
        IReadOnlyList<SyntaxToken> tokens,
        int index,
        LiteBoundaryKind kind,
        int braceDepth)
    {
        if (index >= tokens.Count)
        {
            return;
        }

        var token = tokens[index];
        if (token.Kind == SyntaxTokenKind.EndOfFile ||
            token.Kind == SyntaxTokenKind.CloseBrace)
        {
            return;
        }

        if (boundaries.Count > 0 && boundaries[^1].TokenIndex == index)
        {
            return;
        }

        boundaries.Add(new LiteBoundary(index, kind, braceDepth));
    }

    private static bool IsClosingBracket(SyntaxTokenKind kind) => kind
        is SyntaxTokenKind.CloseParen
        or SyntaxTokenKind.CloseBrace
        or SyntaxTokenKind.CloseBracket;
}
