using System.Globalization;
using System.Text;

namespace Tosh.DevCompanion.Memory;

/// <summary>One memory as its file under <c>.tosh/memories/</c> records it.</summary>
/// <remarks>
/// Access counts and timestamps are left out on purpose: they describe how one checkout used
/// a memory, and committing them would turn every recall into a diff.
/// </remarks>
public sealed record SharedMemory(
    string Id,
    long CreatedAt,
    string Category,
    string Scope,
    string Source,
    bool Pinned,
    string? SessionId,
    IReadOnlyList<string> Tags,
    string Summary,
    string Content,
    IReadOnlyList<MemoryLink> Links,
    long? DeletedAt);

/// <summary>One shared memory's file: the memory and the relations that start from it.</summary>
public sealed record SharedMemoryDocument(
    SharedMemory Memory,
    IReadOnlyList<MemoryRelation> Relations);

/// <summary>
/// Reads and writes the files under <c>.tosh/memories/</c>, the git-tracked copy of every memory
/// stored with <c>visibility = "shared"</c>. Each memory has a file of its own,
/// <c>&lt;id&gt;.toml</c>, which also holds the relations that start from it.
/// </summary>
/// <remarks>
/// <para>
/// The SQLite database is a local cache that git ignores; these files are what travel with the
/// repository. The store rewrites a memory's file whenever the memory changes and imports the
/// directory when it opens, so a fresh clone, or a cloud session whose database starts empty,
/// begins with the project's shared decisions.
/// </para>
/// <para>
/// A file per memory is what lets branches share memories without merge conflicts. In a single
/// file, every memory a branch adds lands at the same place, so any two branches that each add
/// one conflict — and keeping both sides does not resolve it, because git moves the lines the
/// two new blocks have in common out of the conflict (a <c>merge=union</c> driver interleaves
/// them into one broken block the same way). Separate files only conflict when two branches
/// change the same memory.
/// </para>
/// <para>
/// It handles the part of TOML the writer produces — <c>[[memory]]</c> and <c>[[relation]]</c>
/// tables, strings, integers, booleans, offset date-times, arrays and inline tables — so the
/// companion keeps its single package reference, and a hand edit that stays inside that subset
/// reads back unchanged. Anything else is reported with its line number rather than guessed at.
/// </para>
/// </remarks>
public static class SharedMemoryFile
{
    private static readonly string[] Categories = ["fact", "preference", "pattern", "decision", "history", "note"];
    private static readonly string[] Scopes = ["project", "global"];
    private static readonly string[] Sources = ["ai", "user"];
    private static readonly string[] Relationships = ["supersedes", "supports", "contradicts", "related_to"];

    /// <summary>The name of the file that holds the memory with this id.</summary>
    public static string FileName(string id) => id + ".toml";

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders a memory's file. The output depends only on the memory and its relations, so a
    /// file changes exactly when one of them does.
    /// </summary>
    public static string Render(SharedMemory m, IEnumerable<MemoryRelation> relations)
    {
        var sb = new StringBuilder("[[memory]]\n");
        AppendPair(sb, "id", Quote(m.Id));
        AppendPair(sb, "created", Timestamp(m.CreatedAt));
        AppendPair(sb, "category", Quote(m.Category));
        AppendPair(sb, "scope", Quote(m.Scope));
        AppendPair(sb, "source", Quote(m.Source));
        if (m.Pinned) AppendPair(sb, "pinned", "true");
        if (m.SessionId is not null) AppendPair(sb, "session_id", Quote(m.SessionId));
        if (m.Tags.Count > 0) AppendPair(sb, "tags", $"[{string.Join(", ", m.Tags.Select(Quote))}]");
        AppendPair(sb, "summary", Quote(m.Summary));
        AppendPair(sb, "content", m.Content.Contains('\n') ? QuoteMultiline(m.Content) : Quote(m.Content));
        if (m.Links.Count > 0) AppendPair(sb, "links", $"[{string.Join(", ", m.Links.Select(RenderLink))}]");
        if (m.DeletedAt is long deleted) AppendPair(sb, "deleted", Timestamp(deleted));

        foreach (var r in relations
                     .OrderBy(r => r.CreatedAt)
                     .ThenBy(r => r.ToId, StringComparer.Ordinal)
                     .ThenBy(r => r.Relationship, StringComparer.Ordinal))
        {
            sb.Append("\n[[relation]]\n");
            AppendPair(sb, "from", Quote(r.FromId));
            AppendPair(sb, "to", Quote(r.ToId));
            AppendPair(sb, "relationship", Quote(r.Relationship));
            AppendPair(sb, "created", Timestamp(r.CreatedAt));
        }

        return sb.ToString();
    }

