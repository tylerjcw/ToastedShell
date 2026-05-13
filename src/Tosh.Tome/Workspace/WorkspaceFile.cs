using System.Globalization;
using System.Text;

namespace Tosh.Tome.Workspace;

/// <summary>
/// Reader/writer for <c>.tome</c> workspace files. The format is a small
/// declarative subset that <em>looks</em> like a TōSh script but is parsed
/// independently — keeping the format frozen against future TōSh syntax
/// changes and avoiding a dependency from <c>Tosh.Tome</c> on the language
/// projects.
/// </summary>
/// <remarks>
/// Grammar (informal):
/// <code>
/// file       := workspace_decl
/// workspace_decl := "workspace" STRING "{" item* "}"
/// item       := folder_decl | exclude_decl | open_decl | layout_decl
/// folder_decl    := "folder" STRING ("as" STRING)?
/// exclude_decl   := "exclude" list
/// open_decl      := "open" list
/// layout_decl    := "layout" "{" layout_kv (("," | NEWLINE) layout_kv)* "}"
/// layout_kv      := IDENT ("." IDENT)? "=" (NUMBER | BOOL | STRING)
/// list           := "[" (STRING (("," | NEWLINE) STRING)*)? "]"
/// </code>
/// Comments: <c>#</c> to end of line. Strings: double- or single-quoted.
/// </remarks>
internal static class WorkspaceFile
{
    public static Workspace Load(string path)
    {
        var text = File.ReadAllText(path);
        var fullPath = Path.GetFullPath(path);
        // VS Code workspace files are JSON; detect by extension and fall
        // back to native .tome parsing for everything else.
        var ws = fullPath.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase)
            ? CodeWorkspaceImporter.Parse(text, sourceName: fullPath)
            : Parse(text, sourceName: fullPath);
        return ResolveLoadedPaths(ws, fullPath);
    }

    public static Workspace Parse(string text, string sourceName = "<workspace>")
    {
        var tokens = Tokenize(text, sourceName);
        var parser = new Parser(tokens, sourceName);
        return parser.ParseWorkspace();
    }

    public static void Save(Workspace workspace, string path)
    {
        File.WriteAllText(path, Serialize(workspace));
    }

    public static string Serialize(Workspace ws)
    {
        var sb = new StringBuilder();
        sb.Append("workspace ").Append(Quote(ws.Name)).Append(" {\n");
        foreach (var f in ws.Folders)
        {
            sb.Append("    folder ").Append(Quote(f.Path));
            if (!string.IsNullOrEmpty(f.Alias)) sb.Append(" as ").Append(Quote(f.Alias));
            sb.Append('\n');
        }
        if (ws.Exclude.Count > 0)
        {
            sb.Append("    exclude [");
            for (var i = 0; i < ws.Exclude.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Quote(ws.Exclude[i]));
            }
            sb.Append("]\n");
        }
        if (ws.OpenFiles.Count > 0)
        {
            sb.Append("    open [");
            for (var i = 0; i < ws.OpenFiles.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Quote(ws.OpenFiles[i]));
            }
            sb.Append("]\n");
        }
        sb.Append("    layout {\n");
        sb.Append("        explorer.width = ").Append(ws.Layout.ExplorerWidth).Append('\n');
        sb.Append("        explorer.open = ").Append(ws.Layout.ExplorerOpen ? "true" : "false").Append('\n');
        sb.Append("    }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static Workspace ResolveLoadedPaths(Workspace ws, string sourcePath)
    {
        var baseDir = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
        var folders = ws.Folders
            .Select(f =>
            {
                var resolved = Path.IsPathRooted(f.Path) ? Path.GetFullPath(f.Path) : Path.GetFullPath(f.Path, baseDir);
                return f with { Path = resolved };
            })
            .ToArray();
        return ws with { SourcePath = sourcePath, Folders = folders };
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // ──────────────────────────────────────────────────────────────────
    // Tokenizer
    // ──────────────────────────────────────────────────────────────────

    private enum TokKind { Ident, String, Number, LBrace, RBrace, LBracket, RBracket, Comma, Dot, Equals, Newline, Eof }

    private readonly record struct Tok(TokKind Kind, string Text, int Line, int Col);

    private static List<Tok> Tokenize(string text, string sourceName)
    {
        var toks = new List<Tok>();
        var i = 0; var line = 1; var col = 1;

        void Emit(TokKind k, string t, int ln, int cl) => toks.Add(new Tok(k, t, ln, cl));

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\n') { Emit(TokKind.Newline, "\n", line, col); i++; line++; col = 1; continue; }
            if (c == '\r') { i++; continue; }
            if (c == ' ' || c == '\t') { i++; col++; continue; }
            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            var startLine = line; var startCol = col;

            if (c == '{') { Emit(TokKind.LBrace, "{", startLine, startCol); i++; col++; continue; }
            if (c == '}') { Emit(TokKind.RBrace, "}", startLine, startCol); i++; col++; continue; }
            if (c == '[') { Emit(TokKind.LBracket, "[", startLine, startCol); i++; col++; continue; }
            if (c == ']') { Emit(TokKind.RBracket, "]", startLine, startCol); i++; col++; continue; }
            if (c == ',') { Emit(TokKind.Comma, ",", startLine, startCol); i++; col++; continue; }
            if (c == '.') { Emit(TokKind.Dot, ".", startLine, startCol); i++; col++; continue; }
            if (c == '=') { Emit(TokKind.Equals, "=", startLine, startCol); i++; col++; continue; }

            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++; col++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        var esc = text[i + 1];
                        sb.Append(esc switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '"' => '"',
                            '\'' => '\'',
                            '\\' => '\\',
                            _ => esc,
                        });
                        i += 2; col += 2;
                    }
                    else
                    {
                        if (text[i] == '\n') { line++; col = 1; } else col++;
                        sb.Append(text[i]);
                        i++;
                    }
                }
                if (i >= text.Length)
                    throw new WorkspaceParseException($"{sourceName}:{startLine}:{startCol}: unterminated string");
                i++; col++;
                Emit(TokKind.String, sb.ToString(), startLine, startCol);
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                var start = i;
                if (c == '-') { i++; col++; }
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) { i++; col++; }
                Emit(TokKind.Number, text[start..i], startLine, startCol);
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '-'))
                { i++; col++; }
                Emit(TokKind.Ident, text[start..i], startLine, startCol);
                continue;
            }

            throw new WorkspaceParseException($"{sourceName}:{line}:{col}: unexpected character '{c}'");
        }

        Emit(TokKind.Eof, string.Empty, line, col);
        return toks;
    }

    // ──────────────────────────────────────────────────────────────────
    // Parser
    // ──────────────────────────────────────────────────────────────────

    private sealed class Parser
    {
        private readonly List<Tok> _toks;
        private readonly string _src;
        private int _pos;

        public Parser(List<Tok> toks, string src) { _toks = toks; _src = src; }

        private Tok Peek => _toks[_pos];
        private Tok Next() => _toks[_pos++];

        private void SkipNewlines()
        {
            while (Peek.Kind == TokKind.Newline) _pos++;
        }

        private Tok Expect(TokKind kind, string what)
        {
            SkipNewlines();
            var t = Peek;
            if (t.Kind != kind)
                throw new WorkspaceParseException($"{_src}:{t.Line}:{t.Col}: expected {what}, got '{t.Text}'");
            return Next();
        }

        public Workspace ParseWorkspace()
        {
            SkipNewlines();
            var head = Expect(TokKind.Ident, "'workspace'");
            if (!string.Equals(head.Text, "workspace", StringComparison.Ordinal))
                throw new WorkspaceParseException($"{_src}:{head.Line}:{head.Col}: expected 'workspace', got '{head.Text}'");

            var name = Expect(TokKind.String, "workspace name string").Text;
            Expect(TokKind.LBrace, "'{'");

            var folders = new List<WorkspaceFolder>();
            var exclude = new List<string>();
            var open = new List<string>();
            var layout = new WorkspaceLayout();

            while (true)
            {
                SkipNewlines();
                if (Peek.Kind == TokKind.RBrace) { Next(); break; }
                if (Peek.Kind == TokKind.Eof)
                    throw new WorkspaceParseException($"{_src}:{Peek.Line}:{Peek.Col}: unexpected end of file inside workspace block");

                var verb = Expect(TokKind.Ident, "item keyword");
                switch (verb.Text)
                {
                    case "folder":
                        {
                            var path = Expect(TokKind.String, "folder path").Text;
                            string? alias = null;
                            SkipNewlines();
                            if (Peek.Kind == TokKind.Ident && Peek.Text == "as")
                            {
                                Next();
                                alias = Expect(TokKind.String, "folder alias").Text;
                            }
                            folders.Add(new WorkspaceFolder(path, alias));
                            break;
                        }
                    case "exclude":
                        exclude.AddRange(ParseStringList());
                        break;
                    case "open":
                        open.AddRange(ParseStringList());
                        break;
                    case "layout":
                        layout = ParseLayout(layout);
                        break;
                    default:
                        throw new WorkspaceParseException(
                            $"{_src}:{verb.Line}:{verb.Col}: unknown item '{verb.Text}' (expected folder/exclude/open/layout)");
                }
            }

            return new Workspace
            {
                Name = name,
                Folders = folders,
                Exclude = exclude,
                OpenFiles = open,
                Layout = layout,
            };
        }

        private List<string> ParseStringList()
        {
            var items = new List<string>();
            Expect(TokKind.LBracket, "'['");
            while (true)
            {
                SkipNewlines();
                if (Peek.Kind == TokKind.RBracket) { Next(); break; }
                var s = Expect(TokKind.String, "string");
                items.Add(s.Text);
                SkipNewlines();
                if (Peek.Kind == TokKind.Comma) { Next(); continue; }
                if (Peek.Kind == TokKind.RBracket) { Next(); break; }
                throw new WorkspaceParseException($"{_src}:{Peek.Line}:{Peek.Col}: expected ',' or ']', got '{Peek.Text}'");
            }
            return items;
        }

        private WorkspaceLayout ParseLayout(WorkspaceLayout seed)
        {
            Expect(TokKind.LBrace, "'{'");
            var width = seed.ExplorerWidth;
            var openFlag = seed.ExplorerOpen;
            while (true)
            {
                SkipNewlines();
                if (Peek.Kind == TokKind.RBrace) { Next(); break; }

                var head = Expect(TokKind.Ident, "layout key");
                var keyPath = head.Text;
                while (Peek.Kind == TokKind.Dot)
                {
                    Next();
                    var seg = Expect(TokKind.Ident, "layout key segment");
                    keyPath += "." + seg.Text;
                }
                Expect(TokKind.Equals, "'='");
                SkipNewlines();
                var value = Next();

                switch (keyPath)
                {
                    case "explorer.width":
                        if (value.Kind != TokKind.Number || !int.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out width))
                            throw new WorkspaceParseException($"{_src}:{value.Line}:{value.Col}: explorer.width expects an integer");
                        break;
                    case "explorer.open":
                        if (value.Kind != TokKind.Ident || (value.Text != "true" && value.Text != "false"))
                            throw new WorkspaceParseException($"{_src}:{value.Line}:{value.Col}: explorer.open expects true|false");
                        openFlag = value.Text == "true";
                        break;
                    default:
                        // Forward-compat: unknown keys are tolerated but ignored.
                        break;
                }

                SkipNewlines();
                if (Peek.Kind == TokKind.Comma) { Next(); continue; }
            }
            return seed with { ExplorerWidth = width, ExplorerOpen = openFlag };
        }
    }
}

internal sealed class WorkspaceParseException : Exception
{
    public WorkspaceParseException(string message) : base(message) { }
}
