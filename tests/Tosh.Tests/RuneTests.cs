using Tosh.Core;
using Tosh.Language;

namespace Tosh.Tests;

public sealed class RuneTests
{
    // --- Basic rune definition and invocation ---

    [Fact]
    public async Task Simple_rune_executes_body()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("""
            rune greet(name) {
                echo $"Hello, {$name}!"
            }
            greet "World"
            """);
        Assert.Equal(new object[] { "Hello, World!" }, results);
    }

    [Fact]
    public async Task Rune_with_multiple_parameters()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("""
            rune add(a, b) {
                echo ($a + $b)
            }
            add 3 4
            """);
        Assert.Equal(new object[] { 7 }, results);
    }

    [Fact]
    public async Task Rune_with_block_argument()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("""
            rune do-twice(body) {
                $body
                $body
            }
            do-twice { echo "hi" }
            """);
        Assert.Equal(new object[] { "hi", "hi" }, results);
    }

    // --- Hygiene (sealed vs leaky) ---

    [Fact]
    public async Task Sealed_rune_does_not_leak_internal_variables()
    {
        var engine = new ToshEngine();
        await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
        {
            await engine.ExecuteToListAsync("""
                rune set-internal() {
                    var __secret = 42
                }
                set-internal
                echo $__secret
                """);
        });
    }

    [Fact]
    public async Task Leaky_rune_exposes_internal_variables()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("""
            leaky rune set-var() {
                var leaked = 42
            }
            set-var
            echo $leaked
            """);
        Assert.Equal(new object[] { 42 }, results);
    }

    // --- Quote ---

    [Fact]
    public async Task Quote_captures_source_text()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("""
            rune show-source(expr) {
                var src = (quote { $expr })
                echo $src
            }
            show-source (1 + 2)
            """);
        Assert.Single(results);
        Assert.Contains("1 + 2", results[0]?.ToString());
    }

    // --- Pipeline input passthrough ---

    [Fact]
    public async Task Rune_receives_pipeline_input()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("""
            rune pass-through(x) {
                echo $"got {$x}"
            }
            pass-through "data"
            """);
        Assert.Equal(new object[] { "got data" }, results);
    }

    // --- Built-in runes ---

    [Fact]
    public async Task Builtin_unless_skips_when_true()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("""
            unless true { echo "should not appear" }
            echo "done"
            """);
        Assert.Equal(new object[] { "done" }, results);
    }

    [Fact]
    public async Task Builtin_unless_executes_when_false()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("""
            unless false { echo "appeared" }
            """);
        Assert.Equal(new object[] { "appeared" }, results);
    }

    [Fact]
    public async Task Builtin_assert_passes_on_true()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("""
            assert true
            echo "ok"
            """);
        Assert.Equal(new object[] { "ok" }, results);
    }

    [Fact]
    public async Task Builtin_assert_throws_on_false()
    {
        var engine = new ToshEngine();
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync("assert false");
        });
        Assert.Contains("Assertion failed", ex.Message);
    }

    // --- Error cases ---

    [Fact]
    public async Task Rune_with_wrong_arg_count_throws()
    {
        var engine = new ToshEngine();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync("""
                rune needs-two(a, b) { echo "ok" }
                needs-two "only-one"
                """);
        });
    }

    [Fact]
    public async Task Rune_with_duplicate_params_throws()
    {
        var engine = new ToshEngine();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await engine.ExecuteToListAsync("""
                rune bad(x, x) { echo "bad" }
                """);
        });
    }

    // --- Nested rune invocation ---

    [Fact]
    public async Task Nested_rune_invocation()
    {
        var engine = new ToshEngine();
        var results = await engine.ExecuteToListAsync("""
            rune wrap(x) {
                echo $"[{$x}]"
            }
            rune double-wrap(x) {
                wrap $"({$x})"
            }
            double-wrap "hi"
            """);
        Assert.Equal(new object[] { "[(hi)]" }, results);
    }
}
