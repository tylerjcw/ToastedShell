using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A module-level `bind native` block exposes only the symbols it declares.
///
/// `TS-P2-90`. Native export tables were cached per resolved library path and the
/// same table handed to every module wrapping it, so two modules binding the same
/// `.so` shared one surface: `module A` declaring `abs` could call `labs` through
/// its own alias purely because `module B` had declared it. A library built from
/// several modules over one library had no encapsulation at all.
///
/// The library *handle* is still shared — loading the same `.so` twice would be
/// waste, and the handle is what the cache exists for. Only the export surface is
/// per-module now.
///
/// Class-body binds were already isolated, routing through `SetNativeMember`, so
/// this brings module-level binds in line rather than inventing a rule.
/// </summary>
public class NativeModuleIsolationTests
{
    private static bool SkipOffLinux => !OperatingSystem.IsLinux();

    private static async Task<IReadOnlyList<object?>> RunAsync(string source)
        => await new ToshEngine(ToshRuntime.CreateDefault()).ExecuteToListAsync(source);

    private const string TwoModules = """
        module A { bind native "libc.so.6" as L1 { func abs(v: int) -> int } }
        module B { bind native "libc.so.6" as L2 { func labs(v: long) -> long } }

        """;

    /// <summary>
    /// Each module still reaches its own symbols — the isolation must not be
    /// achieved by breaking the binding.
    /// </summary>
    [Fact]
    public async Task Each_module_sees_the_symbols_it_declared()
    {
        if (SkipOffLinux) return;

        var results = await RunAsync(TwoModules + "A.L1.abs(-5)\nB.L2.labs(-11)");

        Assert.Equal(["5", "11"], results.Select(v => v?.ToString()));
    }

    /// <summary>
    /// The reported leak, asserted in both directions. Checking only one would
    /// pass on a fix that merely reordered which module won the shared table.
    /// </summary>
    [Theory]
    [InlineData("A.L1.labs(-9)", "labs")]
    [InlineData("B.L2.abs(-7)", "abs")]
    public async Task A_module_cannot_reach_another_modules_symbol(string call, string symbol)
    {
        if (SkipOffLinux) return;

        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault()).ExecuteToListAsync(TwoModules + call));

        Assert.Contains(symbol, exception.Message);
    }

    /// <summary>
    /// Two modules over one library both work in the same session, which is the
    /// case the shared handle exists for: isolating the surface must not mean
    /// loading the library twice or failing the second bind.
    /// </summary>
    [Fact]
    public async Task Two_modules_over_one_library_both_function()
    {
        if (SkipOffLinux) return;

        var results = await RunAsync(
            TwoModules +
            """
            module C { bind native "libc.so.6" as L3 { func abs(v: int) -> int } }
            A.L1.abs(-1)
            B.L2.labs(-2)
            C.L3.abs(-3)
            """);

        Assert.Equal(["1", "2", "3"], results.Select(v => v?.ToString()));
    }

    /// <summary>
    /// A class-body bind — the spelling `ToastLib` uses throughout — was already
    /// isolated and must stay that way.
    /// </summary>
    [Fact]
    public async Task A_class_body_bind_is_still_isolated()
    {
        if (SkipOffLinux) return;

        var results = await RunAsync(
            """
            hermit class One { bind native "libc.so.6" { func abs(v: int) -> int } }
            hermit class Two { bind native "libc.so.6" { func labs(v: long) -> long } }
            One.abs(-4)
            Two.labs(-6)
            """);

        Assert.Equal(["4", "6"], results.Select(v => v?.ToString()));
    }

    [Fact]
    public async Task A_class_body_bind_does_not_leak_to_a_sibling_class()
    {
        if (SkipOffLinux) return;

        await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault()).ExecuteToListAsync(
                """
                hermit class One { bind native "libc.so.6" { func abs(v: int) -> int } }
                hermit class Two { bind native "libc.so.6" { func labs(v: long) -> long } }
                One.labs(-4)
                """));
    }
}