    private static void AppendPair(StringBuilder sb, string key, string value) =>
        sb.Append(key).Append(" = ").Append(value).Append('\n');

    private static string Timestamp(long unixMilliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string RenderLink(MemoryLink link)
    {
        var parts = new List<string> { $"path = {Quote(link.Path)}" };
        if (link.Line is int line) parts.Add($"line = {line.ToString(CultureInfo.InvariantCulture)}");
        if (link.LineEnd is int end) parts.Add($"line_end = {end.ToString(CultureInfo.InvariantCulture)}");
        if (link.Kind is not null) parts.Add($"kind = {Quote(link.Kind)}");
        return $"{{ {string.Join(", ", parts)} }}";
    }

    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value) AppendEscaped(sb, c, multiline: false);
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// A multi-line basic string, so a memory's body reads and diffs as prose. Content that does
    /// not end in a newline closes with a line-ending backslash, which keeps the delimiter on a
    /// line of its own without adding a newline to the value.
    /// </summary>
    private static string QuoteMultiline(string value)
    {
        var sb = new StringBuilder(value.Length + 8).Append("\"\"\"\n");
        foreach (var c in value) AppendEscaped(sb, c, multiline: true);
        if (!value.EndsWith('\n')) sb.Append("\\\n");
        return sb.Append("\"\"\"").ToString();
    }

