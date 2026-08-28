using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A declared type behaves like its built-in equivalent — <c>TOAST-0088</c>, <c>TOAST-0089</c>.
/// </summary>
/// <remarks>
/// Both defects here have the same shape: a shell-declared type reaches a boundary that was
/// written for the CLR equivalent, matches nothing, and falls through to a generic path that
/// gets it wrong. Neither failed loudly — one produced twenty-three lines of JSON where a CLR
/// enum produces one scalar, and the other silently dropped columns.
/// </remarks>
public sealed class DeclaredTypeSurfaceTests
{
    private static async Task<IReadOnlyList<object?>> RunAsync(string script)
    {
        var engine = ShellEngine.CreateFullShell();
        return await engine.ExecuteToListAsync(script);
    }

    /// <summary>
    /// A declared enum serialises to its member name, not to its internals.
    /// </summary>
    /// <remarks>
    /// <c>ToshEnumValue</c> is an object, so <c>Type.IsEnum</c> is false for it and it missed
    /// the scalar branch in <c>ShellDataSerializer.Normalize</c> — reaching the reflection tail,
    /// which emitted <c>Definition</c>, <c>Name</c>, <c>UnderlyingValue</c>,
    /// <c>ShellTypeDescriptor</c> and <c>EnumTypeName</c>, with the type descriptor twice.
    /// </remarks>
    [Fact]
    public async Task A_declared_enum_serialises_as_its_name()
    {
        var results = await RunAsync("""
            enum Level { Novice  Expert }
            echo (Level.Novice | to json)
            """);

        Assert.Equal("\"Novice\"", results[^1]?.ToString());
    }

    /// <summary>
    /// Every format shares <c>Normalize</c>, so the defect was in all of them at once.
    /// </summary>
    [Fact]
    public async Task Every_format_serialises_a_declared_enum_as_a_scalar()
    {
        var results = await RunAsync("""
            enum Level { Novice  Expert }
            echo ((Level.Novice | to json) | lines | count)
            echo ((Level.Novice | to csv) | lines | count)
            echo ((Level.Novice | to toml) | lines | count)
            """);

        // One scalar each, not a reflected object graph.
        Assert.Equal("1", results[^3]?.ToString());
        Assert.Equal("2", results[^2]?.ToString());   // header plus value
        Assert.Equal("1", results[^1]?.ToString());
    }

    /// <summary>
    /// A composed flags value keeps its composed name.
    /// </summary>
    [Fact]
    public async Task A_flags_enum_serialises_its_composed_name()
    {
        var results = await RunAsync("""
            flags enum F: int { A = 1  B = 2 }
            echo ((F.A bor F.B) | to json)
            """);

        Assert.Equal("\"A, B\"", results[^1]?.ToString());
    }

    /// <summary>
    /// A declared record's collection-valued fields survive into table columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An anonymous record is an <c>ExpandoObject</c> and so matched the
    /// <c>IDictionary&lt;string, object?&gt;</c> display profile. A declared record is a
    /// <c>ToshRecordInstance</c>, matched no profile, and fell to the engine's generic
    /// record-like builder — which drops every column whose values are not a renderable cell
    /// type, silently removing all array- and collection-valued fields.
    /// </para>
    /// <para>
    /// A single row was unaffected, because that path asks for structured values explicitly. So
    /// the same value rendered correctly alone and lost columns in a list of two, which is what
    /// made it look like a serialisation problem rather than a rendering one.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("record Q(A: array<int>, B: int)", "new Q([1, 2], 1)", "new Q([3, 4], 2)")]
    [InlineData("struct Q(A: array<int>, B: int) { }", "new Q([1, 2], 1)", "new Q([3, 4], 2)")]
    [InlineData("union Q { V(A: array<int>, B: int) }", "Q.V([1, 2], 1)", "Q.V([3, 4], 2)")]
    public async Task A_collection_valued_field_keeps_its_column(string declaration, string first, string second)
    {
        var rows = await RunAsync($"""
            {declaration}
            {first}
            {second}
            """);

        var rendered = StyledText.StripAnsi(
            new DisplayEngine(new ObjectFormatter()).RenderMany(rows.Where(r => r is not null).ToArray()!));

        Assert.Contains("A", rendered, StringComparison.Ordinal);
        Assert.Contains("B", rendered, StringComparison.Ordinal);
        Assert.Contains("[1, 2]", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A quantity is record-like for introspection but must still render as a scalar.
    /// </summary>
    /// <remarks>
    /// The first fix targeted <c>IShellRecordObject</c>, which <c>Quantity</c> implements so
    /// that introspection can reach its <c>base-value</c>. That turned <c>483.06 MW</c> into a
    /// table. The profiles name the four declared instance types instead.
    /// </remarks>
    [Fact]
    public void A_quantity_still_renders_as_a_scalar()
    {
        var power = Tosh.Runtime.Units.Quantity.FromLiteral(483.06, "MW");
        var rendered = StyledText.StripAnsi(new DisplayEngine(new ObjectFormatter()).RenderMany([power]));

        Assert.Contains("483.06 MW", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("base-value", rendered, StringComparison.Ordinal);
    }
}
