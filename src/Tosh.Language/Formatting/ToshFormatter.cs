using System.Text;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Language.Formatting;

/// <summary>
/// Deterministic source-code formatter for tosh files.
///
/// Phase 1: re-renders top-level structure (statement separators,
/// indentation, blank lines, brace placement, keyword spacing) and
/// uses original source slices for inner expressions and unsupported
/// statement kinds. This keeps output guaranteed-valid (it always
/// round-trips through the parser) while still normalising the
/// structural surface.
///
/// Style:
///   - 4-space indent
///   - Single blank line between top-level declarations
///   - Opening braces on the same line as the keyword
///   - No semicolons at end-of-line (newlines as separators)
/// </summary>
public sealed class ToshFormatter
{
    private const string Indent = "    ";
    private const int IndentSize = 4;

    private readonly string _source;
    private readonly StringBuilder _output = new();
    private readonly IReadOnlyList<LineComment> _comments;
    private int _depth;
    /// <summary>
    /// Index into <see cref="_comments"/> of the next comment yet to be emitted.
    /// Comments are flushed in source order before the statement that
    /// immediately follows them.
    /// </summary>
    private int _nextCommentIndex;
    /// <summary>
    /// True after we just emitted a top-level declaration that wants a
    /// blank-line gap before the next declaration (functions, classes,
    /// enums, etc.). Reset by <see cref="WriteLine"/>.
    /// </summary>
    private bool _wantBlankLineBefore;

    private ToshFormatter(string source, IReadOnlyList<LineComment> comments)
    {
        _source = source;
        _comments = comments;
    }

    /// <summary>Result of a format run.</summary>
    public sealed record FormatResult(
        string FormattedText,
        IReadOnlyList<SyntaxDiagnostic> ParseDiagnostics)
    {
        /// <summary>True when the parser produced no syntax errors.</summary>
        public bool IsSyntacticallyValid => ParseDiagnostics.Count == 0;
    }

    /// <summary>
    /// Format the given tosh source. If the source contains parse errors,
    /// the original text is returned unchanged and the diagnostics are
    /// surfaced on the result so callers can decide whether to surface or
    /// suppress them.
    /// </summary>
    public static FormatResult Format(string source, string sourceName = "<input>")
    {
        var parsed = ToshParser.Parse(source, sourceName);
        if (parsed.Diagnostics.Count > 0)
        {
            return new FormatResult(source, parsed.Diagnostics);
        }

        // The formatter does not yet round-trip ## doc-comments — they
        // are lexed as DocComment tokens and attached to AST nodes,
        // but the rendering pipeline only re-emits plain `#`
        // LineComments. Until that is implemented, bail out and
        // return the source unchanged whenever any ## doc-comment is
        // present so we don't silently delete API documentation.
        // (Existing formatter tests do not use ## comments, so they
        // continue to exercise the full formatting path.)
        if (ContainsDocCommentMarker(source))
        {
            return new FormatResult(source, parsed.Diagnostics);
        }

        var formatter = new ToshFormatter(source, parsed.LineComments ?? Array.Empty<LineComment>());
        formatter.WriteScript(parsed.Statement);
        // Flush any trailing comments that come after the last statement.
        formatter.FlushCommentsBefore(int.MaxValue);
        var text = formatter._output.ToString();

        // Normalise trailing whitespace and ensure exactly one trailing newline.
        text = NormaliseTrailingNewline(text);
        return new FormatResult(text, parsed.Diagnostics);
    }

    private static string NormaliseTrailingNewline(string text)
    {
        var trimmed = text.TrimEnd('\r', '\n', ' ', '\t');
        return trimmed.Length == 0 ? string.Empty : trimmed + "\n";
    }

