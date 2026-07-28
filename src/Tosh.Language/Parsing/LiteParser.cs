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
/// A position where a statement may begin. <see cref="BraceDepth"/> counts
/// enclosing ordinary <c>{ ... }</c> pairs.
/// <see cref="OwnerOpenTokenIndex"/> identifies the innermost plain brace
/// that owns the candidate, or is <see langword="null"/> at top level.
/// LiteParser deliberately does not decide whether that brace is a block or
/// a specialized entry list; a parser consumer promotes candidates only
/// after it has established the opener's grammar role.
/// </summary>
public sealed record LiteBoundary(
    int TokenIndex,
    LiteBoundaryKind Kind,
    int BraceDepth,
    int? OwnerOpenTokenIndex = null);

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
/// Delimiters are paired so a <c>|</c> inside a subexpression, list, block,
/// or paired collection literal belongs to that nested construct rather
/// than splitting the enclosing statement. Nested content is left intact;
/// recursion into it is the parser's job.
/// </summary>
public static class LiteParser
{
    public static LiteScript Parse(IReadOnlyList<SyntaxToken> tokens, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        sourceText ??= string.Empty;

        var statements = new List<LiteStatement>();
        var stages = new List<LiteStage>();

        var delimiters = new Stack<SyntaxTokenKind>();
        var statementStart = -1;
        var stageStart = -1;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];

            if (token.Kind == SyntaxTokenKind.EndOfFile)
            {
                break;
            }

            // Test the boundary before pushing the current token. An opener
            // can itself begin a new statement after a line break (`echo x`
            // followed by `[1, 2]`, `(...)`, or a collection literal).
            // Mutating depth first silently joined those statements.
            if (delimiters.Count == 0)
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

            if (TryGetClosingDelimiter(token.Kind, out var closingKind))
            {
                delimiters.Push(closingKind);
            }
            else if (IsClosingDelimiter(token.Kind))
            {
                TryCloseDelimiter(delimiters, token.Kind);
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

    private enum BoundaryFrameRole
    {
        Grouping,
        Literal,
        PlainBrace,
    }

    private readonly record struct BoundaryFrame(
        SyntaxTokenKind ClosingKind,
        BoundaryFrameRole Role,
        int OpenTokenIndex);

    /// <summary>
    /// Every structural boundary candidate, at any ordinary-brace depth.
    /// Ordered frames let the innermost construct decide whether candidates
    /// can exist: a plain brace re-enables candidates inside an outer group
    /// or literal, while a nested group or paired literal suppresses them.
    /// Plain-brace roles remain parser-owned; use
    /// <see cref="PromoteBoundariesForBlock"/> only after the parser has
    /// established that a particular opener begins a block.
    /// </summary>
    public static IReadOnlyList<LiteBoundary> CandidateBoundaries(
        IReadOnlyList<SyntaxToken> tokens,
        string sourceText)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        sourceText ??= string.Empty;

        var boundaries = new List<LiteBoundary>();
        var braceDepth = 0;
        var frames = new Stack<BoundaryFrame>();
        var hasPendingPipelineStage = false;
        int? pendingPipelineOwnerOpenTokenIndex = null;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind == SyntaxTokenKind.EndOfFile)
            {
                break;
            }

            // As in Parse, classify a boundary against the frames that
            // enclose the current token before pushing the token itself.
            // This matters when an opener begins a statement.
            var boundariesEnabled = frames.Count == 0 ||
                                    frames.Peek().Role == BoundaryFrameRole.PlainBrace;
            var ownerOpenTokenIndex =
                frames.TryPeek(out var ownerFrame) &&
                ownerFrame.Role == BoundaryFrameRole.PlainBrace
                    ? ownerFrame.OpenTokenIndex
                    : (int?)null;
            var continuesPipeline =
                hasPendingPipelineStage &&
                pendingPipelineOwnerOpenTokenIndex == ownerOpenTokenIndex;

