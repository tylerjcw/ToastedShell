namespace Tosh.Language.Parsing;

/// <summary>
/// The single definition of where a comment starts in TōSh source.
///
/// TōSh has three comment forms, matched in this order:
/// <list type="number">
///   <item><c>##{ … }##</c> — block comment, may span lines.</item>
///   <item><c>## …</c> — doc comment, attaches to the next declaration.</item>
///   <item><c># …</c> — ordinary line comment.</item>
/// </list>
///
/// A lone <c>#</c> opens a line comment only when it stands alone as a word:
/// it must be at the start of a word (start of line, or preceded by whitespace)
/// <em>and</em> followed by whitespace or the end of the line. Anywhere else it
/// is an ordinary bareword character, so <c>#ff0000</c>, <c>issue#42</c>,
/// <c>http://host/p#frag</c>, and <c>C#</c> all need no quoting.
///
/// The word-start half of that test is the POSIX shell rule — bash and zsh also
/// refuse to start a comment mid-word, which is why <c>echo issue#42</c> prints
/// <c>issue#42</c> in both. Requiring whitespace to follow as well is the part
/// TōSh adds, and it is what frees <c>#ff0000</c> from needing quotes.
///
/// <c>##</c> is unconditional: doc comments and <c>##{ }##</c> blocks keep their
/// meaning regardless of what surrounds them.
///
/// Every component that scans for comments — the lexer, the REPL highlighter and
/// line editor, Tōme's colorizer, and the LSP's semantic tokens — routes through
/// here so the rule cannot drift between them.
/// </summary>
public static class ToshCommentSyntax
{
    /// <summary>
    /// True when the character after a lone <c>#</c> permits it to open a comment.
    /// <c>'\0'</c> stands for end-of-input, which lexers report by convention.
    /// </summary>
    public static bool IsLineCommentTerminator(char next)
        => next is ' ' or '\t' or '\r' or '\n' or '\0';

    /// <summary>
    /// True when <paramref name="previous"/> puts a <c>#</c> at the start of a
    /// word. <c>'\0'</c> stands for start-of-input.
    /// </summary>
    public static bool IsWordStartBoundary(char previous)
        => previous is ' ' or '\t' or '\r' or '\n' or '\0';

    /// <summary>
    /// True when the <c>#</c> at <paramref name="index"/> opens a comment of any
    /// form. Callers are responsible for having excluded string context first.
    /// </summary>
    public static bool OpensComment(string text, int index)
    {
        if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length) return false;
        if (text[index] != '#') return false;

        var next = index + 1 < text.Length ? text[index + 1] : '\0';

        // '##' always opens a comment — doc comment or '##{' block.
        if (next == '#') return true;

        var previous = index > 0 ? text[index - 1] : '\0';
        return IsWordStartBoundary(previous) && IsLineCommentTerminator(next);
    }

    /// <summary>
    /// Index of the first character of a comment on <paramref name="line"/>, or
    /// <c>-1</c> when the line has none. Skips over single- and double-quoted
    /// spans so a <c>#</c> inside a string is never mistaken for a comment.
    /// </summary>
    public static int FindCommentStart(string line)
    {
        if (string.IsNullOrEmpty(line)) return -1;

        var inString = false;
        var stringChar = '\0';

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (inString)
            {
                if (ch == '\\' && i + 1 < line.Length) { i++; continue; }
                if (ch == stringChar) inString = false;
                continue;
            }

            if (ch is '"' or '\'')
            {
                inString = true;
                stringChar = ch;
                continue;
            }

            if (OpensComment(line, i)) return i;
        }

        return -1;
    }
}