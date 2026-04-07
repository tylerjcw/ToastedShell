using System.Globalization;
using System.Text;

namespace Tosh.Core;

public static class TomlParser
{
    public static Dictionary<string, object?> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new TomlReader(text);
        return reader.ParseDocument();
    }

    public static string Serialize(object? value, bool indent = true)
    {
        var builder = new StringBuilder();
        var normalized = ShellDataSerializer.Normalize(value);

        if (normalized is IDictionary<string, object?> root)
        {
            SerializeTable(builder, root, "", indent, isRoot: true);
        }
        else
        {
            builder.Append("value = ");
            SerializeValue(builder, normalized, 0, indent);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static void SerializeTable(StringBuilder builder, IDictionary<string, object?> table, string prefix, bool indent, bool isRoot)
    {
        var simpleEntries = new List<KeyValuePair<string, object?>>();
        var tableEntries = new List<KeyValuePair<string, object?>>();
        var arrayOfTableEntries = new List<KeyValuePair<string, object?>>();

        foreach (var (key, val) in table)
        {
            var normalized = val;

            if (normalized is IDictionary<string, object?> dict)
            {
                tableEntries.Add(new(key, dict));
            }
            else if (normalized is object?[] arr && arr.Length > 0 && arr[0] is IDictionary<string, object?>)
            {
                arrayOfTableEntries.Add(new(key, arr));
            }
            else
            {
                simpleEntries.Add(new(key, normalized));
            }
        }

        foreach (var (key, val) in simpleEntries)
        {
            builder.Append(EscapeKey(key));
            builder.Append(" = ");
            SerializeValue(builder, val, 0, indent);
            builder.AppendLine();
        }

        foreach (var (key, val) in tableEntries)
        {
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";
            builder.AppendLine();
            builder.Append('[');
            builder.Append(EscapeKey(fullKey));
            builder.AppendLine("]");
            SerializeTable(builder, (IDictionary<string, object?>)val!, fullKey, indent, isRoot: false);
        }

        foreach (var (key, val) in arrayOfTableEntries)
        {
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";
            var array = (object?[])val!;

            foreach (var item in array)
            {
                builder.AppendLine();
                builder.Append("[[");
                builder.Append(EscapeKey(fullKey));
                builder.AppendLine("]]");

                if (item is IDictionary<string, object?> dict)
                {
                    SerializeTable(builder, dict, fullKey, indent, isRoot: false);
                }
            }
        }
    }

    private static void SerializeValue(StringBuilder builder, object? value, int depth, bool indent)
    {
        switch (value)
        {
            case null:
                builder.Append("\"\"");
                break;
            case bool b:
                builder.Append(b ? "true" : "false");
                break;
            case long l:
                builder.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case int i:
                builder.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                if (double.IsPositiveInfinity(d)) builder.Append("inf");
                else if (double.IsNegativeInfinity(d)) builder.Append("-inf");
                else if (double.IsNaN(d)) builder.Append("nan");
                else builder.Append(d.ToString("G", CultureInfo.InvariantCulture));
                break;
            case float f:
                if (float.IsPositiveInfinity(f)) builder.Append("inf");
                else if (float.IsNegativeInfinity(f)) builder.Append("-inf");
                else if (float.IsNaN(f)) builder.Append("nan");
                else builder.Append(f.ToString("G", CultureInfo.InvariantCulture));
                break;
            case decimal dec:
                builder.Append(dec.ToString(CultureInfo.InvariantCulture));
                break;
            case DateTime dt:
                builder.Append(dt.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                builder.Append(dto.ToString("O", CultureInfo.InvariantCulture));
                break;
            case string s:
                SerializeString(builder, s);
                break;
            case IDictionary<string, object?> dict:
                SerializeInlineTable(builder, dict, depth, indent);
                break;
            case object?[] array:
                SerializeArray(builder, array, depth, indent);
                break;
            default:
                SerializeString(builder, ExternalTextSerializer.Serialize(value));
                break;
        }
    }

    private static void SerializeString(StringBuilder builder, string value)
    {
        builder.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append($"\\u{(int)c:X4}");
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private static void SerializeArray(StringBuilder builder, object?[] array, int depth, bool indent)
    {
        if (array.Length == 0)
        {
            builder.Append("[]");
            return;
        }

        builder.Append('[');

        for (var i = 0; i < array.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            SerializeValue(builder, array[i], depth + 1, indent);
        }

        builder.Append(']');
    }

    private static void SerializeInlineTable(StringBuilder builder, IDictionary<string, object?> dict, int depth, bool indent)
    {
        builder.Append("{ ");
        var first = true;

        foreach (var (key, val) in dict)
        {
            if (!first)
            {
                builder.Append(", ");
            }

            builder.Append(EscapeKey(key));
            builder.Append(" = ");
            SerializeValue(builder, val, depth + 1, indent);
            first = false;
        }

        builder.Append(" }");
    }

    private static string EscapeKey(string key)
    {
        if (IsBareKey(key))
        {
            return key;
        }

        var builder = new StringBuilder();
        SerializeString(builder, key);
        return builder.ToString();
    }

    private static bool IsBareKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        foreach (var c in key)
        {
            if (!char.IsLetterOrDigit(c) && c is not '-' and not '_')
            {
                return false;
            }
        }

        return true;
    }

    private sealed class TomlReader
    {
        private readonly string _text;
        private int _position;

        public TomlReader(string text)
        {
            _text = text;
        }

        public Dictionary<string, object?> ParseDocument()
        {
            var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var currentTable = root;

            while (_position < _text.Length)
            {
                SkipWhitespaceAndNewlines();

                if (_position >= _text.Length)
                {
                    break;
                }

                var c = _text[_position];

                if (c == '#')
                {
                    SkipComment();
                    continue;
                }

                if (c == '[')
                {
                    if (_position + 1 < _text.Length && _text[_position + 1] == '[')
                    {
                        currentTable = ParseArrayOfTablesHeader(root);
                    }
                    else
                    {
                        currentTable = ParseTableHeader(root);
                    }

                    continue;
                }

                if (c is '\r' or '\n')
                {
                    SkipNewline();
                    continue;
                }

                var (key, value) = ParseKeyValue();
                SetNestedValue(currentTable, key, value);
                SkipWhitespace();

                if (_position < _text.Length && _text[_position] == '#')
                {
                    SkipComment();
                }

                if (_position < _text.Length && _text[_position] is '\r' or '\n')
                {
                    SkipNewline();
                }
            }

            return root;
        }

        private Dictionary<string, object?> ParseTableHeader(Dictionary<string, object?> root)
        {
            _position++; // skip [
            SkipWhitespace();
            var key = ParseKey();
            SkipWhitespace();
            Expect(']');
            SkipWhitespace();

            if (_position < _text.Length && _text[_position] == '#')
            {
                SkipComment();
            }

            return EnsureTable(root, key);
        }

        private Dictionary<string, object?> ParseArrayOfTablesHeader(Dictionary<string, object?> root)
        {
            _position += 2; // skip [[
            SkipWhitespace();
            var key = ParseKey();
            SkipWhitespace();
            Expect(']');
            Expect(']');
            SkipWhitespace();

            if (_position < _text.Length && _text[_position] == '#')
            {
                SkipComment();
            }

            return EnsureArrayOfTables(root, key);
        }

        private Dictionary<string, object?> EnsureTable(Dictionary<string, object?> root, IReadOnlyList<string> keyPath)
        {
            var current = root;

            foreach (var segment in keyPath)
            {
                if (current.TryGetValue(segment, out var existing))
                {
                    if (existing is Dictionary<string, object?> dict)
                    {
                        current = dict;
                    }
                    else if (existing is List<object?> array && array.Count > 0 && array[^1] is Dictionary<string, object?> lastDict)
                    {
                        current = lastDict;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Key '{segment}' is already defined and is not a table.");
                    }
                }
                else
                {
                    var table = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    current[segment] = table;
                    current = table;
                }
            }

            return current;
        }

        private Dictionary<string, object?> EnsureArrayOfTables(Dictionary<string, object?> root, IReadOnlyList<string> keyPath)
        {
            var current = root;

            for (var i = 0; i < keyPath.Count - 1; i++)
            {
                current = EnsureTable(current, [keyPath[i]]);
            }

            var lastKey = keyPath[^1];
            List<object?> list;

            if (current.TryGetValue(lastKey, out var existing))
            {
                if (existing is List<object?> existingList)
                {
                    list = existingList;
                }
                else
                {
                    throw new InvalidOperationException($"Key '{lastKey}' is already defined and is not an array of tables.");
                }
            }
            else
            {
                list = new List<object?>();
                current[lastKey] = list;
            }

            var newTable = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            list.Add(newTable);
            return newTable;
        }

        private (IReadOnlyList<string> Key, object? Value) ParseKeyValue()
        {
            var key = ParseKey();
            SkipWhitespace();
            Expect('=');
            SkipWhitespace();
            var value = ParseValue();
            return (key, value);
        }

        private IReadOnlyList<string> ParseKey()
        {
            var parts = new List<string> { ParseSimpleKey() };

            while (_position < _text.Length && _text[_position] == '.')
            {
                _position++;
                SkipWhitespace();
                parts.Add(ParseSimpleKey());
                SkipWhitespace();
            }

            return parts;
        }

        private string ParseSimpleKey()
        {
            if (_position >= _text.Length)
            {
                throw new InvalidOperationException("Expected a key but reached end of input.");
            }

            return _text[_position] switch
            {
                '"' => ParseBasicString(),
                '\'' => ParseLiteralString(),
                _ => ParseBareKey(),
            };
        }

        private string ParseBareKey()
        {
            var start = _position;

            while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] is '-' or '_'))
            {
                _position++;
            }

            if (_position == start)
            {
                throw new InvalidOperationException($"Expected a key at position {_position}.");
            }

            return _text[start.._position];
        }

        private object? ParseValue()
        {
            if (_position >= _text.Length)
            {
                throw new InvalidOperationException("Expected a value but reached end of input.");
            }

            return _text[_position] switch
            {
                '"' => _position + 2 < _text.Length && _text[_position + 1] == '"' && _text[_position + 2] == '"'
                    ? ParseMultiLineBasicString()
                    : ParseBasicString(),
                '\'' => _position + 2 < _text.Length && _text[_position + 1] == '\'' && _text[_position + 2] == '\''
                    ? ParseMultiLineLiteralString()
                    : ParseLiteralString(),
                't' or 'f' => ParseBoolean(),
                '[' => ParseArray(),
                '{' => ParseInlineTable(),
                'i' or 'n' => ParseSpecialFloat(),
                _ => ParseNumberOrDateTime(),
            };
        }

        private string ParseBasicString()
        {
            _position++; // skip opening "
            var builder = new StringBuilder();

            while (_position < _text.Length)
            {
                var c = _text[_position];

                if (c == '"')
                {
                    _position++;
                    return builder.ToString();
                }

                if (c == '\\')
                {
                    _position++;

                    if (_position >= _text.Length)
                    {
                        throw new InvalidOperationException("Unterminated escape sequence in string.");
                    }

                    builder.Append(_text[_position] switch
                    {
                        'b' => '\b',
                        't' => '\t',
                        'n' => '\n',
                        'f' => '\f',
                        'r' => '\r',
                        '"' => '"',
                        '\\' => '\\',
                        'u' => ParseUnicodeEscape(4),
                        'U' => ParseUnicodeEscape(8),
                        _ => throw new InvalidOperationException($"Unknown escape sequence: \\{_text[_position]}."),
                    });

                    _position++;
                }
                else
                {
                    builder.Append(c);
                    _position++;
                }
            }

            throw new InvalidOperationException("Unterminated string.");
        }

        private char ParseUnicodeEscape(int digits)
        {
            _position++; // skip u/U
            var start = _position;
            _position += digits;

            if (_position > _text.Length)
            {
                throw new InvalidOperationException("Unterminated unicode escape sequence.");
            }

            var hex = _text[start.._position];
            _position--; // will be incremented by caller

            return (char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private string ParseMultiLineBasicString()
        {
            _position += 3; // skip """

            if (_position < _text.Length && _text[_position] == '\n')
            {
                _position++;
            }
            else if (_position + 1 < _text.Length && _text[_position] == '\r' && _text[_position + 1] == '\n')
            {
                _position += 2;
            }

            var builder = new StringBuilder();

            while (_position < _text.Length)
            {
                if (_text[_position] == '"' && _position + 2 < _text.Length && _text[_position + 1] == '"' && _text[_position + 2] == '"')
                {
                    _position += 3;
                    return builder.ToString();
                }

                if (_text[_position] == '\\')
                {
                    _position++;

                    if (_position < _text.Length && (_text[_position] is '\n' or '\r' or ' ' or '\t'))
                    {
                        while (_position < _text.Length && _text[_position] is '\n' or '\r' or ' ' or '\t')
                        {
                            _position++;
                        }

                        continue;
                    }

                    if (_position >= _text.Length)
                    {
                        throw new InvalidOperationException("Unterminated escape sequence.");
                    }

                    builder.Append(_text[_position] switch
                    {
                        'b' => '\b',
                        't' => '\t',
                        'n' => '\n',
                        'f' => '\f',
                        'r' => '\r',
                        '"' => '"',
                        '\\' => '\\',
                        _ => throw new InvalidOperationException($"Unknown escape sequence: \\{_text[_position]}."),
                    });

                    _position++;
                }
                else
                {
                    builder.Append(_text[_position]);
                    _position++;
                }
            }

            throw new InvalidOperationException("Unterminated multi-line string.");
        }

        private string ParseLiteralString()
        {
            _position++; // skip opening '
            var start = _position;

            while (_position < _text.Length && _text[_position] != '\'')
            {
                _position++;
            }

            if (_position >= _text.Length)
            {
                throw new InvalidOperationException("Unterminated literal string.");
            }

            var result = _text[start.._position];
            _position++; // skip closing '
            return result;
        }

        private string ParseMultiLineLiteralString()
        {
            _position += 3; // skip '''

            if (_position < _text.Length && _text[_position] == '\n')
            {
                _position++;
            }
            else if (_position + 1 < _text.Length && _text[_position] == '\r' && _text[_position + 1] == '\n')
            {
                _position += 2;
            }

            var start = _position;

            while (_position < _text.Length)
            {
                if (_text[_position] == '\'' && _position + 2 < _text.Length && _text[_position + 1] == '\'' && _text[_position + 2] == '\'')
                {
                    var result = _text[start.._position];
                    _position += 3;
                    return result;
                }

                _position++;
            }

            throw new InvalidOperationException("Unterminated multi-line literal string.");
        }

        private bool ParseBoolean()
        {
            if (_text.AsSpan(_position).StartsWith("true"))
            {
                _position += 4;
                return true;
            }

            if (_text.AsSpan(_position).StartsWith("false"))
            {
                _position += 5;
                return false;
            }

            throw new InvalidOperationException($"Expected 'true' or 'false' at position {_position}.");
        }

        private object ParseSpecialFloat()
        {
            if (_text.AsSpan(_position).StartsWith("inf") || _text.AsSpan(_position).StartsWith("+inf"))
            {
                _position += _text[_position] == '+' ? 4 : 3;
                return double.PositiveInfinity;
            }

            if (_text.AsSpan(_position).StartsWith("-inf"))
            {
                _position += 4;
                return double.NegativeInfinity;
            }

            if (_text.AsSpan(_position).StartsWith("nan") || _text.AsSpan(_position).StartsWith("+nan") || _text.AsSpan(_position).StartsWith("-nan"))
            {
                _position += _text[_position] is '+' or '-' ? 4 : 3;
                return double.NaN;
            }

            return ParseNumberOrDateTime();
        }

        private object ParseNumberOrDateTime()
        {
            var start = _position;

            // Consume the token
            while (_position < _text.Length && _text[_position] is not (' ' or '\t' or '\n' or '\r' or ',' or ']' or '}' or '#'))
            {
                _position++;
            }

            var token = _text[start.._position].Replace("_", "", StringComparison.Ordinal);

            // Try date/time formats
            if (DateTimeOffset.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                return dto;
            }

            if (DateTime.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt;
            }

            // Hex, octal, binary
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                return hex;
            }

            if (token.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Convert.ToInt64(token[2..], 8);
                }
                catch
                {
                    // fall through
                }
            }

            if (token.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return Convert.ToInt64(token[2..], 2);
                }
                catch
                {
                    // fall through
                }
            }

            // Integer
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                return integer;
            }

            // Float
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating))
            {
                return floating;
            }

            throw new InvalidOperationException($"Could not parse value: '{_text[start.._position]}'.");
        }

        private List<object?> ParseArray()
        {
            _position++; // skip [
            var items = new List<object?>();

            while (true)
            {
                SkipWhitespaceAndNewlines();
                SkipComments();

                if (_position >= _text.Length)
                {
                    throw new InvalidOperationException("Unterminated array.");
                }

                if (_text[_position] == ']')
                {
                    _position++;
                    return items;
                }

                items.Add(ParseValue());
                SkipWhitespaceAndNewlines();
                SkipComments();

                if (_position < _text.Length && _text[_position] == ',')
                {
                    _position++;
                }
            }
        }

        private Dictionary<string, object?> ParseInlineTable()
        {
            _position++; // skip {
            var table = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            SkipWhitespace();

            if (_position < _text.Length && _text[_position] == '}')
            {
                _position++;
                return table;
            }

            while (true)
            {
                SkipWhitespace();
                var (key, value) = ParseKeyValue();
                SetNestedValue(table, key, value);
                SkipWhitespace();

                if (_position >= _text.Length)
                {
                    throw new InvalidOperationException("Unterminated inline table.");
                }

                if (_text[_position] == '}')
                {
                    _position++;
                    return table;
                }

                if (_text[_position] == ',')
                {
                    _position++;
                }
            }
        }

        private static void SetNestedValue(Dictionary<string, object?> table, IReadOnlyList<string> keyPath, object? value)
        {
            var current = table;

            for (var i = 0; i < keyPath.Count - 1; i++)
            {
                var segment = keyPath[i];

                if (current.TryGetValue(segment, out var existing))
                {
                    if (existing is Dictionary<string, object?> dict)
                    {
                        current = dict;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Key '{segment}' is already defined and is not a table.");
                    }
                }
                else
                {
                    var nested = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    current[segment] = nested;
                    current = nested;
                }
            }

            current[keyPath[^1]] = value;
        }

        private void Expect(char expected)
        {
            if (_position >= _text.Length || _text[_position] != expected)
            {
                throw new InvalidOperationException($"Expected '{expected}' at position {_position}.");
            }

            _position++;
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length && _text[_position] is ' ' or '\t')
            {
                _position++;
            }
        }

        private void SkipWhitespaceAndNewlines()
        {
            while (_position < _text.Length && _text[_position] is ' ' or '\t' or '\r' or '\n')
            {
                _position++;
            }
        }

        private void SkipNewline()
        {
            if (_position < _text.Length && _text[_position] == '\r')
            {
                _position++;
            }

            if (_position < _text.Length && _text[_position] == '\n')
            {
                _position++;
            }
        }

        private void SkipComment()
        {
            while (_position < _text.Length && _text[_position] is not '\r' and not '\n')
            {
                _position++;
            }
        }

        private void SkipComments()
        {
            while (_position < _text.Length && _text[_position] == '#')
            {
                SkipComment();
                SkipWhitespaceAndNewlines();
            }
        }
    }
}
