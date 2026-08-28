namespace Tosh.AllocationProbe;

/// <summary>
/// The default set: an empty loop, then one shape per kind of expression work.
/// </summary>
/// <remarks>
/// Chosen so that each row differs from the one above it by a single thing, which is what
/// makes the "over empty" column readable. <c>TS-P2-125</c> found that parentheses alone
/// cost more than the assignment they wrapped by comparing exactly these two rows.
/// </remarks>
public static class Shapes
{
    public const string Preamble = """
        var s = 0
        var t = 3
        var x = 0
        var r = "abcd"
        var a = [1, 2, 3]
        """;

    public static readonly IReadOnlyList<Shape> Default =
    [
        new("empty",              ""),
        new("$s = $t",            "$s = $t"),
        new("$s = ($t)",          "$s = ($t)"),
        new("$s = ($t + 1)",      "$s = ($t + 1)"),
        new("$s = ($t+1+2+3)",    "$s = ($t + 1 + 2 + 3)"),
        new("$x += 1",            "$x += 1"),
        new("$s = ($t == 1)",     "$s = ($t == 1)"),
        new("$s = ($t < 5)",      "$s = ($t < 5)"),
        new("$s = $r.Length",     "$s = $r.Length"),
        new("$s = $a[0]",         "$s = $a[0]"),
        new("$s = (-$t)",         "$s = (-$t)"),
        new("$s = ($t * 2.5)",    "$s = ($t * 2.5)"),
    ];
}
