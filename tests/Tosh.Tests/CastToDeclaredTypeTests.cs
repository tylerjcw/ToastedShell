using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// <c>cast</c> reaches a type declared in ToastScript — <c>TS-P2-55</c>.
/// </summary>
/// <remarks>
/// <para>
/// Casting an enum member *to* a number worked; casting a number *to* an enum member did not.
/// <c>cast</c> resolved its target through CLR type lookup alone, and a name declared in
/// ToastScript is never a CLR type, so <c>cast Fuel 8</c> failed with "Unable to resolve type
/// 'Fuel'" and the conversion path was never reached at all.
/// </para>
/// <para>
/// Two conversions exist, deliberately. An enum converts from a member name or a backing value —
/// the reported case, and the symmetric partner of the one that already worked. Every other
/// declared kind converts only from a value that already is one: <c>cast</c> is not a
/// constructor, and "turn this record into that class" is a language decision rather than a
/// repair.
/// </para>
/// <para>
/// The probe types are given distinctive names on purpose. Bare type-name resolution scans every
/// loaded assembly, so a class called <c>B</c> can be captured by a type some emitter test left in
/// the process — which is what `TS-P2-48` is about, and which this file's own
/// <c>cast B $d</c> hit before the names were changed.
/// </para>
/// <para>
/// Resolution is CLR-first and declaration-second. That ordering is not a preference — asking
/// the declaration side first broke the command's own documented example, because
/// <c>cast list&lt;int&gt;</c> resolves to a *shell* descriptor for the builtin list type and
/// then failed as "this value is not a 'list&lt;int&gt;'". Declared types fill the gap where the
/// CLR resolver finds nothing, which is exactly the gap this item is about.
/// </para>
/// </remarks>
public sealed class CastToDeclaredTypeTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private static async Task<ToshDiagnostic> RunForDiagnosticAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(source));
        return exception.Diagnostics[0];
    }

    // ── The reported case ──────────────────────────────────────────────────────

    [Fact]
    public async Task An_enum_casts_from_its_backing_value()
    {
        Assert.Equal("Uranium", await RunAsync(
            """
            enum Fuel : int { Mox = 3, Uranium = 8 }
            cast Fuel 8
            """));
    }

    [Fact]
    public async Task An_enum_casts_from_its_member_name()
    {
        Assert.Equal("Mox", await RunAsync(
            """
            enum Fuel : int { Mox = 3, Uranium = 8 }
            cast Fuel "Mox"
            """));
    }

    [Fact]
    public async Task A_member_name_is_matched_without_regard_to_case()
    {
        Assert.Equal("Uranium", await RunAsync(
            """
            enum Fuel : int { Mox = 3, Uranium = 8 }
            cast Fuel "uranium"
            """));
    }

    [Fact]
    public async Task The_round_trip_closes()
    {
        // `cast int <enum>` was fixed earlier; this is its partner, and together they mean an
        // enum can leave the type system and come back.
        Assert.Equal("Uranium", await RunAsync(
            """
            enum Fuel : int { Mox = 3, Uranium = 8 }
            var v = Fuel.Uranium
            cast Fuel (cast int $v)
            """));
    }

    [Fact]
    public async Task An_enum_of_another_underlying_type_converts_too()
    {
        // The backing value is compared after conversion to the member's own underlying type,
        // so a `long`-shaped literal matches rather than missing on boxed identity.
        Assert.Equal("Huge", await RunAsync(
            """
            enum Big : long { Small = 1, Huge = 300 }
            cast Big 300
            """));
    }

    [Fact]
    public async Task A_nested_enum_casts_by_its_qualified_name()
    {
        Assert.Equal("Mox", await RunAsync(
            """
            class Reactor { enum Fuel : int { Mox = 3 } }
            cast Reactor.Fuel 3
            """));
    }

    [Fact]
    public async Task An_enum_value_casts_to_itself()
    {
        Assert.Equal("Mox", await RunAsync(
            """
            enum Fuel : int { Mox = 3 }
            var v = Fuel.Mox
            cast Fuel $v
            """));
    }

    // ── Declared types that are not enums ──────────────────────────────────────

    [Theory]
    [InlineData("class CastProbeClass { prop X = 7 }\nvar p = new CastProbeClass()\n(cast CastProbeClass $p).X", "7")]
    [InlineData("struct CastProbeStruct { prop X = 7 }\nvar p = new CastProbeStruct()\n(cast CastProbeStruct $p).X", "7")]
    [InlineData("record CastProbeRecord(a, b)\nvar r = new CastProbeRecord(7, 2)\n(cast CastProbeRecord $r).a", "7")]
    public async Task A_declared_value_casts_to_its_own_type(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task A_subclass_instance_casts_to_its_base()
    {
        // Decided by the same walk `is` uses, so `cast` and `is` cannot come to disagree.
        Assert.Equal("7", await RunAsync(
            """
            class CastProbeBase { prop X = 7 }
            class CastProbeDerived extends CastProbeBase { }
            var d = new CastProbeDerived()
            (cast CastProbeBase $d).X
            """));
    }

    [Fact]
    public async Task Casting_to_a_declared_class_does_not_construct_one()
    {
        var diagnostic = await RunForDiagnosticAsync("class CastProbeClass { prop X = 1 }\ncast CastProbeClass 5");

        Assert.Equal("tosh.runtime.cast_failed", diagnostic.Code);
        Assert.Contains("converts only a value that already is one", diagnostic.Title, StringComparison.Ordinal);
    }

    // ── An unknown target is distinct from a failed conversion ─────────────────

    [Fact]
    public async Task A_name_that_is_no_type_at_all_has_its_own_code()
    {
        var diagnostic = await RunForDiagnosticAsync("cast NoSuchTypeAnywhere 1");

        Assert.Equal("tosh.runtime.unknown_cast_target", diagnostic.Code);
        Assert.Contains("NoSuchTypeAnywhere", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_that_will_not_convert_has_a_different_code()
    {
        // The pair that makes both codes worth having: "I misspelled the type" and "this value
        // will not convert" were the same diagnostic before, so neither could be told apart.
        var diagnostic = await RunForDiagnosticAsync(
            "enum Fuel : int { Mox = 3 }\ncast Fuel 99");

        Assert.Equal("tosh.runtime.cast_failed", diagnostic.Code);
    }

    [Fact]
    public async Task A_failed_enum_conversion_lists_the_members()
    {
        var diagnostic = await RunForDiagnosticAsync(
            "enum Fuel : int { Mox = 3, Uranium = 8 }\ncast Fuel \"Plutonium\"");

        Assert.Contains("Mox", diagnostic.Title, StringComparison.Ordinal);
        Assert.Contains("Uranium", diagnostic.Title, StringComparison.Ordinal);
    }

    // ── The value argument is a value, not a type name ─────────────────────────

    [Fact]
    public async Task A_dotted_bareword_value_resolves_rather_than_arriving_as_text()
    {
        // `cast` sat in the list of commands whose bareword arguments are all type names —
        // right for `describe-type` and `members`, wrong for the one command that takes a type
        // *and then values*. So `cast int Fuel.Uranium` handed the conversion the literal text
        // "Fuel.Uranium", while `echo Fuel.Uranium` resolved the very same spelling.
        Assert.Equal("8", await RunAsync(
            """
            enum Fuel : int { Mox = 3, Uranium = 8 }
            cast int Fuel.Uranium
            """));
    }

    [Fact]
    public async Task The_same_holds_for_a_clr_static()
    {
        // Not an enum quirk: `cast double Math.PI` was equally literal.
        Assert.StartsWith("3.14", await RunAsync("cast double Math.PI"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_round_trip_closes_without_a_temporary()
    {
        Assert.Equal("Uranium", await RunAsync(
            """
            enum Fuel : int { Mox = 3, Uranium = 8 }
            cast Fuel (cast int Fuel.Uranium)
            """));
    }

    [Theory]
    // The control for the position rule: commands whose arguments really are all type names
    // must keep reading them that way.
    [InlineData("describe-type int | to json")]
    [InlineData("members System.String | to json")]
    [InlineData("methods System.String | to json")]
    [InlineData("constructors System.String | to json")]
    public async Task Commands_whose_arguments_are_all_type_names_are_unchanged(string source)
    {
        var output = await RunAsync(source);

        Assert.NotEmpty(output);
    }
    // ── Nothing that already worked changed ────────────────────────────────────

    [Theory]
    [InlineData("cast int \"42\"", "42")]
    [InlineData("cast string 42", "42")]
    [InlineData("cast double \"1.5\"", "1.5")]
    [InlineData("echo [1, 2, 3] | cast list<int> | count", "3")]
    public async Task The_clr_casts_are_unchanged(string source, string expected)
    {
        // `cast list<int>` is the command's own first documented example, and resolving declared
        // types before CLR ones broke it. It is a test rather than a note for that reason.
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task A_clr_conversion_failure_is_still_reported()
    {
        var diagnostic = await RunForDiagnosticAsync("cast int \"abc\"");

        Assert.Equal("tosh.runtime.cast_failed", diagnostic.Code);
        Assert.Contains("Int32", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clr_type_wins_a_name_it_shares_with_a_declaration()
    {
        // Not a preference so much as compatibility: every existing script casting to `String`
        // means the CLR one, and a declaration cannot be allowed to change that silently.
        Assert.Equal("42", await RunAsync(
            """
            class String { prop X = 1 }
            cast String 42
            """));
    }

    // ── The same names resolve from inside a script ────────────────────────────

    [Fact]
    public async Task A_type_declared_in_a_running_script_is_castable()
    {
        // Top-level declarations land in the global registry; a script's land in its scope. The
        // view the engine hands the command answers both, so `cast` does not work at the prompt
        // and fail in a file.
        var directory = Path.Combine(Path.GetTempPath(), $"tosh-cast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var scriptPath = Path.Combine(directory, "main.tosh");
            await File.WriteAllTextAsync(
                scriptPath,
                "enum Fuel : int { Mox = 3, Uranium = 8 }\ncast Fuel 8\n");

            var engine = new ToshEngine(ToshRuntime.CreateDefault());
            var results = new List<object?>();

            await foreach (var value in engine.ExecuteScriptFileAsync(scriptPath))
            {
                results.Add(value);
            }

            Assert.Equal("Uranium", Assert.Single(results)?.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Describe_type_reaches_a_script_declared_type_too()
    {
        // The same view fixes the same blindness in a second command, which is why it lives on
        // the context rather than inside `cast`.
        var directory = Path.Combine(Path.GetTempPath(), $"tosh-cast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var scriptPath = Path.Combine(directory, "main.tosh");
            await File.WriteAllTextAsync(
                scriptPath,
                "enum Fuel : int { Mox = 3 }\ndescribe-type Fuel | to json\n");

            var engine = new ToshEngine(ToshRuntime.CreateDefault());
            var results = new List<object?>();

            await foreach (var value in engine.ExecuteScriptFileAsync(scriptPath))
            {
                results.Add(value);
            }

            Assert.Contains("Fuel", string.Join("", results), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
