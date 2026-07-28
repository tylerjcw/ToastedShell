using Tosh.Language;

namespace Tosh.Tests;

public sealed class ComprehensionTests
{
    // --- List comprehensions ---

    [Fact]
    public async Task List_comprehension_basic()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$x * 2 <| for x in [1, 2, 3]]");
        Assert.Single(results);
        var array = Assert.IsType<int[]>(results[0]);
        Assert.Equal([2, 4, 6], array);
    }

    [Fact]
    public async Task List_comprehension_with_where()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$x <| for x in [1, 2, 3, 4, 5, 6] where $x % 2 == 0]");
        Assert.Single(results);
        var array = Assert.IsType<int[]>(results[0]);
        Assert.Equal([2, 4, 6], array);
    }

    [Fact]
    public async Task List_comprehension_with_let()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$y <| for x in [1, 2, 3] let y = $x * 10]");
        Assert.Single(results);
        var array = Assert.IsType<int[]>(results[0]);
        Assert.Equal([10, 20, 30], array);
    }

    [Fact]
    public async Task List_comprehension_with_where_and_let()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$y <| for x in [1, 2, 3, 4] where $x > 2 let y = $x * 5]");
        Assert.Single(results);
        var array = Assert.IsType<int[]>(results[0]);
        Assert.Equal([15, 20], array);
    }

    [Fact]
    public async Task List_comprehension_empty_source()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$x <| for x in []]");
        Assert.Single(results);
        var array = Assert.IsType<object?[]>(results[0]);
        Assert.Empty(array);
    }

    [Fact]
    public async Task List_comprehension_where_filters_all()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$x <| for x in [1, 2, 3] where $x > 10]");
        Assert.Single(results);
        var array = Assert.IsType<object?[]>(results[0]);
        Assert.Empty(array);
    }

    [Fact]
    public async Task List_comprehension_nested_for()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$x + $y <| for x in [10, 20] for y in [1, 2]]");
        Assert.Single(results);
        var array = Assert.IsType<int[]>(results[0]);
        Assert.Equal([11, 12, 21, 22], array);
    }

    // --- Set comprehensions ---

    [Fact]
    public async Task Set_comprehension_basic()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo {: $x <| for x in [1, 2, 2, 3, 3, 3] :}");
        Assert.Single(results);
        var set = Assert.IsType<HashSet<object?>>(results[0]);
        Assert.Equal(3, set.Count);
        Assert.Contains(1, set);
        Assert.Contains(2, set);
        Assert.Contains(3, set);
    }

    [Fact]
    public async Task Set_comprehension_with_where()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo {: $x <| for x in [1, 2, 3, 4] where $x % 2 == 0 :}");
        Assert.Single(results);
        var set = Assert.IsType<HashSet<object?>>(results[0]);
        Assert.Equal(2, set.Count);
        Assert.Contains(2, set);
        Assert.Contains(4, set);
    }

    // --- Dict comprehensions ---

    [Fact]
    public async Task Dict_comprehension_basic()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("var d = {% $x => $x * 2 <| for x in [1, 2, 3] %}; echo $d.Count");
        Assert.Single(results);
        Assert.Equal(3, results[0]);
    }

    [Fact]
    public async Task Dict_comprehension_with_where()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("var d = {% $x => $x * $x <| for x in [1, 2, 3, 4] where $x > 2 %}; echo $d.Count");
        Assert.Single(results);
        Assert.Equal(2, results[0]);
    }

    // --- Generator comprehensions (lazy) ---

    [Fact]
    public async Task Generator_comprehension_basic()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("($x * 3 <| for x in [1, 2, 3]) | each { $_ }");
        Assert.Equal(new object[] { 3, 6, 9 }, results);
    }

    [Fact]
    public async Task Generator_comprehension_with_where()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("($x <| for x in [1, 2, 3, 4, 5] where $x > 3) | each { $_ }");
        Assert.Equal(new object[] { 4, 5 }, results);
    }

    // --- String body ---

    [Fact]
    public async Task List_comprehension_string_body()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$\"item-{$x}\" <| for x in [1, 2, 3]]");
        Assert.Single(results);
        var array = Assert.IsType<string[]>(results[0]);
        Assert.Equal(["item-1", "item-2", "item-3"], array);
    }

    // --- Cartesian product (comma syntax) ---

    [Fact]
    public async Task List_comprehension_cartesian_product()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [($x, $y) <| for x in [1, 2], y in [\"a\", \"b\"]]");
        Assert.Single(results);
        var list = Assert.IsAssignableFrom<System.Collections.IList>(results[0]);
        Assert.Equal(4, list.Count);
    }

    [Fact]
    public async Task List_comprehension_cartesian_product_values()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$x + $y <| for x in [1, 2], y in [10, 20]]");
        Assert.Single(results);
        var list = Assert.IsAssignableFrom<System.Collections.IList>(results[0]);
        Assert.Equal(4, list.Count);
        Assert.Equal(new object[] { 11, 21, 12, 22 }, list.Cast<object>().ToArray());
    }

    // --- Parallel / zip (|| syntax) ---

    [Fact]
    public async Task List_comprehension_parallel_zip()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$x + $y <| for x in [1, 2, 3] || y in [10, 20, 30]]");
        Assert.Single(results);
        var list = Assert.IsAssignableFrom<System.Collections.IList>(results[0]);
        Assert.Equal(new object[] { 11, 22, 33 }, list.Cast<object>().ToArray());
    }

    [Fact]
    public async Task List_comprehension_parallel_zip_shorter_terminates()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$x + $y <| for x in [1, 2, 3] || y in [10, 20]]");
        Assert.Single(results);
        var list = Assert.IsAssignableFrom<System.Collections.IList>(results[0]);
        Assert.Equal(new object[] { 11, 22 }, list.Cast<object>().ToArray());
    }

    [Fact]
    public async Task Generator_comprehension_parallel_zip()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("($x + $y <| for x in [1, 2, 3] || y in [10, 20, 30]) | each { $_ }");
        Assert.Equal(new object[] { 11, 22, 33 }, results);
    }

    // --- Destructuring patterns ---

    [Fact]
    public async Task List_comprehension_destructuring_tuples()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$a + $b <| for (a, b) in [(1, 2), (3, 4)]]");
        Assert.Single(results);
        var list = Assert.IsAssignableFrom<System.Collections.IList>(results[0]);
        Assert.Equal(new object[] { 3, 7 }, list.Cast<object>().ToArray());
    }

    [Fact]
    public async Task List_comprehension_destructuring_three_elements()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("echo [$a + $b + $c <| for (a, b, c) in [(1, 2, 3), (4, 5, 6)]]");
        Assert.Single(results);
        var list = Assert.IsAssignableFrom<System.Collections.IList>(results[0]);
        Assert.Equal(new object[] { 6, 15 }, list.Cast<object>().ToArray());
    }

    [Fact]
    public async Task Generator_comprehension_destructuring_tuples()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("($a + $b <| for (a, b) in [(1, 2), (3, 4)]) | each { $_ }");
        Assert.Equal(new object[] { 3, 7 }, results);
    }
}
