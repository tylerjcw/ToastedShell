using System.Runtime.InteropServices;
using System.Text;
using Tosh.Tome.Theme;
using Tosh.Tui.Editing;
using static Tosh.Tome.TreeSitter.TreeSitterNative;

namespace Tosh.Tome.TreeSitter;

/// <summary>
/// Tree-sitter-driven colorizer. Parses the buffer with the grammar
/// registered for the file's extension, then walks the syntax tree and
/// maps each leaf token's node type to one of a small set of style
/// categories (keyword / string / number / comment / function / type /
/// variable / operator / punctuation).
///
/// We deliberately do <em>not</em> parse <c>highlights.scm</c> query
/// files. The query engine is significant code surface and only a
/// handful of grammars ship queries with the Arch packages anyway. The
/// node-type heuristic produces reasonable highlighting for every
/// grammar in the official <c>tree-sitter-grammars</c> set with zero
/// per-language configuration. Per-grammar overrides can be added to
/// <see cref="OverridesByGrammar"/> when a language needs them.
///
/// Like <c>LspBackedColorizer</c>, we reparse lazily on text change and
/// cache per-line span lists between calls.
/// </summary>
internal sealed class TreeSitterColorizer : ISyntaxColorizer, IDisposable
{
    // SGR-open sequences keyed by node-category, resolved through TomeTheme
    // so the active palette (truecolor vs. 256-color) is honoured. Match
    // the LSP colorizer palette for visual consistency across the editor.
    private static Dictionary<string, string> Palette => _palette ??= new(StringComparer.Ordinal)
    {
        ["comment"]     = TomeTheme.Active.Open(Role.Comment),
        ["keyword"]     = TomeTheme.Active.Open(Role.Keyword),
        ["string"]      = TomeTheme.Active.Open(Role.EscapedString),
        ["number"]      = TomeTheme.Active.Open(Role.Number),
        ["variable"]    = TomeTheme.Active.Open(Role.Variable),
        ["function"]    = TomeTheme.Active.Open(Role.FunctionName),
        ["type"]        = TomeTheme.Active.Open(Role.TypeName),
        ["operator"]    = TomeTheme.Active.Open(Role.Operator),
        ["constant"]    = TomeTheme.Active.Open(Role.Constant),
        ["punctuation"] = TomeTheme.Active.Open(Role.Punctuation),
        ["heading"]     = TomeTheme.Active.Open(Role.Heading),
        ["emphasis"]    = TomeTheme.Active.Open(Role.Emphasis),
        ["strong"]      = TomeTheme.Active.Open(Role.Strong),
    };
    private static Dictionary<string, string>? _palette;

    private readonly Func<string> _readText;
    private readonly IntPtr _language;
    private readonly string _grammarName;
    private IntPtr _parser;
    private IntPtr _tree;
    private string? _lastText;
    private List<List<StyledSpan>> _spansByLine = new();
    private bool _disposed;

    public TreeSitterColorizer(IntPtr language, string grammarName, Func<string> readText)
    {
        _language = language;
        _grammarName = grammarName;
        _readText = readText;
        _parser = ts_parser_new();
        if (!ts_parser_set_language(_parser, language))
        {
            TreeSitterDebug.Log($"ts_parser_set_language FAILED for grammar={grammarName}");
            ts_parser_delete(_parser);
            _parser = IntPtr.Zero;
        }
        else
        {
            TreeSitterDebug.Log($"parser ready for grammar={grammarName}");
        }
    }