            if (boundariesEnabled &&
                token.Kind == SyntaxTokenKind.Semicolon &&
                index + 1 < tokens.Count)
            {
                AddCandidate(
                    boundaries,
                    tokens,
                    index + 1,
                    LiteBoundaryKind.Explicit,
                    braceDepth,
                    ownerOpenTokenIndex);
            }
            else if (boundariesEnabled &&
                     !continuesPipeline &&
                     index > 0 &&
                     HasLineBreakBetween(sourceText, tokens[index - 1].Span.End, token.Span.Start) &&
                     (ToshParser.IsExpressionStartToken(token.Kind) ||
                      token.Kind == SyntaxTokenKind.DocComment))
            {
                AddCandidate(
                    boundaries,
                    tokens,
                    index,
                    LiteBoundaryKind.LineBreak,
                    braceDepth,
                    ownerOpenTokenIndex);
            }

            if (boundariesEnabled)
            {
                if (token.Kind == SyntaxTokenKind.Pipe)
                {
                    // A line break after `|` starts the next stage, not a
                    // statement. Tie the pending stage to its exact plain-
                    // brace owner so nested braces retain independent state.
                    hasPendingPipelineStage = true;
                    pendingPipelineOwnerOpenTokenIndex = ownerOpenTokenIndex;
                }
                else if (continuesPipeline || token.Kind == SyntaxTokenKind.Semicolon)
                {
                    hasPendingPipelineStage = false;
                    pendingPipelineOwnerOpenTokenIndex = null;
                }
            }

            if (TryCreateBoundaryFrame(tokens, index, out var frame))
            {
                if (token.Kind == SyntaxTokenKind.OpenBrace)
                {
                    braceDepth++;
                }

                frames.Push(frame);

                if (frame.Role == BoundaryFrameRole.PlainBrace && index + 1 < tokens.Count)
                {
                    AddCandidate(
                        boundaries,
                        tokens,
                        index + 1,
                        LiteBoundaryKind.LineBreak,
                        braceDepth,
                        index);
                }

                continue;
            }

