using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P2-16 — module-qualified command dispatch must not depend on the
/// module's capitalization. `Geo.area 2` previously failed to parse
/// because a dotted name beginning with an uppercase letter was assumed
/// to be a CLR static member access, leaving the argument with nowhere
/// to attach, while `geo.area 2` dispatched normally.
/// </summary>
public sealed class ModuleDispatchCasingTests
{
    [Theory]
    [InlineData("Geo")]
    [InlineData("geo")]
    [InlineData("GEO")]
    [InlineData("kebab-mod")]
    [InlineData("snake_mod")]
    public async Task Module_dispatch_works_for_any_casing(string moduleName)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        await engine.ExecuteToListAsync(
            $$"""
            module {{moduleName}} { export func twice(r) { return $r * 2 } }
            var got = ({{moduleName}}.twice 21)
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(42, got);
    }

    [Fact]
    public async Task Static_clr_access_still_resolves_in_command_position()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        await engine.ExecuteToListAsync(
            """
            var alone = (Math.PI)
            var arithmetic = (Math.PI + 1)
            var call = (Math.Sqrt(16))
            """);

        Assert.True(engine.TryGetVariableValue("alone", out var alone));
        Assert.Equal(Math.PI, Assert.IsType<double>(alone), 10);
        Assert.True(engine.TryGetVariableValue("arithmetic", out var arithmetic));
        Assert.Equal(Math.PI + 1, Assert.IsType<double>(arithmetic), 10);
        Assert.True(engine.TryGetVariableValue("call", out var call));
        Assert.Equal(4d, Assert.IsType<double>(call));
    }

    [Fact]
    public async Task Sibling_static_members_remain_arguments_not_a_command_call()
    {
        // The discriminator is confined to command position: here the
        // second dotted name is a sibling argument to `echo`, not an
        // argument to the first.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(
            """
            class Config {
                shared prop version = "1.0"
                shared prop maxRetries = 3
            }
            echo Config.version Config.maxRetries
            """);

        Assert.Equal(["1.0", 3], results);
    }
}
