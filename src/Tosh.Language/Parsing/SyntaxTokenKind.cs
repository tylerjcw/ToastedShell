namespace Tosh.Language.Parsing;

public enum SyntaxTokenKind
{
    EndOfFile,
    Pipe,
    OpenParen,
    CloseParen,
    OpenBrace,
    CloseBrace,
    OpenBracket,
    CloseBracket,
    Comma,
    Semicolon,
    Bareword,
    String,
    Number,
    Boolean,
    Null,

    // Operators and combinators
    Ampersand,              // & (background)
    QuestionQuestion,       // ?? (null-coalescing)
    QuestionDot,            // ?. (null-safe member access)

    // Comparison operators (previously barewords, now distinct tokens)
    GreaterThanEqual,       // >=
    LessThanEqual,          // <=
    BangEqual,              // !=
    BangTilde,              // !~
    Bang,                   // !

    // Redirection
    GreaterThan,            // >
    GreaterThanGreaterThan, // >>
    LessThan,              // <
    LessThanLessThanLessThan, // <<< (here-string)

    // Expansion prefixes
    DollarOpenParen,        // $(

    // Interpolated strings
    InterpolatedString,     // $"...{expr}..."

    // Range
    DotDot,                 // ..

    // Process substitution
    LessThanOpenParen,
    DoublePipe,
    DoubleAmpersand,      // <(

    // Unit literals (e.g. 100`m, 9.8`m/s^2)
    UnitLiteral,

    // Doc comments (## lines)
    DocComment,
}