            if (IsClosingDelimiter(token.Kind) &&
                TryCloseBoundaryFrame(frames, token.Kind, out var poppedPlainBraceCount))
            {
                braceDepth = Math.Max(0, braceDepth - poppedPlainBraceCount);
            }
        }

        return boundaries;
    }

    private static bool TryCreateBoundaryFrame(
        IReadOnlyList<SyntaxToken> tokens,
        int tokenIndex,
        out BoundaryFrame frame)
    {
        var token = tokens[tokenIndex];

        if (!TryGetClosingDelimiter(token.Kind, out var closingKind))
        {
            frame = default;
            return false;
        }

        var role = token.Kind switch
        {
            SyntaxTokenKind.OpenBrace => BoundaryFrameRole.PlainBrace,
            SyntaxTokenKind.OpenBraceColon or
            SyntaxTokenKind.OpenBracePipe or
            SyntaxTokenKind.OpenBracePercent => BoundaryFrameRole.Literal,
            _ => BoundaryFrameRole.Grouping,
        };

        frame = new BoundaryFrame(closingKind, role, tokenIndex);
        return true;
    }

    /// <summary>
    /// Promotes only the candidates owned directly by a plain-brace opener
    /// that the caller has already parsed as a real block. Candidates owned
    /// by nested braces are intentionally excluded and must be promoted
    /// independently when their own opener is proven to be a block.
    /// </summary>
    public static IReadOnlyList<LiteBoundary> PromoteBoundariesForBlock(
        IReadOnlyList<LiteBoundary> candidates,
        int blockOpenTokenIndex)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfNegative(blockOpenTokenIndex);

        return candidates
            .Where(boundary => boundary.OwnerOpenTokenIndex == blockOpenTokenIndex)
            .ToArray();
    }

    private static bool TryCloseDelimiter(
        Stack<SyntaxTokenKind> delimiters,
        SyntaxTokenKind closingKind)
    {
        // Prefer the nearest exact frame anywhere in the stack. A closer
        // often arrives after an inner malformed group: `([1)` must unwind
        // the unmatched `[` and close its exact `(`, and `|}` must close an
        // outer record even when an inner plain block forgot `}`.
        var hasExactMatch = delimiters.Contains(closingKind);

        // With no exact match, a brace-family closer remains a recovery
        // closer for the nearest brace-family frame. This preserves the
        // parser's acceptance of plain `}` after an unterminated paired
        // literal without letting that malformed frame leak through EOF.
        if (!hasExactMatch &&
            (!IsBraceClosingDelimiter(closingKind) ||
             !delimiters.Any(IsBraceClosingDelimiter)))
        {
            return false;
        }

        while (delimiters.Count > 0)
        {
            var expected = delimiters.Pop();
            if (hasExactMatch
                    ? expected == closingKind
                    : IsBraceClosingDelimiter(expected))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCloseBoundaryFrame(
        Stack<BoundaryFrame> frames,
        SyntaxTokenKind closingKind,
        out int poppedPlainBraceCount)
    {
        var hasExactMatch = frames.Any(frame => frame.ClosingKind == closingKind);

        if (!hasExactMatch &&
            (!IsBraceClosingDelimiter(closingKind) ||
             !frames.Any(frame => IsBraceClosingDelimiter(frame.ClosingKind))))
        {
            poppedPlainBraceCount = 0;
            return false;
        }

        poppedPlainBraceCount = 0;

        while (frames.Count > 0)
        {
            var frame = frames.Pop();

            if (frame.Role == BoundaryFrameRole.PlainBrace)
            {
                poppedPlainBraceCount++;
            }

            if (hasExactMatch
                    ? frame.ClosingKind == closingKind
                    : IsBraceClosingDelimiter(frame.ClosingKind))
            {
                return true;
            }
        }

        poppedPlainBraceCount = 0;
        return false;
    }

    private static bool TryGetClosingDelimiter(
        SyntaxTokenKind openingKind,
        out SyntaxTokenKind closingKind)
    {
        closingKind = openingKind switch
        {
            SyntaxTokenKind.OpenParen => SyntaxTokenKind.CloseParen,
            SyntaxTokenKind.DollarOpenParen => SyntaxTokenKind.CloseParen,
            SyntaxTokenKind.LessThanOpenParen => SyntaxTokenKind.CloseParen,
            SyntaxTokenKind.OpenBracket => SyntaxTokenKind.CloseBracket,
            SyntaxTokenKind.OpenBrace => SyntaxTokenKind.CloseBrace,
            SyntaxTokenKind.OpenBraceColon => SyntaxTokenKind.ColonCloseBrace,
            SyntaxTokenKind.OpenBracePipe => SyntaxTokenKind.PipeCloseBrace,
            SyntaxTokenKind.OpenBracePercent => SyntaxTokenKind.PercentCloseBrace,
            _ => SyntaxTokenKind.EndOfFile,
        };

        return closingKind != SyntaxTokenKind.EndOfFile;
    }

    private static bool IsClosingDelimiter(SyntaxTokenKind kind) => kind
        is SyntaxTokenKind.CloseParen
        or SyntaxTokenKind.CloseBrace
        or SyntaxTokenKind.CloseBracket
        or SyntaxTokenKind.ColonCloseBrace
        or SyntaxTokenKind.PipeCloseBrace
        or SyntaxTokenKind.PercentCloseBrace;

    private static bool IsBraceClosingDelimiter(SyntaxTokenKind kind) => kind
        is SyntaxTokenKind.CloseBrace
        or SyntaxTokenKind.ColonCloseBrace
        or SyntaxTokenKind.PipeCloseBrace
        or SyntaxTokenKind.PercentCloseBrace;

    private static void AddCandidate(
        List<LiteBoundary> boundaries,
        IReadOnlyList<SyntaxToken> tokens,
        int index,
        LiteBoundaryKind kind,
        int braceDepth,
        int? ownerOpenTokenIndex)
    {
        if (index >= tokens.Count)
        {
            return;
        }

        var token = tokens[index];
        if (token.Kind == SyntaxTokenKind.EndOfFile ||
            token.Kind == SyntaxTokenKind.Semicolon ||
            IsClosingDelimiter(token.Kind))
        {
            return;
        }

        if (boundaries.Count > 0 && boundaries[^1].TokenIndex == index)
        {
            return;
        }

        boundaries.Add(new LiteBoundary(
            index,
            kind,
            braceDepth,
            ownerOpenTokenIndex));
    }
}
