using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A failure inside an interpolation hole is reported where the hole is written.
///
/// The hole's expression is parsed on its own — lazily, so a hole in a branch never
/// taken still never reports — but it was then *evaluated* against the enclosing
/// file's text while carrying spans measured from the hole. The two disagreed, and
/// every error inside a <c>$"…{ }"</c> landed on line 1 whatever line it was on:
/// a three-line script reported its line-3 fault against line 1, and the caret
/// underlined whatever happened to sit there.
///
/// The cost was not the wrong number. It was that the number looked right — a real
/// line, a real underline — so it sent a reader to a line that was fine.
/// </summary>
public class InterpolationHoleLocationTests
{
    /// <summary>The span the first diagnostic of a failing script carries.</summary>
    private static async Task<TextSpan> FailureSpanOf(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(source));
        var span = error.Diagnostics.First().Span;

        Assert.True(span.HasValue, "the diagnostic carried no span to locate");
        return span!.Value;
    }

    /// <summary>Line and column of a span, both 1-based, as a renderer computes them.</summary>
    private static (int Line, int Column) Position(string text, TextSpan span)
    {
        var line = 1;
        var column = 1;
        for (var index = 0; index < span.Start && index < text.Length; index++)
        {
            if (text[index] == '\n') { line++; column = 1; } else { column++; }
        }

        return (line, column);
    }

    [Fact]
    public async Task An_unknown_command_in_a_hole_reports_the_line_the_hole_is_on()
    {
        const string source = "writeline \"one\"\nwriteline \"two\"\nwriteline $\"value: {(nosuchcommand)}\"";

        var (line, _) = Position(source, await FailureSpanOf(source));

        Assert.Equal(3, line);
    }

    [Fact]
    public async Task The_caret_lands_inside_the_hole_not_at_the_start_of_the_line()
    {
        const string source = "writeline \"one\"\nwriteline \"two\"\nwriteline $\"value: {(nosuchcommand)}\"";

        var span = await FailureSpanOf(source);

        Assert.Equal("nosuchcommand", source.Substring(span.Start, span.Length));
    }

    /// <summary>
    /// A declaration above the hole used to shift nothing — the report stayed on line 1 —
    /// which is what made the old behaviour look like "the first line of the file" rather
    /// than "an offset nobody applied".
    /// </summary>
    [Fact]
    public async Task A_declaration_above_the_hole_does_not_move_the_report()
    {
        const string source =
            "func helper() {\n    return 1\n}\nwriteline \"x\"\nwriteline $\"value: {(nosuchcommand)}\"";

        var (line, _) = Position(source, await FailureSpanOf(source));

        Assert.Equal(5, line);
    }

    /// <summary>A runtime fault, not just an unresolved name, lands in the hole too.</summary>
    [Fact]
    public async Task A_runtime_fault_in_a_hole_reports_the_line_the_hole_is_on()
    {
        const string source = "writeline \"x\"\nwriteline \"y\"\nwriteline $\"value: {(1 / 0)}\"";

        var (line, _) = Position(source, await FailureSpanOf(source));

        Assert.Equal(3, line);
    }

    /// <summary>
    /// The control. A fault outside any hole was always reported correctly, and the
    /// offsetting must not disturb it.
    /// </summary>
    [Fact]
    public async Task A_fault_outside_a_hole_is_still_reported_where_it_is()
    {
        const string source = "writeline \"one\"\nwriteline \"two\"\nnosuchcommand";

        var (line, column) = Position(source, await FailureSpanOf(source));

        Assert.Equal(3, line);
        Assert.Equal(1, column);
    }
}
