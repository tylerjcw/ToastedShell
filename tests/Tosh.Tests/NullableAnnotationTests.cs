using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A nullable annotation on a declaration — `TS-P2-120`.
///
/// `var x: int? = null` failed with `tosh.bind.unknown_command` — *"Command 'var' is not a
/// registered builtin"* — at column 1, a message naming neither the type nor the
/// annotation. `var x: Nonexistent? = null` parsed fine, and `func f(a: int?)` worked, so
/// the form was understood everywhere except a `var` declaration naming a built-in alias.
///
/// The cause was out of all proportion to the symptom. `LooksLikePotentialTypeName` decides
/// whether `var` opens a *declaration*, and the lexer keeps a nullable suffix inside the
/// bareword — so `int?` arrived whole and failed `IsValidIdentifier` on the `?`. `Int32?`
/// passed only because the CLR-name heuristic accepts a capitalised word, which is exactly
/// why the defect looked specific to built-in aliases: their spellings are lowercase, and
/// nothing else would take them.
/// </summary>
public sealed class NullableAnnotationTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>
    /// Both declaration keywords, for the alias spellings that failed and the CLR ones that
    /// did not.
    /// </summary>
    [Theory]
    [InlineData("var x: int? = null\n($x == null)", "True")]
    [InlineData("var x: string? = null\n($x == null)", "True")]
    [InlineData("var x: bool? = null\n($x == null)", "True")]
    [InlineData("const c: int? = null\n($c == null)", "True")]
    [InlineData("var x: Int32? = null\n($x == null)", "True")]
    public async Task A_nullable_annotation_parses_in_a_declaration(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));

    /// <summary>
    /// It is an annotation, not decoration: the declared type still holds for a value.
    /// </summary>
    [Fact]
    public async Task A_nullable_annotation_still_accepts_its_type()
        => Assert.Equal("5", await RunAsync("var x: int? = 5\n$x"));

    /// <summary>
    /// And still refuses another one. Without this the fix could have been "accept the
    /// annotation and ignore it".
    /// </summary>
    [Fact]
    public async Task A_nullable_annotation_still_refuses_a_wrong_type()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync("var x: int? = \"abc\""));
    }

    /// <summary>
    /// The non-nullable form still refuses null, so `?` continues to mean something.
    /// </summary>
    [Fact]
    public async Task A_non_nullable_annotation_still_refuses_null()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync("var x: int = null"));
    }

    /// <summary>
    /// An unknown type reports as an unknown *type*, not as an unknown command. That was
    /// the item's other complaint: the diagnostic named the statement keyword and pointed
    /// at column 1.
    /// </summary>
    [Fact]
    public async Task An_unknown_nullable_type_reports_as_a_type()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => engine.ExecuteToListAsync("var x: Nonexistent? = null"));

        Assert.Contains("Nonexistent", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("registered builtin", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The positions that already worked, kept as controls.
    /// </summary>
    [Theory]
    [InlineData("func nf(a: int?) { return 1 }\n(nf 1)", "1")]
    [InlineData("class Nc { prop P: int? = null }\n((new Nc()).P == null)", "True")]
    public async Task The_positions_that_already_worked_still_do(string source, string expected)
        => Assert.Equal(expected, await RunAsync(source));
}