    /// <summary>
    /// True when the source contains a <c>##</c> doc-comment line.
    /// Detected by scanning for a <c>##</c> sequence preceded only by
    /// whitespace on its line. We deliberately walk the raw text
    /// (rather than the lexer's LineComment list) because doc-comments
    /// are emitted as DocComment tokens, not LineComments.
    /// </summary>
    private static bool ContainsDocCommentMarker(string source)
    {
        var atLineStart = true;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\n')
            {
                atLineStart = true;
                continue;
            }
            if (c == ' ' || c == '\t' || c == '\r')
            {
                continue;
            }
            if (atLineStart && c == '#' && i + 1 < source.Length && source[i + 1] == '#')
            {
                // Block doc-comments use `##{ ... }##`; line doc-comments
                // are exactly `##` followed by anything other than `{`.
                // Either form means the file carries documentation we
                // must not destroy.
                return true;
            }
            atLineStart = false;
        }
        return false;
    }

    private static bool ContainsDocComment(IReadOnlyList<LineComment>? comments)
    {
        if (comments is null) return false;
        for (var i = 0; i < comments.Count; i++)
        {
            var text = comments[i].Text;
            if (comments[i].IsFullLine && text.Length >= 2 && text[0] == '#' && text[1] == '#')
            {
                return true;
            }
        }
        return false;
    }

    // ── Statements ──────────────────────────────────────────────

    private void WriteScript(StatementSyntax root)
    {
        var statements = root is ScriptStatementSyntax script
            ? script.Statements
            : new[] { root };

        for (var i = 0; i < statements.Count; i++)
        {
            var stmt = statements[i];
            var isTopLevelDecl = IsTopLevelDeclaration(stmt);

            if (i > 0 && (isTopLevelDecl || _wantBlankLineBefore))
            {
                _output.Append('\n');
                _wantBlankLineBefore = false;
            }

            WriteStatement(stmt);

            if (isTopLevelDecl)
            {
                _wantBlankLineBefore = true;
            }
        }
    }

    private static bool IsTopLevelDeclaration(StatementSyntax stmt) => stmt
        is FunctionDefinitionStatementSyntax
        or ClassDefinitionStatementSyntax
        or EnumDefinitionStatementSyntax
        or RecordDefinitionStatementSyntax
        or StructDefinitionStatementSyntax
        or InterfaceDefinitionStatementSyntax
        or TraitDefinitionStatementSyntax
        or UnionDefinitionStatementSyntax
        or ModuleDefinitionStatementSyntax
        or RuneDefinitionStatementSyntax
        or EventDefinitionStatementSyntax;

    private void WriteStatement(StatementSyntax stmt)
    {
        // Flush comments that appear before this statement (e.g. at
        // the top of a block, or between two inner statements).
        FlushCommentsBefore(stmt.Span.Start);

        switch (stmt)
        {
            case VariableDeclarationStatementSyntax varDecl:
                WriteVariableDeclaration(varDecl);
                break;

            case ReturnStatementSyntax ret:
                WriteReturnLike("return", ret.Value, ret.Span);
                break;

            case YieldStatementSyntax yld:
                WriteReturnLike("yield", yld.Value, yld.Span);
                break;

            case BreakStatementSyntax brk:
                WriteIndented("break");
                break;

            case ContinueStatementSyntax cont:
                WriteIndented("continue");
                break;

            case PipelineStatementSyntax pipe:
                WriteIndented(SliceTrimmed(pipe.Span));
                break;

            case IfStatementSyntax ifStmt:
                WriteIf(ifStmt);
                break;

            case ForStatementSyntax forStmt:
                WriteFor(forStmt);
                break;

            case WhileStatementSyntax whileStmt:
                WriteWhile(whileStmt);
                break;

            case TryStatementSyntax tryStmt:
                WriteTry(tryStmt);
                break;

            case SwitchStatementSyntax switchStmt:
                WriteSwitch(switchStmt);
                break;

            case VariableAssignmentStatementSyntax assign:
                WriteSimpleAssignment("$" + assign.Name, assign.Operator, assign.Value);
                break;

            case MemberAssignmentStatementSyntax memAssign:
                WriteSimpleAssignment(SliceTrimmed(memAssign.Target.Span), memAssign.Operator, memAssign.Value);
                break;

            case FunctionDefinitionStatementSyntax fn when !fn.IsCommandWrapper:
                WriteFunctionDefinition(fn);
                break;

            // Anything we don't yet structurally format is emitted as
            // an exact source slice. Output remains correct; just
            // unformatted.
            //
            // Brace-delimited declarations (class/enum/record/struct/
            // interface/trait/union/module/rune) currently report a
            // span ending at their last member, not at the closing
            // brace, so we extend the slice to the matching '}'.
            default:
                if (IsBraceDelimitedDeclaration(stmt))
                {
                    var extendedSpan = ExtendSpanToMatchingBrace(stmt.Span);
                    WriteIndented(SliceTrimmed(extendedSpan));
                    AdvanceCommentsBefore(extendedSpan.End);
                }
                else
                {
                    WriteIndented(SliceTrimmed(stmt.Span));
                }
                break;
        }

        // Pick up any trailing same-line comment that was attached to
        // the source position just past this statement.
        EmitTrailingCommentIfAny(stmt.Span.End);
    }

    private static bool IsBraceDelimitedDeclaration(StatementSyntax stmt) => stmt
        is ClassDefinitionStatementSyntax
        or EnumDefinitionStatementSyntax
        or RecordDefinitionStatementSyntax
        or StructDefinitionStatementSyntax
        or InterfaceDefinitionStatementSyntax
        or TraitDefinitionStatementSyntax
        or UnionDefinitionStatementSyntax
        or ModuleDefinitionStatementSyntax
        or RuneDefinitionStatementSyntax;

    /// <summary>
    /// Returns the source slice for <paramref name="span"/> extended
    /// to the next matching '}' if the slice starts with an opener
    /// (i.e., the declaration introduces a brace block whose closer
    /// the parser-reported span omits). Tracks brace depth so nested
    /// blocks are handled correctly. Falls back to <see cref="SliceTrimmed"/>
    /// when no opening brace is found inside the span.
    /// </summary>
    private TextSpan ExtendSpanToMatchingBrace(TextSpan span)
    {
        if (span.Start < 0 || span.Start >= _source.Length) return span;
        var end = Math.Min(span.End, _source.Length);

        // Find the first '{' inside the span; if there isn't one, the
        // declaration has no body (e.g. a class with primary-constructor
        // params and no members) and the span is already correct.
        var firstBrace = _source.IndexOf('{', span.Start, end - span.Start);
        if (firstBrace < 0) return span;

        // Scan forward from the span end, tracking brace depth that the
        // parser already balanced *inside* the span. Start depth at the
        // count of '{' minus '}' seen so far in the span.
        var depth = 0;
        for (var i = span.Start; i < end; i++)
        {
            if (_source[i] == '{') depth++;
            else if (_source[i] == '}') depth--;
        }

        // Walk forward until depth returns to zero. We expect depth >= 1
        // here because the parser-omitted closer is what we're hunting.
        var scan = end;
        while (scan < _source.Length && depth > 0)
        {
            if (_source[scan] == '{') depth++;
            else if (_source[scan] == '}') depth--;
            scan++;
            if (depth == 0) break;
        }

        return TextSpan.FromBounds(span.Start, scan);
    }

    private void WriteVariableDeclaration(VariableDeclarationStatementSyntax decl)
    {
        var sb = new StringBuilder();
        sb.Append(decl.Modifier switch
        {
            DeclarationModifier.Global => "global ",
            DeclarationModifier.Export => "export ",
            DeclarationModifier.Shy => "shy ",
            _ => string.Empty,
        });
        sb.Append(decl.IsConst ? "const " : "var ");
        sb.Append(decl.Name);
        if (!string.IsNullOrEmpty(decl.TypeName))
        {
            sb.Append(": ").Append(decl.TypeName);
        }
        if (decl.Value is not null && decl.Value.Stages.Count > 0)
        {
            var singleExpr = TryGetSingleExpression(decl.Value);
            if (singleExpr is MatchArgumentSyntax matchArg)
            {
                WriteIndentedRaw($"{sb} = ");
                WriteMatchInline(matchArg);
                return;
            }
            if (singleExpr is AnonymousFunctionArgumentSyntax lambdaArg)
            {
                WriteIndentedRaw($"{sb} = ");
                WriteAnonymousFunctionInline(lambdaArg);
                return;
            }
            var valueSpan = TextSpan.FromBounds(
                decl.Value.Stages[0].Span.Start,
                decl.Value.Stages[^1].Span.End);
            sb.Append(" = ").Append(SliceTrimmed(valueSpan));
        }
        else if (decl.Value is not null)
        {
            sb.Append(" = ").Append(SliceTrimmed(decl.Span));
        }
        WriteIndented(sb.ToString());
    }

    private void WriteReturnLike(string keyword, PipelineSyntax? value, TextSpan span)
    {
        if (value is null || value.Stages.Count == 0)
        {
            WriteIndented(keyword);
            return;
        }

        var valueSpan = TextSpan.FromBounds(
            value.Stages[0].Span.Start,
            value.Stages[^1].Span.End);
        WriteIndented($"{keyword} {SliceTrimmed(valueSpan)}");
    }

    private void WriteIf(IfStatementSyntax ifStmt)
    {
        var conditionText = SliceTrimmed(ifStmt.Condition.Span);
        WriteIndented($"if {conditionText} {{");
        _depth++;
        foreach (var inner in ifStmt.ThenBlock.Statements)
        {
            WriteStatement(inner);
        }
        _depth--;

        if (ifStmt.ElseBlock is not null)
        {
            // `else if` chain: elseBlock contains a single IfStatement.
            if (ifStmt.ElseBlock.Statements.Count == 1
                && ifStmt.ElseBlock.Statements[0] is IfStatementSyntax elseIf)
            {
                WriteIndentedRaw("} else ");
                // Recurse without the leading indent of the next line.
                WriteIfInline(elseIf);
                return;
            }

            WriteIndented("} else {");
            _depth++;
            foreach (var inner in ifStmt.ElseBlock.Statements)
            {
                WriteStatement(inner);
            }
            _depth--;
        }

        WriteIndented("}");
    }

    private void WriteIfInline(IfStatementSyntax ifStmt)
    {
        // No leading indent — caller has already placed the cursor.
        var conditionText = SliceTrimmed(ifStmt.Condition.Span);
        _output.Append("if ").Append(conditionText).Append(" {\n");
        _depth++;
        foreach (var inner in ifStmt.ThenBlock.Statements)
        {
            WriteStatement(inner);
        }
        _depth--;

        if (ifStmt.ElseBlock is not null)
        {
            if (ifStmt.ElseBlock.Statements.Count == 1
                && ifStmt.ElseBlock.Statements[0] is IfStatementSyntax elseIf)
            {
                WriteIndentedRaw("} else ");
                WriteIfInline(elseIf);
                return;
            }

            WriteIndented("} else {");
            _depth++;
            foreach (var inner in ifStmt.ElseBlock.Statements)
            {
                WriteStatement(inner);
            }
            _depth--;
        }

        WriteIndented("}");
    }

    private void WriteFor(ForStatementSyntax forStmt)
    {
        // Header is taken as a source slice from after `for` to before
        // the opening `{`. Re-extracting it from the AST would multiply
        // surface area dramatically — the slice is good enough.
        var header = ExtractHeader(forStmt.Span, "for", forStmt.Body.Span);
        WriteIndented($"for {header} {{");
        _depth++;
        foreach (var inner in forStmt.Body.Statements)
        {
            WriteStatement(inner);
        }
        _depth--;
        WriteIndented("}");
    }

    private void WriteTry(TryStatementSyntax tryStmt)
    {
        WriteIndented("try {");
        _depth++;
        foreach (var inner in tryStmt.TryBlock.Statements) WriteStatement(inner);
        _depth--;

        if (tryStmt.CatchClause is { } catchClause)
        {
            var header = string.IsNullOrEmpty(catchClause.VariableName)
                ? "} catch {"
                : $"}} catch ({catchClause.VariableName}) {{";
            WriteIndented(header);
            _depth++;
            foreach (var inner in catchClause.Body.Statements) WriteStatement(inner);
            _depth--;
        }

        if (tryStmt.FinallyBlock is { } finallyBlock)
        {
            WriteIndented("} finally {");
            _depth++;
            foreach (var inner in finallyBlock.Statements) WriteStatement(inner);
            _depth--;
        }

        WriteIndented("}");
    }

    private void WriteSwitch(SwitchStatementSyntax switchStmt)
    {
        var valueText = SliceTrimmed(switchStmt.Value.Span);
        WriteIndented($"switch {valueText} {{");
        _depth++;

        foreach (var c in switchStmt.Cases)
        {
            var pattern = SliceTrimmed(c.MatchExpression.Span);
            var guard = c.Guard is null ? string.Empty : $" if {SliceTrimmed(c.Guard.Span)}";
            WriteIndented($"case {pattern}{guard} {{");
            _depth++;
            foreach (var inner in c.Body.Statements) WriteStatement(inner);
            _depth--;
            WriteIndented("}");
        }

        if (switchStmt.DefaultBlock is { } defaultBlock)
        {
            WriteIndented("default {");
            _depth++;
            foreach (var inner in defaultBlock.Statements) WriteStatement(inner);
            _depth--;
            WriteIndented("}");
        }

        _depth--;
        WriteIndented("}");
    }

    private void WriteSimpleAssignment(string lhs, string op, PipelineSyntax value)
    {
        if (value.Stages.Count == 0)
        {
            WriteIndented($"{lhs} {op}");
            return;
        }
        var singleExpr = TryGetSingleExpression(value);
        if (singleExpr is MatchArgumentSyntax matchArg)
        {
            WriteIndentedRaw($"{lhs} {op} ");
            WriteMatchInline(matchArg);
            return;
        }
        if (singleExpr is AnonymousFunctionArgumentSyntax lambdaArg)
        {
            WriteIndentedRaw($"{lhs} {op} ");
            WriteAnonymousFunctionInline(lambdaArg);
            return;
        }
        var valueSpan = TextSpan.FromBounds(value.Stages[0].Span.Start, value.Stages[^1].Span.End);
        WriteIndented($"{lhs} {op} {SliceTrimmed(valueSpan)}");
    }

    private void WriteWhile(WhileStatementSyntax whileStmt)
    {
        var header = ExtractHeader(whileStmt.Span, "while", whileStmt.Body.Span);
        WriteIndented($"while {header} {{");
        _depth++;
        foreach (var inner in whileStmt.Body.Statements)
        {
            WriteStatement(inner);
        }
        _depth--;
        WriteIndented("}");
    }

    private void WriteFunctionDefinition(FunctionDefinitionStatementSyntax fn)
    {
        var sb = new StringBuilder();
        sb.Append(fn.Modifier switch
        {
            DeclarationModifier.Global => "global ",
            DeclarationModifier.Export => "export ",
            DeclarationModifier.Shy => "shy ",
            _ => string.Empty,
        });
        sb.Append("func ").Append(fn.Name);
        AppendParameterList(sb, fn.Parameters);
        if (!string.IsNullOrEmpty(fn.ReturnTypeName))
        {
            sb.Append(" -> ").Append(fn.ReturnTypeName);
        }

        // Detect arrow-body form via single-statement body whose source
        // slice is preceded by `=>`. If we can't tell cheaply, default to
        // braced form — it's always valid.
        if (TryExtractArrowBody(fn.Body, out var arrowBody))
        {
            sb.Append(" => ").Append(arrowBody);
            WriteIndented(sb.ToString());
            return;
        }

        sb.Append(" {");
        WriteIndented(sb.ToString());
        _depth++;
        foreach (var inner in fn.Body.Statements)
        {
            WriteStatement(inner);
        }
        _depth--;
        WriteIndented("}");
    }

    private static ArgumentSyntax? TryGetSingleExpression(PipelineSyntax pipeline)
    {
        if (pipeline.Stages.Count == 1
            && pipeline.Stages[0] is ExpressionPipelineStageSyntax expr)
            return expr.Expression;
        return null;
    }

    /// <summary>
    /// Renders a <c>match</c> expression starting at the current output position
    /// (no leading indentation). The caller must have already written any prefix
    /// (e.g. <c>"var x = "</c>) via <see cref="WriteIndentedRaw"/>.
    /// </summary>
    private void WriteMatchInline(MatchArgumentSyntax match)
    {
        _output.Append("match ").Append(SliceTrimmed(match.Value.Span)).Append(" {\n");
        _depth++;
        foreach (var arm in match.Arms)
            WriteMatchArm(arm);
        _depth--;
        WriteIndented("}");
    }

    private void WriteMatchArm(MatchArmSyntax arm)
    {
        var pattern = arm.IsWildcard ? "default" : SliceTrimmed(arm.Pattern!.Span);
        var guard = arm.Guard is null ? string.Empty : $" if {SliceTrimmed(arm.Guard.Span)}";
        switch (arm.Body)
        {
            case MatchArmPipelineBodySyntax { Pipeline.Stages.Count: > 0 } pipeBody:
                var valueSpan = TextSpan.FromBounds(
                    pipeBody.Pipeline.Stages[0].Span.Start,
                    pipeBody.Pipeline.Stages[^1].Span.End);
                WriteIndented($"{pattern}{guard} => {SliceTrimmed(valueSpan)}");
                break;
            case MatchArmBlockBodySyntax blockBody:
                WriteIndented($"{pattern}{guard} => {{");
                _depth++;
                foreach (var inner in blockBody.Block.Statements)
                    WriteStatement(inner);
                _depth--;
                WriteIndented("}");
                break;
            default:
                WriteIndented($"{pattern}{guard} => ()");
                break;
        }
    }

    /// <summary>
    /// Renders an anonymous function expression starting at the current output
    /// position (no leading indentation). Arrow form is preserved when detected.
    /// </summary>
    private void WriteAnonymousFunctionInline(AnonymousFunctionArgumentSyntax fn)
    {
        var sb = new StringBuilder();
        sb.Append("func");
        AppendParameterList(sb, fn.Parameters);
        if (!string.IsNullOrEmpty(fn.ReturnTypeName))
            sb.Append(" -> ").Append(fn.ReturnTypeName);

        if (TryExtractArrowBodyForAnonymous(fn, out var arrowBody))
        {
            _output.Append(sb).Append(" => ").Append(arrowBody).Append('\n');
            return;
        }

        _output.Append(sb).Append(" {\n");
        _depth++;
        foreach (var inner in fn.Body.Statements)
            WriteStatement(inner);
        _depth--;
        WriteIndented("}");
    }

    /// <summary>
    /// Arrow-body detection for anonymous functions. Unlike named functions
    /// where <see cref="TryExtractArrowBody"/> works by scanning source before
    /// the body span, anonymous function bodies in arrow form have
    /// <c>Body.Span.Start</c> pointing directly at <c>=&gt;</c> — so we
    /// detect the form by checking that character position directly.
    /// </summary>
    private bool TryExtractArrowBodyForAnonymous(AnonymousFunctionArgumentSyntax fn, out string arrowBody)
    {
        arrowBody = string.Empty;
        if (fn.Body.Statements.Count != 1) return false;

        var bodyStart = fn.Body.Span.Start;
        if (bodyStart < 0 || bodyStart + 1 >= _source.Length) return false;
        if (_source[bodyStart] != '=' || _source[bodyStart + 1] != '>') return false;

        // The single statement's span also starts at `=>`. Strip it.
        var stmtText = SliceTrimmed(fn.Body.Statements[0].Span);
        if (!stmtText.StartsWith("=>")) return false;

        arrowBody = stmtText[2..].TrimStart();
        return true;
    }

    private void AppendParameterList(StringBuilder sb, IReadOnlyList<FunctionParameterSyntax> parameters)
    {
        sb.Append('(');
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            var p = parameters[i];
            sb.Append(p.Name);
            if (p.IsRest) sb.Append("...");
            if (p.IsOptional && p.DefaultValue is null)
            {
                sb.Append('?');
            }
            if (!string.IsNullOrEmpty(p.TypeName))
            {
                sb.Append(": ").Append(p.TypeName);
            }
            if (p.DefaultValue is not null && p.DefaultValue.Stages.Count > 0)
            {
                var span = TextSpan.FromBounds(
                    p.DefaultValue.Stages[0].Span.Start,
                    p.DefaultValue.Stages[^1].Span.End);
                sb.Append(" = ").Append(SliceTrimmed(span));
            }
        }
        sb.Append(')');
    }

    // ── Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Pulls the text between `keyword` and the body's opening brace
    /// from the original source. Used for control-flow headers where
    /// re-rendering the AST is not worth the surface area.
    /// </summary>
    private string ExtractHeader(TextSpan stmtSpan, string keyword, TextSpan bodySpan)
    {
        var keywordEnd = stmtSpan.Start + keyword.Length;
        if (keywordEnd > _source.Length || bodySpan.Start <= keywordEnd)
        {
            return string.Empty;
        }
        var raw = _source.Substring(keywordEnd, bodySpan.Start - keywordEnd);
        return raw.Trim();
    }

    /// <summary>
    /// Detects an arrow-body single-expression function, e.g.
    /// <c>func double(x) =&gt; ($x * 2)</c>. Returns the body text
    /// (without the leading <c>=&gt;</c>) when we can confidently
    /// identify the form, else <c>false</c>.
    /// </summary>
    private bool TryExtractArrowBody(BlockSyntax body, out string arrowBody)
    {
        arrowBody = string.Empty;
        if (body.Statements.Count != 1) return false;

        // Look at the source between the function's parameter list and
        // the body's first statement to see whether `=>` appears.
        var firstStmt = body.Statements[0];
        var bodyStart = body.Span.Start;
        if (bodyStart < 2 || bodyStart > _source.Length) return false;

        var prefix = _source.AsSpan(0, bodyStart);
        // Walk back to find the last "=>" not inside a string. Naive
        // scan is sufficient for the prefix of a well-parsed source.
        var arrowIndex = prefix.LastIndexOf("=>");
        if (arrowIndex < 0) return false;

        // Make sure there is no `{` between the arrow and the first
        // statement — that would indicate a braced body, not an arrow.
        for (var i = arrowIndex + 2; i < firstStmt.Span.Start; i++)
        {
            if (_source[i] == '{') return false;
        }

        arrowBody = SliceTrimmed(firstStmt.Span);
        return true;
    }

    private string SliceTrimmed(TextSpan span)
    {
        if (span.Start < 0 || span.Start >= _source.Length) return string.Empty;
        var end = Math.Min(span.End, _source.Length);
        return _source[span.Start..end].Trim();
    }

    // ── Comment preservation ───────────────────────────────────

    /// <summary>
    /// Emit all pending full-line comments whose source position is
    /// strictly before <paramref name="position"/>. Returns true when
    /// at least one comment was emitted, so callers can suppress an
    /// otherwise-blank separator line.
    ///
    /// Trailing same-line comments are skipped here — they're handled
    /// by <see cref="EmitTrailingCommentIfAny"/> after the statement
    /// they follow.
    /// </summary>
    private bool FlushCommentsBefore(int position)
    {
        var emittedAny = false;
        var lastEmittedLine = -1;
        while (_nextCommentIndex < _comments.Count
               && _comments[_nextCommentIndex].Position < position)
        {
            var c = _comments[_nextCommentIndex];
            if (!c.IsFullLine)
            {
                // Skip trailing comments — owned by the preceding
                // statement, emitted via EmitTrailingCommentIfAny.
                _nextCommentIndex++;
                continue;
            }

            // Preserve a blank-line gap between non-adjacent comment
            // groups so docs blocks stay visually separated.
            if (lastEmittedLine >= 0 && c.Line > lastEmittedLine + 1)
            {
                _output.Append('\n');
            }

            WriteIndented(c.Text);
            lastEmittedLine = c.Line;
            emittedAny = true;
            _nextCommentIndex++;
        }
        return emittedAny;
    }

    /// <summary>
    /// Marks comments before <paramref name="position"/> as consumed.
    /// Used after emitting an exact source slice that already contains
    /// those comments, such as a currently-unformatted class body.
    /// </summary>
    private void AdvanceCommentsBefore(int position)
    {
        while (_nextCommentIndex < _comments.Count
               && _comments[_nextCommentIndex].Position < position)
        {
            _nextCommentIndex++;
        }
    }

    /// <summary>
    /// If a non-full-line comment immediately follows the position
    /// <paramref name="afterEnd"/> on the same line, append it to the
    /// last emitted line as <c>"  # text"</c>. Trailing comments inside
    /// nested blocks are picked up by the deeper statement loops via
    /// the same mechanism.
    /// </summary>
    private void EmitTrailingCommentIfAny(int afterEnd)
    {
        if (_nextCommentIndex >= _comments.Count) return;
        var c = _comments[_nextCommentIndex];
        if (c.IsFullLine || c.Position < afterEnd) return;

        // Comment must sit on the same source line as the position we
        // just emitted (i.e. no newline between afterEnd and c.Position).
        for (var i = afterEnd; i < c.Position && i < _source.Length; i++)
        {
            if (_source[i] == '\n') return;
        }

        // Replace the trailing newline emitted by the previous
        // WriteIndented call with "  # text\n".
        if (_output.Length > 0 && _output[^1] == '\n')
        {
            _output.Length--;
        }
        _output.Append("  ").Append(c.Text).Append('\n');
        _nextCommentIndex++;
    }

    private void WriteIndented(string text)
    {
        _output.Append(string.Concat(Enumerable.Repeat(Indent, _depth)));
        _output.Append(text);
        _output.Append('\n');
    }

    /// <summary>
    /// Emits the indentation and the given text without a trailing
    /// newline. Used to construct lines like `} else if ...` where the
    /// caller continues on the same line.
    /// </summary>
    private void WriteIndentedRaw(string text)
    {
        _output.Append(string.Concat(Enumerable.Repeat(Indent, _depth)));
        _output.Append(text);
    }
}
