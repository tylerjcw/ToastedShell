using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The typed record literal, <c>new T {| … |}</c> — <c>TOAST-0091</c>.
/// </summary>
/// <remarks>
/// <para>
/// A declared type could only be written through its constructor, so any state the constructor
/// does not reach had no literal form: the type carrying more information had the weaker
/// spelling than an anonymous record, which can be written in full.
/// </para>
/// <para>
/// **Spelled with `new`.** The item first proposed a bare <c>Villager {| … |}</c>, which is
/// grammatically identical to a command invocation passing a record — <c>f {| a = 7 |}</c>
/// already works — so telling the two apart would need a type table in the parser, the same
/// heuristic that produced <c>TS-P2-16</c> and that <c>TOAST-0090</c> exists to retire.
/// <c>new</c> already marks construction and needs no lookup. The control below keeps the
/// command form working, because that is what the spelling was chosen to protect.
/// </para>
/// <para>
/// **The constructor runs, then the rest is assigned.** Not populate-only: a struct is immutable
/// unless declared <c>fluid</c>, so "allocate and assign" is not available for the default struct
/// at all, and the two tiers could not have agreed under it. Assignment reuses the accessor
/// behind <c>$value.Member = x</c>, so an unwritable member reports what it reports there rather
/// than a second explanation of the same rule.
/// </para>
/// </remarks>
public sealed class TypedRecordLiteralTests
{
    private const string Prelude =
        """
        class LitVillager(p: string, l: int) {
            prop Profession = $p
            prop Level = $l
            prop Name = ""
        }
        class LitBox { prop X = 0 }
        struct LitVec { prop X = 0 }
        fluid struct LitFluidVec { prop X = 0 }
        record LitPoint(X: int, Y: int)

        """;

    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(Prelude + source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    // ── The form ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_literal_with_no_constructor_arguments_sets_its_fields()
    {
        Assert.Equal("9", await RunAsync("echo ((new LitBox {| X = 9 |}).X)"));
    }

    [Fact]
    public async Task A_constructor_runs_and_the_literal_fills_in_the_rest()
    {
        Assert.Equal("Steve", await RunAsync(
            """echo ((new LitVillager("lib", 3) {| Name = "Steve" |}).Name)"""));
    }

    [Fact]
    public async Task The_constructor_arguments_survive_the_initialiser()
    {
        // The whole point of running the constructor rather than populating: state it set is
        // still there afterwards.
        Assert.Equal("lib,3", await RunAsync(
            """
            var v = new LitVillager("lib", 3) {| Name = "Steve" |}
            echo $v.Profession
            echo $v.Level
            """));
    }

    [Fact]
    public async Task A_literal_spans_lines()
    {
        Assert.Equal("Steve", await RunAsync(
            """
            var v = new LitVillager("lib", 3) {|
                Name = "Steve"
            |}
            echo $v.Name
            """));
    }

    [Fact]
    public async Task A_declared_record_takes_a_literal()
    {
        Assert.Equal("9", await RunAsync("echo ((new LitPoint(1, 2) {| Y = 9 |}).Y)"));
    }

    [Fact]
    public async Task A_fluid_struct_takes_a_literal()
    {
        Assert.Equal("5", await RunAsync("echo ((new LitFluidVec {| X = 5 |}).X)"));
    }

    [Fact]
    public async Task A_path_operator_type_takes_a_literal()
    {
        // `TOAST-0090` and this item meet: the type is named by a path, then filled by a literal.
        Assert.Equal("4", await RunAsync(
            """
            class LitOuter { class LitInner { prop V = 0 } }
            echo ((new LitOuter::LitInner {| V = 4 |}).V)
            """));
    }

    // ── What it refuses ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_field_the_type_does_not_have_is_a_diagnostic_naming_it()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync("echo ((new LitBox {| Nope = 1 |}).X)"));

        Assert.Contains("Nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_immutable_struct_refuses_and_says_how_to_allow_it()
    {
        // Reusing the ordinary assignment accessor is what earns this message — the advice to
        // declare the struct `fluid` is the one `$v.X = 5` already gives.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync("echo ((new LitVec {| X = 5 |}).X)"));

        Assert.Contains("fluid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Omitted_required_constructor_arguments_still_fail()
    {
        // The literal does not stand in for the constructor: `LitVillager` needs two arguments
        // and naming `Name` does not supply them. Required state omitted is refused rather than
        // default-initialised.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await RunAsync("""var v = new LitVillager {| Name = "Steve" |}"""));
    }

    // ── Controls ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_anonymous_record_is_unchanged()
    {
        Assert.Equal("1", await RunAsync("echo ({| a = 1 |}.a)"));
    }

    [Fact]
    public async Task A_record_passed_to_a_function_is_still_a_call()
    {
        // The reason the form is spelled with `new`. If this ever becomes a typed literal, the
        // ambiguity the spelling was chosen to avoid has arrived anyway.
        Assert.Equal("7", await RunAsync(
            """
            func litTake(r) { return $r.a }
            echo (litTake {| a = 7 |})
            """));
    }

    [Fact]
    public async Task Ordinary_construction_is_unchanged()
    {
        Assert.Equal("1", await RunAsync("echo ((new LitPoint(1, 2)).X)"));
    }
}