    public IReadOnlyList<StyledSpan> Colorize(string line, int lineIndex)
    {
        if (_parser == IntPtr.Zero) return Array.Empty<StyledSpan>();
        var text = _readText();
        if (!ReferenceEquals(text, _lastText) && text != _lastText)
        {
            try { Rebuild(text); }
            catch (Exception ex)
            {
                TreeSitterDebug.Log($"Rebuild threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            _lastText = text;
            if (TreeSitterDebug.Enabled)
            {
                var total = 0;
                foreach (var l in _spansByLine) total += l.Count;
                TreeSitterDebug.Log($"{_grammarName}: rebuilt — textLen={text.Length}, lines={_spansByLine.Count}, spans={total}");
            }
        }
        if (lineIndex < 0 || lineIndex >= _spansByLine.Count) return Array.Empty<StyledSpan>();
        return _spansByLine[lineIndex];
    }

    private void Rebuild(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var lineCount = CountLines(text);
        _spansByLine = new List<List<StyledSpan>>(lineCount);
        for (var i = 0; i < lineCount; i++) _spansByLine.Add(new List<StyledSpan>());

        // Byte-offset → (line, col-in-bytes) table. The renderer expects
        // byte-offsets within the line, so we walk the UTF-8 buffer once
        // and remember each line's starting byte offset.
        var lineStarts = new int[lineCount];
        var li = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                li++;
                if (li < lineCount) lineStarts[li] = i + 1;
            }
        }

        // Incremental reparse: when we already have a tree and a known
        // previous text, diff the two and tell tree-sitter what changed
        // so it can reuse subtrees outside the modified range.
        var reuseTree = IntPtr.Zero;
        if (_tree != IntPtr.Zero && _lastText is { } prev)
        {
            var edit = ComputeEdit(prev, text);
            ts_tree_edit(_tree, ref edit);
            reuseTree = _tree;
        }

        IntPtr newTree;
        unsafe
        {
            fixed (byte* p = bytes)
            {
                newTree = ts_parser_parse_string(_parser, reuseTree, (IntPtr)p, (uint)bytes.Length);
            }
        }
        if (newTree == IntPtr.Zero) return;
        if (_tree != IntPtr.Zero) ts_tree_delete(_tree);
        _tree = newTree;

        var root = ts_tree_root_node(_tree);
        var cursor = ts_tree_cursor_new(root);
        try
        {
            Walk(ref cursor, parentType: null, bytes, lineStarts);
        }
        finally
        {
            ts_tree_cursor_delete(ref cursor);
        }

        foreach (var list in _spansByLine)
            list.Sort(static (a, b) => a.Start.CompareTo(b.Start));
    }

    private void Walk(ref TSTreeCursor cursor, string? parentType, byte[] bytes, int[] lineStarts)
    {
        var node = ts_tree_cursor_current_node(ref cursor);
        var type = MarshalUtf8(ts_node_type(node));
        var named = ts_node_is_named(node);
        var childCount = ts_node_child_count(node);

        if (childCount == 0)
        {
            var category = Classify(type, named, parentType, _grammarName, bytes, node);
            if (category != null) EmitSpan(node, category, lineStarts);
            return;
        }

        if (ts_tree_cursor_goto_first_child(ref cursor))
        {
            do
            {
                Walk(ref cursor, type, bytes, lineStarts);
            } while (ts_tree_cursor_goto_next_sibling(ref cursor));
            ts_tree_cursor_goto_parent(ref cursor);
        }
    }

