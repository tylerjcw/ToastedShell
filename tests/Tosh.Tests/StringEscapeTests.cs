using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A double-quoted string and an ANSI-C string take the same escapes — `TOAST-0027`.
/// </summary>
/// <remarks>
/// <para>
/// There were two escape tables and nothing said which one a string was read with.
/// `ReadEscapeSequence` served `"..."` and knew the control escapes;
/// `ReadAnsiCEscapeSequence` served `$'...'` and knew those plus `\xHH` and `\uHHHH`. So
/// `"\u00E9"` was six characters and `$'\u00E9'` was one, and the difference was silent —
/// the unknown-escape fallback keeps whatever it did not recognise.
/// </para>
/// <para>
/// It reached the specification as a false claim, because every probe had gone through
/// `tosh -c`, which performs its own shell-level escape processing: the same line answered
/// `1` on the command line and `6` in a script.
/// </para>
/// <para>
/// The repair gives both kinds the same two escapes rather than reporting the difference.
/// That does not make a backslash newly dangerous — `\t` and `\n` already resolved in a
/// double-quoted string, so `"C:\path\to"` already contained a tab, which is pinned below
/// so nobody repairs the wrong thing.
/// </para>
/// </remarks>
public sealed class StringEscapeTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    /// <summary>The two string kinds agree, which is the whole of this item.</summary>
    [Theory]
    [InlineData("\\u00E9", "1")]
    [InlineData("e\\u0301", "2")]
    [InlineData("\\uD83D\\uDC4B", "2")]
    [InlineData("\\x41", "1")]
    [InlineData("a\\nb", "3")]
    public async Task Both_string_kinds_take_the_same_escapes(string body, string expectedLength)
    {
        Assert.Equal(expectedLength, await RunAsync($"(\"{body}\".Length)"));
        Assert.Equal(expectedLength, await RunAsync($"($'{body}'.Length)"));
    }

    /// <summary>And produce the same value, not merely the same length.</summary>
    [Theory]
    [InlineData("\\u00E9", "\u00E9")]
    [InlineData("\\x41", "A")]
    [InlineData("\\uD83D\\uDC4B", "\uD83D\uDC4B")]
    public async Task The_escape_resolves_to_the_character_it_names(string body, string expected)
    {
        Assert.Equal(expected, await RunAsync($"echo $\"{{\"{body}\"}}\""));
        Assert.Equal(expected, await RunAsync($"echo $\"{{$'{body}'}}\""));
    }

    /// <summary>
    /// An escape that names nothing is still kept, and that is what protects ordinary text.
    /// </summary>
    /// <remarks>
    /// The reason the strictest option was not taken: reporting every unrecognised escape
    /// would make `"\q"` an error and require `\\` for a literal backslash.
    /// </remarks>
    [Theory]
    [InlineData("\\q", "2")]
    [InlineData("\\z", "2")]
    // A hex escape with no digits after it is not an escape either.
    [InlineData("\\u", "2")]
    [InlineData("\\x", "2")]
    public async Task An_unrecognised_escape_is_kept_as_written(string body, string expectedLength)
        => Assert.Equal(expectedLength, await RunAsync($"(\"{body}\".Length)"));

    /// <summary>
    /// A backslash path was already unsafe, and still is — pinned so it is not mistaken
    /// for something this change caused.
    /// </summary>
    /// <remarks>
    /// `\t` resolves in a double-quoted string and always did, so `"C:\path\to"` is
    /// `C:\path` + TAB + `o`: nine characters, not ten. Anyone reaching for this case as
    /// an argument about `\u` is looking at a hazard that predates it.
    /// </remarks>
    [Fact]
    public async Task A_windows_path_was_already_mangled_by_the_control_escapes()
    {
        Assert.Equal("9", await RunAsync("(\"C:\\path\\to\".Length)"));
        Assert.Equal("C:\\path\to", await RunAsync("echo $\"{\"C:\\path\\to\"}\""));

        // The safe spellings, both unaffected.
        Assert.Equal("10", await RunAsync("(\"C:\\\\path\\\\to\".Length)"));
        Assert.Equal("10", await RunAsync("('C:\\path\\to'.Length)"));
    }
}
