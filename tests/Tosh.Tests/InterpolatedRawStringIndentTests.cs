using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A triple-quoted string is indented to suit the code around it, not the output.
/// </summary>
/// <remarks>
/// <para>
/// The closing quote says where the left margin is: text level with it starts at column
/// zero, and text further in keeps the difference. The plain form did that; the interpolated
/// form measured the closing line and threw the number away, so every
/// <c>$"""…"""</c> came out wearing the indentation of the function it was written in.
/// </para>
/// <para>
/// Interpolation is why the two differ at all. It breaks the text into parts, and the
/// closing line — the one that decides the margin — is not seen until the last of them.
/// </para>
/// </remarks>
public sealed class InterpolatedRawStringIndentTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public InterpolatedRawStringIndentTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private async Task<string> Value(string source)
    {
        var results = await new ToshEngine(_runtime.Language).ExecuteToListAsync(source);

        return results.OfType<string>().Single();
    }

    /// <summary>Level with the closing quote means column zero.</summary>
    [Fact]
    public async Task Text_level_with_the_closing_quote_is_not_indented()
    {
        var text = await Value(""""
            func demo() {
                var who = "world"
                return $"""
                    alpha {$who}
                    beta
                    """
            }
            demo
            """");

        Assert.Equal("alpha world\nbeta", text.ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// Text further in than the closing quote keeps the difference.
    /// </summary>
    /// <remarks>
    /// The margin is a measurement, not a strip: a block deliberately inset stays inset, and
    /// that is how a nested listing keeps its shape.
    /// </remarks>
    [Fact]
    public async Task Text_further_in_than_the_closing_quote_keeps_the_difference()
    {
        var text = await Value(""""
            func demo() {
                var who = "world"
                return $"""
                        alpha {$who}
                        beta
                    """
            }
            demo
            """");

        Assert.Equal("    alpha world\n    beta", text.ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// A blank line stays blank rather than becoming whitespace.
    /// </summary>
    /// <remarks>
    /// A line with less indentation than the margin gives up what it has instead of eating
    /// the text after it, which is what makes an empty line in the middle of a block
    /// harmless.
    /// </remarks>
    [Fact]
    public async Task A_blank_line_stays_blank()
    {
        var text = await Value(""""
            func demo() {
                var who = "world"
                return $"""
                    alpha {$who}

                    beta
                    """
            }
            demo
            """");

        Assert.Equal("alpha world\n\nbeta", text.ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// A line that begins with an interpolated value is still a line.
    /// </summary>
    /// <remarks>
    /// Its indentation sits in the literal *before* the hole, and the walk has to carry
    /// "at the start of a line" across the boundary to find it.
    /// </remarks>
    [Fact]
    public async Task A_line_starting_with_a_value_is_trimmed_too()
    {
        var text = await Value(""""
            func demo() {
                var who = "world"
                return $"""
                    alpha
                    {$who} beta
                    """
            }
            demo
            """");

        Assert.Equal("alpha\nworld beta", text.ReplaceLineEndings("\n"));
    }

    /// <summary>Indentation inside an interpolated value is that value's own business.</summary>
    /// <remarks>
    /// It was not written in the source and cannot be measured against the closing quote, so
    /// a value carrying spaces keeps them.
    /// </remarks>
    [Fact]
    public async Task Spaces_inside_a_value_are_left_alone()
    {
        var text = await Value(""""
            func demo() {
                var padded = "    indented"
                return $"""
                    {$padded}
                    """
            }
            demo
            """");

        Assert.Equal("    indented", text);
    }

    /// <summary>The single-quoted form behaves identically.</summary>
    [Fact]
    public async Task The_single_quoted_form_agrees()
    {
        var text = await Value(""""
            func demo() {
                var who = "world"
                return $'''
                    alpha {$who}
                    beta
                    '''
            }
            demo
            """");

        Assert.Equal("alpha world\nbeta", text.ReplaceLineEndings("\n"));
    }

    /// <summary>And the plain form, which already did this, still does.</summary>
    [Fact]
    public async Task The_plain_form_is_unchanged()
    {
        var text = await Value(""""
            func demo() {
                return """
                    alpha
                    beta
                    """
            }
            demo
            """");

        Assert.Equal("alpha\nbeta", text.ReplaceLineEndings("\n"));
    }
}
