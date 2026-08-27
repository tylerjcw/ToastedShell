using Tosh.Runtime;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class LazySequenceTests
{
    // --- Infinite ranges ---

    [Fact]
    public async Task Infinite_range_with_first()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("1.. | first 5");
        Assert.Equal(new object[] { 1, 2, 3, 4, 5 }, results);
    }

    [Fact]
    public async Task Infinite_range_with_step_and_first()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("0..2.. | first 5");
        Assert.Equal(new object[] { 0, 2, 4, 6, 8 }, results);
    }

    [Fact]
    public async Task Infinite_range_in_variable()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("var nums = 1..; $nums | first 3");
        Assert.Equal(new object[] { 1, 2, 3 }, results);
    }

    [Fact]
    public async Task Infinite_range_with_where_and_first()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("1.. | where { _ % 2 == 0 } | first 4");
        Assert.Equal(new object[] { 2, 4, 6, 8 }, results);
    }

    [Fact]
    public async Task Infinite_range_with_take_while()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("1.. | take-while { _ < 6 }");
        Assert.Equal(new object[] { 1, 2, 3, 4, 5 }, results);
    }

    [Fact]
    public void Infinite_range_toString()
    {
        var range = new ToshRange(1, null, null);
        Assert.Equal("1..", range.ToString());
    }

    [Fact]
    public void Infinite_range_stepped_toString()
    {
        var range = new ToshRange(0, 3, null);
        Assert.Equal("0..3..", range.ToString());
    }

    [Fact]
    public async Task Infinite_range_in_list_comprehension()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("echo [$x * $x <| for x in 1..10]");
        Assert.Single(results);
        var array = Assert.IsType<int[]>(results[0]);
        Assert.Equal([1, 4, 9, 16, 25, 36, 49, 64, 81, 100], array);
    }

    [Fact]
    public async Task Finite_range_still_works()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("1..5 | each { $_ * 2 }");
        Assert.Equal(new object[] { 2, 4, 6, 8, 10 }, results);
    }

    [Fact]
    public async Task Infinite_range_match_pattern()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("match (42) { 1..100 => \"in range\"; default => \"out\" }");
        Assert.Single(results);
        Assert.Equal("in range", results[0]);
    }

    // --- LazySequence memoization ---

    [Fact]
    public void LazySequence_memoizes_items()
    {
        var callCount = 0;
        IEnumerable<object?> Source()
        {
            for (var i = 0; i < 10; i++)
            {
                callCount++;
                yield return i;
            }
        }

        var seq = new LazySequence(Source());

        // First traversal: pull 3 items
        var first3 = seq.EnumerateShellItems().Take(3).ToList();
        Assert.Equal(new object?[] { 0, 1, 2 }, first3);
        Assert.Equal(3, callCount); // only 3 items evaluated

        // Second traversal: pull 5 items — should reuse cached 3 + evaluate 2 more
        var first5 = seq.EnumerateShellItems().Take(5).ToList();
        Assert.Equal(new object?[] { 0, 1, 2, 3, 4 }, first5);
        Assert.Equal(5, callCount); // only 2 more evaluated
    }

    [Fact]
    public void LazySequence_toString_shows_preview()
    {
        var seq = new LazySequence(Enumerable.Range(1, 100).Select(x => (object?)x));
        // Force first 3 items
        seq.EnumerateShellItems().Take(3).ToList();
        var str = seq.ToString();
        Assert.Contains("1, 2, 3", str);
        Assert.Contains("...", str);
        Assert.StartsWith("lazy ", str);
    }

    // --- recur command ---

    [Fact]
    public async Task Recur_fibonacci()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("recur (0, 1) func(a, b) => ($a + $b) | first 10");
        Assert.Equal(new object[] { 0, 1, 1, 2, 3, 5, 8, 13, 21, 34 }, results);
    }

    [Fact]
    public async Task Recur_tribonacci()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("recur (0, 0, 1) func(a, b, c) => ($a + $b + $c) | first 8");
        Assert.Equal(new object[] { 0, 0, 1, 1, 2, 4, 7, 13 }, results);
    }

    [Fact]
    public async Task Recur_single_seed()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("recur 1 func(x) => ($x * 2) | first 5");
        Assert.Equal(new object[] { 1, 2, 4, 8, 16 }, results);
    }

    [Fact]
    public async Task Recur_with_block()
    {
        var engine = ShellEngine.CreateFullShell();
        // Blocks receive _ as the window list for multi-seed
        var results = await engine.ExecuteToListAsync("recur (1, 1) { $_[0] + $_[1] } | first 7");
        Assert.Equal(new object[] { 1, 1, 2, 3, 5, 8, 13 }, results);
    }

    // --- iterate (existing) with infinite range ---

    [Fact]
    public async Task Iterate_powers_of_2()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("iterate 1 func(x) => ($x * 2) | first 8");
        Assert.Equal(new object[] { 1, 2, 4, 8, 16, 32, 64, 128 }, results);
    }

    // --- Lazy generator comprehensions ---

    [Fact]
    public async Task Generator_comprehension_is_lazy()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("var sq = ($x * $x <| for x in 1..); $sq | first 5");
        Assert.Equal(new object[] { 1, 4, 9, 16, 25 }, results);
    }

    [Fact]
    public async Task Generator_comprehension_with_where_is_lazy()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("var evens = ($x <| for x in 1.. where $x % 3 == 0)\n$evens | first 4");
        Assert.Equal(new object[] { 3, 6, 9, 12 }, results);
    }

    [Fact]
    public async Task Generator_comprehension_finite()
    {
        var engine = ShellEngine.CreateFullShell();
        // Generator comprehension with finite source — still lazy but pipeable
        var results = await engine.ExecuteToListAsync("($x * 10 <| for x in [1, 2, 3]) | each { $_ }");
        Assert.Equal(new object[] { 10, 20, 30 }, results);
    }

    // --- Composition: lazy + pipeline ---

    [Fact]
    public async Task Infinite_range_map_first()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("1.. | map { $_ * 3 } | first 4");
        Assert.Equal(new object[] { 3, 6, 9, 12 }, results);
    }

    [Fact]
    public async Task Iterate_with_take_while()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("iterate 1 func(x) => ($x * 2) | take-while { _ <= 64 }");
        Assert.Equal(new object[] { 1, 2, 4, 8, 16, 32, 64 }, results);
    }

    [Fact]
    public async Task Recur_fibonacci_take_while()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("recur (0, 1) func(a, b) => ($a + $b) | take-while { _ < 100 }");
        Assert.Equal(new object[] { 0, 1, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89 }, results);
    }

    // --- Get with infinite ranges ---

    [Fact]
    public async Task Get_open_range_from_pipeline()
    {
        var engine = ShellEngine.CreateFullShell();
        var results = await engine.ExecuteToListAsync("echo a b c d e | get 2..");
        Assert.Equal(new object[] { "c", "d", "e" }, results);
    }
}
