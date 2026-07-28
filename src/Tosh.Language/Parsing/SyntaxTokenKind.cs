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

    // Comprehension operator
    LessThanPipe,               // <|

    FatArrow,                   // =>

    // Paired collection-literal delimiters (TS-P2-25).
    //
    // `{` opens a block and nothing else; each literal kind carries its own
    // pair, so the opening token alone identifies the construct and the
    // structural pass needs no lookahead. Both characters must be adjacent —
    // `{ |` is not an opener and `| }` is not a closer — which is what lets an
    // interior pipeline or modulo keep its ordinary meaning.
    OpenBraceColon,             // {:  set
    ColonCloseBrace,            // :}
    OpenBracePipe,              // {|  record
    PipeCloseBrace,             // |}
    OpenBracePercent,           // {%  dict
    PercentCloseBrace,          // %}
}
