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
    AmpersandAmpersand,     // &&
    PipePipe,               // ||
    Ampersand,              // & (background)
    Bang,                   // !

    // Comparison operators (previously barewords, now distinct tokens)
    GreaterThanEqual,       // >=
    LessThanEqual,          // <=
    BangEqual,              // !=

    // Redirection
    GreaterThan,            // >
    GreaterThanGreaterThan, // >>
    LessThan,              // <
    LessThanLessThanLessThan, // <<< (here-string)

    // Expansion prefixes
    DollarOpenParen,        // $(
    DollarDoubleOpenParen,  // $((
    DollarOpenBrace,        // ${

    // ANSI-C quoting
    DollarSingleQuote,      // $'

    // Process substitution
    LessThanOpenParen,      // <(
    GreaterThanOpenParen,   // >(
}
