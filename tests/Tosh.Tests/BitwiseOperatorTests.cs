using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Bitwise operators, spelled as words, and enums declared `flags`.
///
/// `TS-P3-14`. The language had no way to express a bit flag at all: `&amp;` is the
/// background operator and the function-reference sigil, `|` separates pipeline
/// stages, and there were no word forms. So `SDL_INIT_VIDEO | SDL_INIT_AUDIO` —
/// the shape of every C API whose constants are flags — had no spelling.
///
/// The evidence was in the user's own library: `Native.tosh` carried a `Bits`
/// hermit class folding OR/AND/SHL/SHR **bit by bit with division and modulo**,
/// whose doc comment said it existed because the language could not say this. Its
/// `Or` was written to be idempotent precisely because callers told to "just add
/// the flags" write a bug.
///
/// Two decisions depart from C, both deliberately:
///
/// * **Word forms**, joining the existing `and` / `or` / `not` / `is` / `in`
///   family, because the symbols are taken and would be ambiguous even if freed.
/// * **Tighter than comparison**, so `$f band Mask == 0` groups as
///   `($f band Mask) == 0` — the reading the text suggests. C's ordering is a
///   known trap; its *relative* order among `shl`/`shr` → `band` → `bxor` → `bor`
///   is kept, since that part is not.
///
/// A combined value over a `flags` enum keeps the enum type and renders as member
/// names. Over a plain enum it collapses to the underlying integer: the author did
/// not declare the type combinable, so the result is a number, not a member.
/// </summary>
public class BitwiseOperatorTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(v => v?.ToString() ?? "null"));
    }

    // ── The operators themselves ────────────────────────────────────────────

    [Theory]
    [InlineData("6 band 3", "2")]
    [InlineData("4 bor 2", "6")]
    [InlineData("6 bxor 3", "5")]
    [InlineData("1 shl 3", "8")]
    [InlineData("8 shr 3", "1")]
    [InlineData("bnot 0", "-1")]
    [InlineData("bnot 5", "-6")]
    // Shifting by zero is identity, not an error.
    [InlineData("5 shl 0", "5")]
    [InlineData("5 shr 0", "5")]
    // Negative operands: `shr` is arithmetic, so the sign is preserved.
    [InlineData("-8 shr 1", "-4")]
    [InlineData("-1 band 255", "255")]
    [InlineData("0x30 band 0x10", "16")]
    public async Task An_operator_computes_its_bits(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));

    /// <summary>
    /// The result stays `Int32` while it fits and widens when it does not, rather
    /// than wrapping — the same rule the arithmetic operators follow.
    /// </summary>
    [Theory]
    [InlineData("(6 band 3).GetType().Name", "Int32")]
    [InlineData("(2147483647 shl 1).GetType().Name", "Int64")]
    [InlineData("2147483647 shl 1", "4294967294")]
    public async Task A_result_widens_only_when_it_must(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));

    // ── Precedence ──────────────────────────────────────────────────────────

    /// <summary>
    /// The whole reason for choosing a non-C ordering. Each case would answer
    /// differently under C's precedence, so these pin the decision and not merely
    /// the arithmetic.
    /// </summary>
    [Theory]
    // Tighter than comparison: C groups this as `6 band (3 == 2)`.
    [InlineData("6 band 3 == 2", "True")]
    [InlineData("4 bor 2 > 3", "True")]
    // Looser than additive: `1 shl (2 + 1)`, which is C's ordering here.
    [InlineData("1 shl 2 + 1", "8")]
    // `band` binds tighter than `bor`, so this is `1 bor (3 band 2)`.
    [InlineData("1 bor 3 band 2", "3")]
    // …and `bxor` sits between them: `1 bor (2 bxor 3)`.
    [InlineData("1 bor 2 bxor 3", "1")]
    // Unary binds tighter than additive, so `bnot 5 + 1` is `(bnot 5) + 1` — the
    // same grouping arithmetic negation gets, which is the point: `- 5 + 1` is
    // also -4 rather than -6.
    [InlineData("bnot 5 + 1", "-5")]
    [InlineData("- 5 + 1", "-4")]
    [InlineData("bnot (5 + 1)", "-7")]
    public async Task Precedence_groups_the_way_the_text_reads(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));

    /// <summary>
    /// A bare binary expression as a whole statement, and as the right-hand side of
    /// an assignment.
    ///
    /// This is not redundant with the cases above. `IsAnyOperatorToken` and six
    /// separate "does this look like an expression?" scans enumerate the operator
    /// predicates by hand, and `TS-P2-105` is what happens when one is missed:
    /// `$x as int` *alone* stopped parsing while every expression containing a
    /// second operator still worked. A lone operator is the shape that catches it.
    /// </summary>
    [Theory]
    [InlineData("var a = 4\nvar b = 3\n$a bor $b", "7")]
    [InlineData("var a = 6\nvar b = 3\n$a band $b", "2")]
    [InlineData("var a = 1\nvar r = ($a shl 4)\n$r", "16")]
    [InlineData("var a = 1\nvar r = $a shl 4\n$r", "16")]
    public async Task A_lone_bitwise_expression_parses(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    // ── Flags enums ─────────────────────────────────────────────────────────

    private const string Flags = """
        flags enum InitFlag: int { None = 0, Video = 1, Audio = 2, Timer = 4 }

        """;

    /// <summary>
    /// The point of the `flags` modifier: the combination keeps the enum type and
    /// reads back as names. Asserting the number alone would pass on a fix that
    /// returned a bare integer.
    /// </summary>
    [Fact]
    public async Task Combining_flags_keeps_the_enum_and_names_the_bits()
    {
        Assert.Equal("Video, Audio", await RunAsync(Flags + "InitFlag.Video bor InitFlag.Audio"));
        Assert.Equal("ToshEnumValue", await RunAsync(Flags + "(InitFlag.Video bor InitFlag.Audio).GetType().Name"));
        Assert.Equal("3", await RunAsync(Flags + "(InitFlag.Video bor InitFlag.Audio) as int"));
    }

    /// <summary>
    /// Names are composed from the underlying bits in declaration order rather than
    /// concatenated where the operator ran, so `band` and `bxor` name their results
    /// correctly too — including the zero member.
    /// </summary>
    [Theory]
    [InlineData("InitFlag.Video bor InitFlag.Timer", "Video, Timer")]
    [InlineData("(InitFlag.Video bor InitFlag.Audio) band InitFlag.Video", "Video")]
    [InlineData("InitFlag.Video bxor InitFlag.Video", "None")]
    [InlineData("InitFlag.Video bor InitFlag.Audio bor InitFlag.Timer", "Video, Audio, Timer")]
    public async Task A_combined_value_names_itself_from_its_bits(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(Flags + expression));

    /// <summary>
    /// `flags` composes with the other declaration modifiers, the way `hermit`
    /// composes with `class`.
    ///
    /// It has to be listed among the words a modifier may precede, or the modifier
    /// scan stops at `export` and the declaration is read as something else
    /// entirely: `export flags enum` reported
    /// `tosh.parser.variable_references_require_dollar` pointing at the *first
    /// member*, because the body was being parsed as ordinary statements. Every
    /// real flag enum is exported, so the bare form working proved very little.
    /// </summary>
    [Theory]
    [InlineData("flags enum E: int { A = 1, B = 2 }\nE.A bor E.B", "A, B")]
    [InlineData("export flags enum E: int { A = 1, B = 2 }\nE.A bor E.B", "A, B")]
    [InlineData("module M { export flags enum E: int { A = 1, B = 2 } }\nM.E.A bor M.E.B", "A, B")]
    // A `uint` underlying type, which is what every FFI flag enum uses.
    [InlineData("export flags enum E: uint { A = 0x10, B = 0x20 }\n(E.A bor E.B) as uint", "48")]
    public async Task The_flags_modifier_composes_with_the_others(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>`has` in both directions — one alone would pass on a constant.</summary>
    [Fact]
    public async Task Has_tests_membership()
    {
        const string combined = "var f = (InitFlag.Video bor InitFlag.Audio)\n";

        Assert.Equal("True", await RunAsync(Flags + combined + "$f has InitFlag.Audio"));
        Assert.Equal("False", await RunAsync(Flags + combined + "$f has InitFlag.Timer"));
        // A composite flag is present only when wholly present.
        Assert.Equal("True", await RunAsync(Flags + combined + "$f has (InitFlag.Video bor InitFlag.Audio)"));
        Assert.Equal("False", await RunAsync(Flags + combined + "$f has (InitFlag.Video bor InitFlag.Timer)"));
        // A zero flag is vacuously present, as `Enum.HasFlag` and the hand-written
        // `Bits.Has` this replaces both have it.
        Assert.Equal("True", await RunAsync(Flags + combined + "$f has InitFlag.None"));
        // The reason `has` binds tighter than comparison as well.
        Assert.Equal("True", await RunAsync(Flags + combined + "$f has InitFlag.Video == true"));
    }

    /// <summary>
    /// A plain enum still computes, but yields the underlying integer: `flags` is a
    /// claim the author makes, and without it a combination is not a member.
    /// </summary>
    [Fact]
    public async Task A_plain_enum_yields_a_number()
    {
        const string plain = "enum Plain: int { A = 1, B = 2 }\n";

        Assert.Equal("3", await RunAsync(plain + "Plain.A bor Plain.B"));
        Assert.Equal("Int32", await RunAsync(plain + "(Plain.A bor Plain.B).GetType().Name"));
    }

    /// <summary>
    /// Mixing two enum types is refused, mirroring the rule `ToshEnumValue`
    /// already applies to ordering. The message names both types, because a
    /// silent widening to `int` here is exactly the bug `flags` exists to prevent.
    /// </summary>
    [Fact]
    public async Task Members_of_two_enums_cannot_be_combined()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            """
            flags enum A: int { X = 1 }
            flags enum B: int { Y = 2 }
            A.X bor B.Y
            """));

        Assert.Contains("'A'", exception.Message);
        Assert.Contains("'B'", exception.Message);
    }

    // ── Controls ────────────────────────────────────────────────────────────

    /// <summary>
    /// The logical words are untouched. Six new operator words went into
    /// `NormalizeBinaryOperator` and seven scan sites; `and` and `or` answering
    /// bitwise results would be the way that goes wrong.
    /// </summary>
    [Theory]
    [InlineData("true and false", "False")]
    [InlineData("true or false", "True")]
    [InlineData("not true", "False")]
    [InlineData("6 and 3", "True")]
    public async Task The_logical_words_still_mean_what_they_did(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(expression));

    /// <summary>
    /// And the symbols the word forms exist to avoid still do their own jobs: `|`
    /// pipes, `&amp;` takes a function reference.
    /// </summary>
    [Fact]
    public async Task The_taken_symbols_are_unaffected()
    {
        Assert.Equal("3", await RunAsync("[1, 2, 3] | count"));
        Assert.Equal("6", await RunAsync("func double(n: int) -> int => ($n * 2)\nvar f = &double\n$f(3)"));
    }
}
