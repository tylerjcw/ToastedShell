using Tosh.Language;

namespace Tosh.Tests;

/// <summary>
/// Destructuring a record, struct or class in a match arm — <c>TOAST-0053</c>.
/// </summary>
/// <remarks>
/// <para>
/// The pattern grammar arrived for union variants, but nothing about it was variant-shaped:
/// a pattern asks a value for its type name, its fields in order, and its fields by name.
/// A record, a struct and a class all answer those, so the matcher asks them through one
/// <c>PatternSubject</c> rather than switching on the instance type in four places.
/// </para>
/// <para>
/// A class is the exception, and deliberately: its properties may be inherited, reordered or
/// added without changing what the class means, so there is no order a positional pattern
/// could rely on. Naming the fields is the only safe spelling, and a positional pattern over
/// a class says so instead of binding against an order that is not a contract.
/// </para>
/// </remarks>
public sealed class RecordAndClassPatternTests
{
    private const string Shapes = """
        record Point(x: int, y: int)
        struct Size(w: int, h: int) { }
        class Shape {
            prop Kind = "none"
            prop Area = 0
        }
        class Circle extends Shape {
            prop Radius = 1
        }
        """;

    private static async Task<IReadOnlyList<object?>> RunAsync(string body)
    {
        var engine = ShellEngine.CreateFullShell();
        return await engine.ExecuteToListAsync(Shapes + "\n" + body);
    }

    [Fact]
    public async Task A_record_destructures_positionally()
    {
        var results = await RunAsync("""
            echo (match (new Point(3, 4)) {
                Point(x, y) => $x + $y
                default => -1
            })
            """);

        Assert.Equal("7", results[^1]?.ToString());
    }

    [Fact]
    public async Task A_record_destructures_by_name()
    {
        var results = await RunAsync("""
            echo (match (new Point(3, 4)) {
                Point { y } => $y
                default => -1
            })
            """);

        Assert.Equal("4", results[^1]?.ToString());
    }

    /// <summary>
    /// One field tests, another binds — the same rule as a variant pattern, because it is the
    /// same code.
    /// </summary>
    [Fact]
    public async Task A_record_pattern_may_test_one_field_and_bind_another()
    {
        var results = await RunAsync("""
            echo (match (new Point(3, 4)) {
                Point { x: 3, y } => $y
                default => -1
            })
            echo (match (new Point(9, 4)) {
                Point { x: 3, y } => $y
                default => -1
            })
            """);

        Assert.Equal("4", results[^2]?.ToString());
        Assert.Equal("-1", results[^1]?.ToString());
    }

    [Fact]
    public async Task A_struct_destructures_positionally()
    {
        var results = await RunAsync("""
            var s = new Size(4, 5)
            echo (match ($s) {
                Size(w, h) => $w * $h
                default => -1
            })
            """);

        Assert.Equal("20", results[^1]?.ToString());
    }

    [Fact]
    public async Task A_class_destructures_by_name()
    {
        var results = await RunAsync("""
            var c = new Circle()
            echo (match ($c) {
                Circle { Radius } => $Radius
                default => -1
            })
            """);

        Assert.Equal("1", results[^1]?.ToString());
    }

    /// <summary>
    /// A pattern may name a property the class inherits rather than declares.
    /// </summary>
    [Fact]
    public async Task A_class_pattern_reaches_an_inherited_property()
    {
        var results = await RunAsync("""
            var c = new Circle()
            echo (match ($c) {
                Circle { Radius, Kind } => $Kind
                default => "MISS"
            })
            """);

        Assert.Equal("none", results[^1]?.ToString());
    }

    /// <summary>
    /// Destructuring a class positionally is refused, with the named spelling in the help.
    /// </summary>
    /// <remarks>
    /// Binding against property order would compile and run and be wrong the first time
    /// somebody added a property to a base class. The named spelling is offered in the
    /// diagnostic's help, which the rendered form carries but <c>Exception.Message</c> — the
    /// title alone — does not, so only the title is asserted here.
    /// </remarks>
    [Fact]
    public async Task A_class_cannot_be_destructured_positionally()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            var c = new Circle()
            echo (match ($c) {
                Circle(r) => $r
                default => -1
            })
            """));

        Assert.Contains("class", error.Message);
        Assert.Contains("positionally", error.Message);
    }

    /// <summary>
    /// The diagnostics reach records too — the field is named, not silently missed.
    /// </summary>
    [Fact]
    public async Task An_unknown_record_field_is_diagnosed()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            echo (match (new Point(3, 4)) {
                Point { z } => 1
                default => -1
            })
            """));

        Assert.Contains("Point", error.Message);
        Assert.Contains("z", error.Message);
    }

    [Fact]
    public async Task A_record_pattern_of_the_wrong_arity_is_diagnosed()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync("""
            echo (match (new Point(3, 4)) {
                Point(a, b, c) => 1
                default => -1
            })
            """));

        Assert.Contains("Point", error.Message);
        Assert.Contains("3", error.Message);
    }

    /// <summary>
    /// A pattern naming a different type is an ordinary miss, not an error.
    /// </summary>
    [Fact]
    public async Task A_pattern_for_another_type_is_a_miss()
    {
        var results = await RunAsync("""
            echo (match (new Point(3, 4)) {
                Size { w } => 1
                default => -1
            })
            """);

        Assert.Equal("-1", results[^1]?.ToString());
    }

    /// <summary>
    /// The forms mix: a record nested inside a union variant, bound two levels down.
    /// </summary>
    [Fact]
    public async Task A_record_nests_inside_a_variant_pattern()
    {
        var results = await RunAsync("""
            union Opt {
                Some(p: Point)
                None()
            }
            echo (match (Opt.Some(new Point(3, 4))) {
                Some(Point(a, b)) => $a * $b
                default => -1
            })
            """);

        Assert.Equal("12", results[^1]?.ToString());
    }
}
