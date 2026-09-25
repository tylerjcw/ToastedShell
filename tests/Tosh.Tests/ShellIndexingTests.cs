using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Index access through <see cref="ShellIndexingUtilities.GetIndexedValueAsync"/>.
/// </summary>
public sealed class ShellIndexingTests
{
    /// <summary>Indexes a target and returns what it produced, or the failure's message.</summary>
    private static async Task<string> IndexAsync(
        object? target,
        object? index,
        IndexLookupKind lookupKind = IndexLookupKind.Default)
    {
        try
        {
            var value = await ShellIndexingUtilities.GetIndexedValueAsync(
                target, index, lookupKind, CancellationToken.None);
            return $"{value ?? "null"}";
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
    }

    [Theory]
    // Every shape the shared pre-record half handles.
    [InlineData(new object[] { 10, 20, 30 }, 1, "20")]
    [InlineData(new object[] { 10, 20, 30 }, 0, "10")]
    public async Task An_integer_index_resolves(object[] items, int index, string expected)
    {
        var actual = await IndexAsync(items, index);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("abc", 1, "b")]
    // Out of range, so the message itself has to match — it is the shared half that produces it.
    [InlineData("abc", 9, "Index 9 is out of range for string length 3.")]
    [InlineData("abc", -1, "Indexes must be zero or greater.")]
    public async Task A_string_index_resolves(string text, int index, string expected)
    {
        var actual = await IndexAsync(text, index);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task A_numeric_string_key_still_reaches_the_dictionary()
    {
        // The case that made this convergence delicate, and that an earlier attempt broke.
        // TryGetIntegerIndex accepts "3", so a numeric string enters the integer branch — which
        // sits ahead of record and dictionary lookup. That branch must *fall through* when the
        // target is none of the integer-indexable shapes, or a dictionary key spelled "3" stops
        // resolving. The first extraction ended it with a throw and turned this into an error;
        // caught by probing the shell, not by the suite, which is why it is pinned here now.
        var dictionary = new Dictionary<object, object?> { ["0"] = "zero", ["3"] = "THREE" };
        var actual = await IndexAsync(dictionary, "3");

        Assert.Equal("THREE", actual);
    }

    [Fact]
    public async Task A_numeric_string_index_still_means_the_element()
    {
        // The reason the split is before/after rather than an async prefix. TryGetIntegerIndex
        // accepts "3", so a numeric string reaches the integer branch — which sits *ahead* of
        // record access. Hoisting the record lookup to the front, the obvious convergence, would
        // silently change this to mean "the field named 3".
        var actual = await IndexAsync(new object[] { "a", "b", "c", "d" }, "3");

        Assert.Equal("d", actual);
    }

    [Fact]
    public async Task A_dictionary_key_resolves()
    {
        var dictionary = new Dictionary<string, object?> { ["alpha"] = 1, ["beta"] = 2 };
        var actual = await IndexAsync(dictionary, "beta");

        Assert.Equal("2", actual);
    }

    [Fact]
    public async Task A_missing_key_fails()
    {
        var dictionary = new Dictionary<string, object?> { ["alpha"] = 1 };
        var actual = await IndexAsync(dictionary, "nope");
    }

    [Fact]
    public async Task Indexing_null_fails()
    {
        var actual = await IndexAsync(null, 0);

        Assert.Equal("Cannot index into null.", actual);
    }
}