    private static void AppendEscaped(StringBuilder sb, char c, bool multiline)
    {
        switch (c)
        {
            case '\\': sb.Append("\\\\"); break;
            case '"': sb.Append("\\\""); break;
            case '\n': sb.Append(multiline ? "\n" : "\\n"); break;
            case '\t': sb.Append(multiline ? "\t" : "\\t"); break;
            case '\r': sb.Append("\\r"); break;
            case '\b': sb.Append("\\b"); break;
            case '\f': sb.Append("\\f"); break;
            case < ' ' or '\u007F': sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)); break;
            default: sb.Append(c); break;
        }
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>Parses a memory's file.</summary>
    /// <exception cref="FormatException">The text is not something a memory's file could hold;
    /// the message names the line.</exception>
    public static SharedMemoryDocument Parse(string text)
    {
        try
        {
            return ParseDocument(text);
        }
        catch (FormatException) when (FindConflictMarker(text) is int line)
        {
            // A marker inside a string parses, so it never gets here: a file that fails to parse
            // and has one is a merge that has not been resolved.
            throw new FormatException(
                $"line {line}: unresolved merge conflict. Keep the side that should win, " +
                "delete the conflict markers, and the companion picks the file up on its next call");
        }
    }

    private static SharedMemoryDocument ParseDocument(string text)
    {
        SharedMemory? memory = null;
        var relations = new List<MemoryRelation>();

        foreach (var (name, values, line) in new Parser(text).ReadTables())
        {
            switch (name)
            {
                case "memory" when memory is not null:
                    throw new FormatException($"line {line}: a file holds one [[memory]]");
                case "memory":
                    memory = ToMemory(values, line);
                    break;
                case "relation":
                    relations.Add(ToRelation(values, line));
                    break;
                default:
                    throw new FormatException($"line {line}: unknown table [[{name}]]");
            }
        }

        if (memory is null) throw new FormatException("line 1: the file has no [[memory]]");

        if (relations.FirstOrDefault(r => r.FromId != memory.Id) is { } stray)
        {
            throw new FormatException(
                $"a [[relation]] from '{stray.FromId}' belongs in {FileName(stray.FromId)}, not with '{memory.Id}'");
        }

        return new SharedMemoryDocument(memory, relations);
    }

    /// <summary>The line of the first git conflict marker, or <see langword="null"/>.</summary>
    private static int? FindConflictMarker(string text)
    {
        var line = 0;
        foreach (var l in text.ReplaceLineEndings("\n").Split('\n'))
        {
            line++;
            if (l.StartsWith("<<<<<<<", StringComparison.Ordinal) ||
                l.StartsWith("|||||||", StringComparison.Ordinal) ||
                l.StartsWith(">>>>>>>", StringComparison.Ordinal) ||
                l.TrimEnd() == "=======")
            {
                return line;
            }
        }

        return null;
    }

    private static SharedMemory ToMemory(Dictionary<string, object?> values, int line)
    {
        var fields = new Fields(values, line, "memory");

        return new SharedMemory(
            Id: fields.RequiredString("id"),
            CreatedAt: fields.RequiredTimestamp("created"),
            Category: fields.OneOf("category", Categories, fallback: null),
            Scope: fields.OneOf("scope", Scopes, fallback: "project"),
            Source: fields.OneOf("source", Sources, fallback: "ai"),
            Pinned: fields.OptionalBool("pinned") ?? false,
            SessionId: fields.OptionalString("session_id"),
            Tags: fields.StringList("tags"),
            Summary: fields.RequiredString("summary"),
            Content: fields.RequiredString("content", allowEmpty: true),
            Links: fields.Links("links"),
            DeletedAt: fields.OptionalTimestamp("deleted"));
    }

    private static MemoryRelation ToRelation(Dictionary<string, object?> values, int line)
    {
        var fields = new Fields(values, line, "relation");

        return new MemoryRelation(
            FromId: fields.RequiredString("from"),
            ToId: fields.RequiredString("to"),
            Relationship: fields.OneOf("relationship", Relationships, fallback: null),
            CreatedAt: fields.RequiredTimestamp("created"));
    }

    /// <summary>Typed access to one table's values, failing with the table's line number.</summary>
    private sealed class Fields(Dictionary<string, object?> values, int line, string table)
    {
        public string RequiredString(string key, bool allowEmpty = false) =>
            values.TryGetValue(key, out var v) && v is string s && (allowEmpty || s.Length > 0)
                ? s
                : throw Invalid(key, allowEmpty ? "a string" : "a non-empty string");

        public string? OptionalString(string key) =>
            !values.TryGetValue(key, out var v) ? null : v as string ?? throw Invalid(key, "a string");

        public bool? OptionalBool(string key) =>
            !values.TryGetValue(key, out var v) ? null : v as bool? ?? throw Invalid(key, "true or false");

        public long RequiredTimestamp(string key) =>
            values.TryGetValue(key, out var v) && v is DateTimeOffset at
                ? at.ToUnixTimeMilliseconds()
                : throw Invalid(key, "a date-time such as 2026-01-31T12:00:00.000Z");

        public long? OptionalTimestamp(string key) =>
            values.ContainsKey(key) ? RequiredTimestamp(key) : null;

        public string OneOf(string key, string[] allowed, string? fallback)
        {
            if (!values.TryGetValue(key, out var v) && fallback is not null) return fallback;
            return v is string s && allowed.Contains(s, StringComparer.Ordinal)
                ? s
                : throw Invalid(key, $"one of {string.Join(", ", allowed.Select(a => $"\"{a}\""))}");
        }

        public IReadOnlyList<string> StringList(string key)
        {
            if (!values.TryGetValue(key, out var v)) return [];
            return v is List<object?> items && items.All(i => i is string)
                ? [.. items.Cast<string>()]
                : throw Invalid(key, "an array of strings");
        }

        public IReadOnlyList<MemoryLink> Links(string key)
        {
            if (!values.TryGetValue(key, out var v)) return [];
            if (v is not List<object?> items) throw Invalid(key, "an array of { path = ... } tables");

            var links = new List<MemoryLink>(items.Count);
            foreach (var item in items)
            {
                if (item is not Dictionary<string, object?> link ||
                    !link.TryGetValue("path", out var path) || path is not string p || p.Length == 0)
                {
                    throw Invalid(key, "an array of { path = ... } tables");
                }

                links.Add(new MemoryLink(
                    Path: p,
                    Line: LineNumber(link, "line"),
                    LineEnd: LineNumber(link, "line_end"),
                    Kind: link.TryGetValue("kind", out var kind) ? kind as string ?? throw Invalid(key, "a string kind") : null));
            }

            return links;
        }

        private int? LineNumber(Dictionary<string, object?> link, string key) =>
            !link.TryGetValue(key, out var v)
                ? null
                : v is long n && n is > 0 and <= int.MaxValue ? (int)n : throw Invalid(key, "a positive line number");

        private FormatException Invalid(string key, string expected) =>
            new($"line {line}: [[{table}]] needs '{key}' to be {expected}");
    }

    /// <summary>A reader for the TOML subset described on <see cref="SharedMemoryFile"/>.</summary>
    private sealed class Parser(string text)
    {
        private int _pos;
        private int _line = 1;

        private bool AtEnd => _pos >= text.Length;

        private char Peek(int offset = 0) => _pos + offset < text.Length ? text[_pos + offset] : '\0';

        private bool StartsWith(string s) => string.CompareOrdinal(text, _pos, s, 0, s.Length) == 0;

        public List<(string Name, Dictionary<string, object?> Values, int Line)> ReadTables()
        {
            var tables = new List<(string, Dictionary<string, object?>, int)>();
            Dictionary<string, object?>? current = null;

            while (true)
            {
                SkipTrivia(newlines: true);
                if (AtEnd) return tables;

                if (Peek() == '[')
                {
                    if (Peek(1) != '[') throw Error("only [[memory]] and [[relation]] tables are supported");

                    var headerLine = _line;
                    _pos += 2;
                    SkipSpaces();
                    var name = ReadBareKey();
                    SkipSpaces();
                    Expect(']');
                    Expect(']');
                    EndOfLine();

                    current = new Dictionary<string, object?>(StringComparer.Ordinal);
                    tables.Add((name, current, headerLine));
                    continue;
                }

                if (current is null) throw Error("a key appears before the first [[memory]] or [[relation]] header");

                var key = ReadKey();
                SkipSpaces();
                Expect('=');
                SkipSpaces();
                var value = ReadValue();
                if (!current.TryAdd(key, value)) throw Error($"duplicate key '{key}'");
                EndOfLine();
            }
        }

        private object? ReadValue() => Peek() switch
        {
            '"' when StartsWith("\"\"\"") => ReadMultilineBasicString(),
            '"' => ReadBasicString(),
            '\'' when StartsWith("'''") => ReadMultilineLiteralString(),
            '\'' => ReadLiteralString(),
            '[' => ReadArray(),
            '{' => ReadInlineTable(),
            _ => ReadScalar(),
        };

        private List<object?> ReadArray()
        {
            Expect('[');
            var items = new List<object?>();

            while (true)
            {
                SkipTrivia(newlines: true);
                if (Peek() == ']') { _pos++; return items; }

                items.Add(ReadValue());
                SkipTrivia(newlines: true);

                if (Peek() == ',') { _pos++; continue; }
                if (Peek() == ']') { _pos++; return items; }
                throw Error("expected ',' or ']' in an array");
            }
        }

        private Dictionary<string, object?> ReadInlineTable()
        {
            Expect('{');
            var table = new Dictionary<string, object?>(StringComparer.Ordinal);
            SkipSpaces();
            if (Peek() == '}') { _pos++; return table; }

            while (true)
            {
                SkipSpaces();
                var key = ReadKey();
                SkipSpaces();
                Expect('=');
                SkipSpaces();
                if (!table.TryAdd(key, ReadValue())) throw Error($"duplicate key '{key}'");
                SkipSpaces();

                if (Peek() == ',') { _pos++; continue; }
                if (Peek() == '}') { _pos++; return table; }
                throw Error("expected ',' or '}' in an inline table");
            }
        }

        /// <summary>A boolean, an integer or an offset date-time.</summary>
        private object ReadScalar()
        {
            var start = _pos;
            while (!AtEnd && Peek() is not (' ' or '\t' or '\r' or '\n' or ',' or ']' or '}' or '#')) _pos++;
            var token = text[start.._pos];

            if (token == "true") return true;
            if (token == "false") return false;

            if (token.Contains('T') &&
                DateTimeOffset.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                return at;
            }

            if (long.TryParse(token.Replace("_", string.Empty), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }

            throw Error(token.Length == 0 ? "a value is missing" : $"'{token}' is not a value this file uses");
        }

        private string ReadBasicString()
        {
            Expect('"');
            var sb = new StringBuilder();

            while (true)
            {
                if (AtEnd || Peek() is '\n' or '\r') throw Error("unterminated string");
                var c = text[_pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\') { ReadEscape(sb); continue; }
                sb.Append(c);
            }
        }

        private string ReadMultilineBasicString()
        {
            _pos += 3;
            SkipFirstNewline();
            var sb = new StringBuilder();

            while (true)
            {
                if (AtEnd) throw Error("unterminated multi-line string");

                if (StartsWith("\"\"\""))
                {
                    _pos += 3;
                    // Up to two quotation marks may sit just inside the closing delimiter.
                    for (var extra = 0; extra < 2 && Peek() == '"'; extra++, _pos++) sb.Append('"');
                    return sb.ToString();
                }

                var c = text[_pos++];

                if (c == '\\')
                {
                    // A backslash that ends a line drops the newline and the whitespace after it.
                    if (RestOfLineIsBlank()) { SkipWhitespaceAndNewlines(); continue; }
                    ReadEscape(sb);
                    continue;
                }

                AppendRaw(sb, c);
            }
        }

        private string ReadLiteralString()
        {
            Expect('\'');
            var start = _pos;
            while (!AtEnd && Peek() is not ('\'' or '\n' or '\r')) _pos++;
            if (Peek() != '\'') throw Error("unterminated string");
            var value = text[start.._pos];
            _pos++;
            return value;
        }

        private string ReadMultilineLiteralString()
        {
            _pos += 3;
            SkipFirstNewline();
            var sb = new StringBuilder();

            while (true)
            {
                if (AtEnd) throw Error("unterminated multi-line string");

                if (StartsWith("'''"))
                {
                    _pos += 3;
                    for (var extra = 0; extra < 2 && Peek() == '\''; extra++, _pos++) sb.Append('\'');
                    return sb.ToString();
                }

                AppendRaw(sb, text[_pos++]);
            }
        }

        /// <summary>Keeps a newline as <c>\n</c> whether the checkout wrote LF or CRLF.</summary>
        private void AppendRaw(StringBuilder sb, char c)
        {
            if (c == '\r' && Peek() == '\n') return;
            if (c == '\n') _line++;
            sb.Append(c);
        }

        private void SkipFirstNewline()
        {
            if (StartsWith("\r\n")) { _pos += 2; _line++; }
            else if (Peek() == '\n') { _pos++; _line++; }
        }

        private bool RestOfLineIsBlank()
        {
            var i = _pos;
            while (i < text.Length && text[i] is ' ' or '\t') i++;
            return i < text.Length && text[i] is '\n' or '\r';
        }

        private void SkipWhitespaceAndNewlines()
        {
            while (!AtEnd && Peek() is ' ' or '\t' or '\r' or '\n')
            {
                if (Peek() == '\n') _line++;
                _pos++;
            }
        }

        private void ReadEscape(StringBuilder sb)
        {
            if (AtEnd) throw Error("unterminated escape");

            var e = text[_pos++];
            switch (e)
            {
                case 'b': sb.Append('\b'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'f': sb.Append('\f'); break;
                case 'r': sb.Append('\r'); break;
                case 'e': sb.Append('\u001B'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u': sb.Append(ReadCodePoint(4)); break;
                case 'U': sb.Append(ReadCodePoint(8)); break;
                default: throw Error($"invalid escape '\\{e}'");
            }
        }

        private string ReadCodePoint(int digits)
        {
            if (_pos + digits > text.Length) throw Error("truncated unicode escape");

            var hex = text.Substring(_pos, digits);
            if (!int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var codePoint) ||
                codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
            {
                throw Error($"invalid unicode escape '{hex}'");
            }

            _pos += digits;
            return char.ConvertFromUtf32(codePoint);
        }

        private string ReadKey() => Peek() switch
        {
            '"' => ReadBasicString(),
            '\'' => ReadLiteralString(),
            _ => ReadBareKey(),
        };

        private string ReadBareKey()
        {
            var start = _pos;
            while (!AtEnd && (char.IsAsciiLetterOrDigit(Peek()) || Peek() is '_' or '-')) _pos++;
            return _pos > start ? text[start.._pos] : throw Error("expected a key");
        }

        private void Expect(char c)
        {
            if (Peek() != c || AtEnd) throw Error($"expected '{c}'");
            _pos++;
        }

        private void SkipSpaces()
        {
            while (!AtEnd && Peek() is ' ' or '\t') _pos++;
        }

        private void SkipTrivia(bool newlines)
        {
            while (!AtEnd)
            {
                var c = Peek();
                if (c is ' ' or '\t') { _pos++; continue; }
                if (c == '#') { while (!AtEnd && Peek() != '\n') _pos++; continue; }
                if (newlines && c == '\r' && Peek(1) == '\n') { _pos++; continue; }
                if (newlines && c == '\n') { _pos++; _line++; continue; }
                return;
            }
        }

        private void EndOfLine()
        {
            SkipTrivia(newlines: false);
            if (AtEnd) return;
            if (Peek() == '\r' && Peek(1) == '\n') { _pos += 2; _line++; return; }
            if (Peek() == '\n') { _pos++; _line++; return; }
            throw Error("expected the end of the line");
        }

        private FormatException Error(string message) => new($"line {_line}: {message}");
    }
}
