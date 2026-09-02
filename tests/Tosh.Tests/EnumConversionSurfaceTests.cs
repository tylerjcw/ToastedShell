using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The operator and static enum surface use the same type universe for CLR and declared enums.
/// </summary>
public sealed class EnumConversionSurfaceTests
{
    private static ToshEngine CreateEngine() =>
        new(ToshRuntime.CreateDefault(TextWriter.Null, TextWriter.Null).Language);

    [Fact]
    public async Task As_resolves_a_qualified_clr_enum_and_converts_its_backing_value()
    {
        var result = Assert.Single(await CreateEngine().ExecuteToListAsync(
            "12 as System.ConsoleColor"));

        Assert.Equal(ConsoleColor.Red, Assert.IsType<ConsoleColor>(result));
    }

    [Fact]
    public async Task As_resolves_a_declared_enum_and_converts_its_backing_value()
    {
        var result = Assert.Single(await CreateEngine().ExecuteToListAsync(
            "enum ScopedAsFuelProbe : int { Mox = 3, Uranium = 8 }\n8 as ScopedAsFuelProbe"));

        var member = Assert.IsType<ToshEnumValue>(result);
        Assert.Equal("Uranium", member.Name);
        Assert.Equal(8, member.UnderlyingValue);
    }

    [Fact]
    public async Task Values_and_names_are_ordered_arrays_on_clr_and_declared_enums()
    {
        var results = await CreateEngine().ExecuteToListAsync(
            """
            var clrValues = (System.ConsoleColor.values())
            var clrNames = (System.ConsoleColor.names())
            $clrValues[12]
            $clrNames[12]

            enum Fuel : int { Mox = 3, Uranium = 8 }
            var declaredValues = (Fuel.values())
            var declaredNames = (Fuel.names())
            $declaredValues[1]
            $declaredNames[1]
            """);

        Assert.Equal([12, "Red", 8, "Uranium"], results);
        Assert.IsType<int>(results[0]);
        Assert.IsType<int>(results[2]);
    }

    [Fact]
    public async Task Enum_helpers_appear_in_method_introspection_for_both_type_kinds()
    {
        var results = await CreateEngine().ExecuteToListAsync(
            """
            methods has values System.ConsoleColor
            methods has names System.ConsoleColor
            enum Fuel { Mox, Uranium }
            methods has values Fuel
            methods has names Fuel
            """);

        Assert.Equal([true, true, true, true], results);
    }

    [Theory]
    [InlineData("System.ConsoleColor.values(1)", "System.ConsoleColor.values")]
    [InlineData("enum Fuel { Mox }\nFuel.names(1)", "Fuel.names")]
    public async Task Enum_helpers_reject_arguments_uniformly(string source, string qualifiedMethod)
    {
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => CreateEngine().ExecuteToListAsync(source));

        Assert.Contains(qualifiedMethod, exception.Diagnostics[0].Title, StringComparison.Ordinal);
        Assert.Contains("expects no arguments", exception.Diagnostics[0].Title, StringComparison.Ordinal);
    }
}