    private void EmitSpan(TSNode node, string category, int[] lineStarts)
    {
        if (!Palette.TryGetValue(category, out var ansi)) return;
        var startByte = (int)ts_node_start_byte(node);
        var endByte = (int)ts_node_end_byte(node);
        var startPt = ts_node_start_point(node);
        var endPt = ts_node_end_point(node);
        var startLine = (int)startPt.Row;
        var endLine = (int)endPt.Row;
        if (startLine < 0 || startLine >= _spansByLine.Count) return;

        // Single-line span — common case.
        if (startLine == endLine)
        {
            var col = startByte - lineStarts[startLine];
            var len = endByte - startByte;
            if (len > 0) _spansByLine[startLine].Add(new StyledSpan(col, len, ansi));
            return;
        }

        // Multi-line: split. First line goes to EOL, intermediate lines
        // are colored end-to-end, last line from start of line up to the
        // node's end column.
        var firstCol = startByte - lineStarts[startLine];
        var firstLineEnd = (startLine + 1 < lineStarts.Length ? lineStarts[startLine + 1] - 1 : lineStarts[startLine])
                           - lineStarts[startLine];
        if (firstLineEnd > firstCol)
            _spansByLine[startLine].Add(new StyledSpan(firstCol, firstLineEnd - firstCol, ansi));
        for (var ln = startLine + 1; ln < endLine && ln < _spansByLine.Count; ln++)
        {
            var lnEnd = (ln + 1 < lineStarts.Length ? lineStarts[ln + 1] - 1 : lineStarts[ln]) - lineStarts[ln];
            if (lnEnd > 0) _spansByLine[ln].Add(new StyledSpan(0, lnEnd, ansi));
        }
        if (endLine < _spansByLine.Count)
        {
            var lastLen = endByte - lineStarts[endLine];
            if (lastLen > 0) _spansByLine[endLine].Add(new StyledSpan(0, lastLen, ansi));
        }
    }

    /// <summary>
    /// Heuristic node-type → category map. Order matters: more-specific
    /// patterns must run before generic <c>Contains</c> checks.
    /// </summary>
    private static string? Classify(string type, bool named, string? parentType, string grammar, byte[] bytes, TSNode node)
    {
        // Anonymous leaves: typically keywords ("if", "for") or
        // punctuation ("(", "{", ";", "->").
        if (!named)
        {
            if (type.Length == 0) return null;
            // Pure-letter anonymous tokens are keywords.
            if (IsAllLetters(type)) return "keyword";
            // Single-char punctuation / operators.
            return IsOperatorish(type) ? "operator" : "punctuation";
        }

        // Comments cover every grammar's "comment", "line_comment",
        // "block_comment" etc. variants.
        if (type.Contains("comment", StringComparison.Ordinal)) return "comment";

        // String content & wrappers. "string", "string_literal",
        // "raw_string_literal", "string_content", "interpreted_string_literal".
        if (type.Contains("string", StringComparison.Ordinal)) return "string";
        if (type == "char_literal" || type == "character") return "string";

        // Numerics.
        if (type == "integer" || type == "float" || type == "number"
            || type.Contains("integer_literal", StringComparison.Ordinal)
            || type.Contains("float_literal", StringComparison.Ordinal)
            || type.Contains("number_literal", StringComparison.Ordinal))
            return "number";

        // Booleans / null / nil — surface as "constant" (orange).
        if (type == "true" || type == "false" || type == "none" || type == "null"
            || type == "nil" || type == "boolean_literal")
            return "constant";

        // Markdown — surface a few high-impact node types.
        if (grammar == "markdown")
        {
            if (type.StartsWith("atx_heading", StringComparison.Ordinal)
                || type.StartsWith("setext_heading", StringComparison.Ordinal)
                || type == "atx_h1_marker" || type == "atx_h2_marker"
                || type == "atx_h3_marker" || type == "atx_h4_marker"
                || type == "atx_h5_marker" || type == "atx_h6_marker")
                return "heading";
            if (type == "fenced_code_block" || type == "code_fence_content"
                || type == "indented_code_block" || type == "code_span")
                return "string";
            if (type == "link_destination" || type == "link_text" || type == "link_label")
                return "function";
        }

        // Type names — almost every grammar names these "type_identifier"
        // or contains "type" in the parent.
        if (type == "type_identifier" || type == "primitive_type") return "type";
        if (type == "identifier" || type == "name" || type == "word")
        {
            if (parentType != null)
            {
                if (parentType.Contains("call", StringComparison.Ordinal)
                    || parentType.Contains("function", StringComparison.Ordinal)
                    || parentType == "method_definition" || parentType == "method_declaration")
                    return "function";
                if (parentType.Contains("type", StringComparison.Ordinal)
                    || parentType.Contains("class", StringComparison.Ordinal)
                    || parentType.Contains("struct", StringComparison.Ordinal)
                    || parentType.Contains("enum", StringComparison.Ordinal))
                    return "type";
            }
            return "variable";
        }

        if (type.EndsWith("_operator", StringComparison.Ordinal) || type == "operator")
            return "operator";

        return null;
    }

