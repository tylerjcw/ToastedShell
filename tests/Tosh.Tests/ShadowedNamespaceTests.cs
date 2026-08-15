using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// A module named after a CLR namespace does not make that namespace unreachable.
///
/// `TS-P2-100`. A profile declaring `module System { … }` turned
/// `System.Convert.ToInt32(…)` in an unrelated file into "Member 'Convert' was
/// not found on type 'ToshModuleObject'", and bare `System` into "Command
/// 'System' was not found". The same file worked under `--no-profile`, so it
/// showed up only in a real session and only after the module was declared.
///
/// The specification already states the rule for a module named after a CLR
/// *type* — its own exports win, and a name it does not export is looked up on
/// the shadowed type. `TS-P1-35` built that. A namespace had no fall-through at
/// all, so this extends the documented rule rather than inventing one.
/// </summary>
public class ShadowedNamespaceTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(v => v?.ToString() ?? "null"));
    }

    private const string SystemModule = """
        partial module System {
            export func Hello() -> string => "module-wins"
        }

        """;

    /// <summary>The module's own export still wins — that is the part to preserve.</summary>
    [Fact]
    public async Task A_module_export_wins_over_the_namespace()
        => Assert.Equal("module-wins", await RunAsync(SystemModule + "System.Hello()"));

    /// <summary>And a name it does not export reaches the namespace.</summary>
    [Theory]
    [InlineData("""System.Convert.ToInt32("42")""", "42")]
    [InlineData("System.Math.Max(1, 9)", "9")]
    [InlineData("""System.String.Join("-", ["a", "b"])""", "a-b")]
    public async Task A_name_the_module_does_not_export_reaches_the_namespace(
        string expression,
        string expected)
        => Assert.Equal(expected, await RunAsync(SystemModule + expression));

    /// <summary>
    /// The nested spelling the report actually used, where `System` sits inside
    /// `ToastLib`. Both the nested module and the global namespace must resolve in
    /// the same file.
    /// </summary>
    [Fact]
    public async Task A_nested_module_of_the_same_name_coexists_with_the_namespace()
        => Assert.Equal("lib,7", await RunAsync(
            """
            partial module ToastLib {
                partial module System { export func Sig() -> string => "lib" }
            }
            ToastLib.System.Sig()
            System.Convert.ToInt32("7")
            """));

    /// <summary>
    /// The regression the first attempt caused, and the reason the fix is narrow.
    ///
    /// A module named after a CLR *type* already had a fall-through, and skipping
    /// the module branch for it handed `Math` to a different implementation:
    /// `Math.Max(3, 7)` kept answering 7 while changing from `Int32` to `Double`,
    /// so the value looked right and the type was not. The assertion is on the
    /// type for that reason — comparing the number alone would not have caught it,
    /// and the suite only did because `Assert.Equal(7, …)` is type-aware.
    /// </summary>
    [Fact]
    public async Task A_shadowed_type_keeps_its_own_overload_resolution()
        => Assert.Equal("Int32", await RunAsync(
            """
            module Math {
                export func Clamp(a, b, c) { return "module-wins" }
                export func widest() { return Math.Max(3, 7) }
            }
            (describe-type (Math.widest())).Name
            """));

    /// <summary>Ordinary modules are untouched, nested ones included.</summary>
    [Theory]
    [InlineData("M.Top()", "1")]
    [InlineData("M.Inner.Deep(5)", "5")]
    public async Task An_ordinary_module_still_resolves(string expression, string expected)
        => Assert.Equal(expected, await RunAsync(
            """
            module M {
                export module Inner { export func Deep(x: int) -> int => $x }
                export func Top() -> int => 1
            }

            """ + expression));

    /// <summary>
    /// A genuine miss is still an error rather than a silent null: the fall-through
    /// must not swallow mistakes.
    /// </summary>
    [Fact]
    public async Task A_name_in_neither_the_module_nor_the_namespace_still_fails()
    {
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => new ToshEngine(ToshRuntime.CreateDefault())
                .ExecuteToListAsync(SystemModule + "System.NoSuchThing.Nope()"));

        Assert.Contains("NoSuchThing", exception.Message);
    }
}
