using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What goes in a table cell when the value has a name — `TOAST-0021`.
///
/// `DisplayEngine` decided for itself what could be expanded structurally, and its test was
/// "does it have readable properties?". An enum member has them, so a `Color.Red` cell
/// became a nested table of `EnumTypeName`, `ShellTypeDescriptor` and `UnderlyingValue` —
/// the value's implementation where the reader had written `Color.Red`.
///
/// `TOAST-0014` was expected to fix this and did not, because display never asks the
/// formatter about a value it thinks it can expand. It now asks the *renderer* whether the
/// language calls the value a scalar, so one place decides and the two cannot disagree.
/// </summary>
public sealed class DisplayScalarCellTests
{
    private static async Task<string> RenderTableAsync(string source)
    {
        var runtime = ToshRuntime.CreateDefault();
        var engine = new ToshEngine(runtime);
        var results = await engine.ExecuteToListAsync(source);

        // A width is required, not incidental: the structural expansion this item is about
        // only runs when `MaxWidth` is set, so a harness without one silently exercises a
        // different path and passes with the fix reverted. That is exactly what the first
        // version of this file did.
        return StyledText.StripAnsi(
            runtime.Display.Render(results, new DisplayRenderOptions(ObjectRenderStyle.Compact, MaxWidth: 120)));
    }

    /// <summary>The defect: an enum in a cell shows its member name.</summary>
    [Fact]
    public async Task An_enum_in_a_cell_shows_its_member_name()
    {
        var table = await RenderTableAsync(
            """
            enum CellHue { Red, Green }
            [{| C = CellHue.Red, N = 1 |}]
            """);

        Assert.Contains("Red", table);
        Assert.DoesNotContain("UnderlyingValue", table);
        Assert.DoesNotContain("ShellTypeDescriptor", table);
        Assert.DoesNotContain("EnumTypeName", table);
    }

    /// <summary>
    /// A container still expands into display's own structural view. This is the control
    /// that keeps the fix from becoming "render everything flat" — a nested table is a
    /// display feature, and the rule is about values with a *name*, not values with parts.
    /// </summary>
    [Fact]
    public async Task A_container_in_a_cell_still_expands()
    {
        var table = await RenderTableAsync("""[{| Tags = ["x", "y"] |}]""");

        Assert.Contains("x", table);
        Assert.Contains("y", table);
    }

    /// <summary>
    /// A display profile still wins where one applies — that is what profiles are for, and
    /// the point of the separation is that display keeps its presentation while the
    /// language keeps its text.
    /// </summary>
    [Fact]
    public async Task A_profile_still_decides_a_cell_it_covers()
    {
        var table = await RenderTableAsync("[{| When = (new DateTime(2026, 8, 17, 12, 0, 0)) |}]");

        Assert.Contains("2026-08-17", table);
    }

    /// <summary>
    /// The predicate display consults is the renderer's own, run rather than restated, so
    /// the two lists cannot drift apart.
    /// </summary>
    [Fact]
    public void The_scalar_predicate_agrees_with_the_renderer()
    {
        Assert.True(ToastRenderer.RendersAsScalar(42));
        Assert.True(ToastRenderer.RendersAsScalar("text"));
        Assert.True(ToastRenderer.RendersAsScalar(true));
        Assert.True(ToastRenderer.RendersAsScalar(new DateTime(2026, 8, 17)));

        Assert.False(ToastRenderer.RendersAsScalar(null));
        Assert.False(ToastRenderer.RendersAsScalar(new object?[] { 1, 2 }));
        Assert.False(ToastRenderer.RendersAsScalar(new Dictionary<object, object?>()));
    }
}