    private static bool IsAllLetters(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))) return false;
        }
        return s.Length > 0;
    }

    private static bool IsOperatorish(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '=' || c == '+' || c == '-' || c == '*' || c == '/' || c == '%'
                || c == '<' || c == '>' || c == '!' || c == '&' || c == '|' || c == '^'
                || c == '~' || c == '?' || c == ':' || c == '.') return true;
        }
        return false;
    }

    private static string MarshalUtf8(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return string.Empty;
        return Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        var count = 1;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') count++;
        return count;
    }

    /// <summary>
    /// Builds a single <see cref="TSInputEdit"/> describing the
    /// difference between <paramref name="oldText"/> and
    /// <paramref name="newText"/>. We diff by trimming the longest
    /// common prefix and suffix and treating everything in between as a
    /// single contiguous replacement — exactly what tree-sitter's
    /// incremental engine needs to skip untouched subtrees. Multi-edit
    /// batches collapse to one bounding edit, which is conservative but
    /// still beats a full reparse for the keystroke-paced edits Tōme
    /// sees.
    /// </summary>
    private static TSInputEdit ComputeEdit(string oldText, string newText)
    {
        var oldBytes = Encoding.UTF8.GetBytes(oldText);
        var newBytes = Encoding.UTF8.GetBytes(newText);

        var prefix = 0;
        var max = Math.Min(oldBytes.Length, newBytes.Length);
        while (prefix < max && oldBytes[prefix] == newBytes[prefix]) prefix++;

        var oldSuffix = oldBytes.Length;
        var newSuffix = newBytes.Length;
        while (oldSuffix > prefix && newSuffix > prefix
            && oldBytes[oldSuffix - 1] == newBytes[newSuffix - 1])
        {
            oldSuffix--;
            newSuffix--;
        }

        return new TSInputEdit
        {
            StartByte = (uint)prefix,
            OldEndByte = (uint)oldSuffix,
            NewEndByte = (uint)newSuffix,
            StartPoint = ByteOffsetToPoint(oldBytes, prefix),
            OldEndPoint = ByteOffsetToPoint(oldBytes, oldSuffix),
            NewEndPoint = ByteOffsetToPoint(newBytes, newSuffix),
        };
    }

    private static TSPoint ByteOffsetToPoint(byte[] bytes, int offset)
    {
        uint row = 0;
        uint col = 0;
        var clamped = Math.Min(offset, bytes.Length);
        for (var i = 0; i < clamped; i++)
        {
            if (bytes[i] == (byte)'\n') { row++; col = 0; }
            else col++;
        }
        return new TSPoint { Row = row, Column = col };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_tree != IntPtr.Zero) { ts_tree_delete(_tree); _tree = IntPtr.Zero; }
        if (_parser != IntPtr.Zero) { ts_parser_delete(_parser); _parser = IntPtr.Zero; }
    }

    /// <summary>
    /// Returns the byte range of the smallest named tree-sitter node
    /// containing <paramref name="location"/>, in (line, column)
    /// coordinates. Returns null when no parse tree is available.
    /// </summary>
    public (TextLocation start, TextLocation end)? RangeAt(TextLocation location)
    {
        if (_tree == IntPtr.Zero) return null;
        var root = ts_tree_root_node(_tree);
        var p = new TSPoint { Row = (uint)Math.Max(0, location.Line), Column = (uint)Math.Max(0, location.Column) };
        var node = ts_node_named_descendant_for_point_range(root, p, p);
        if (node.Id == IntPtr.Zero) return null;
        var s = ts_node_start_point(node);
        var e = ts_node_end_point(node);
        return (new TextLocation((int)s.Row, (int)s.Column), new TextLocation((int)e.Row, (int)e.Column));
    }
}
