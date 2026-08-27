using Tosh.Language;
using Tosh.Language.Parsing;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Assignment to a static member — <c>TS-P2-51</c>.
/// </summary>
/// <remarks>
/// <para>
/// A static property could be initialised and never written again. <c>B.S = 5</c> was rejected by
/// the parser with <c>tosh.parser.variable_references_require_dollar</c>, which reads the target as
/// a variable someone forgot to spell with a <c>$</c>. <c>TrySetStaticMember</c> — base walk and
/// all — had exactly one caller, the declaration's own initializer, and no user-reachable path at
/// all.
/// </para>
/// <para>
/// The parser cannot decide this. <c>B.S = 5</c> and <c>person.Name = "x"</c> are the same shape,
/// and it has no symbol table to tell a class from a variable; capitalization is not an answer
/// either, which <c>TS-P2-16</c> settled. So the target is handed over unresolved and the engine —
/// which does know — either performs the static assignment or raises the missing-<c>$</c> hint
/// itself. That is where the matching *read* already answers: <c>person.Name</c> has always given
/// <c>tosh.runtime.variable_reference_requires_dollar</c> at runtime, so this makes a write
/// diagnose where its read does instead of one phase earlier.
/// </para>
/// <para>
/// The rules governing the write are the instance rules, deliberately: a custom setter runs, a
/// getter-only property refuses, and <c>fixed</c> refuses after initialization. A static that
/// answered differently from an instance property would be a second rule for no reason — and
/// declaration-time initialization goes through its own entry point rather than a flag, so
/// <c>fixed static prop S = 1</c> can still reach 1.
/// </para>
/// </remarks>
public sealed class StaticMemberAssignmentTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private static async Task<ToshDiagnostic> RunForDiagnosticAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(source));
        return Assert.Single(exception.Diagnostics);
    }

    // ── The reported case ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_static_property_can_be_assigned_and_read_back()
    {
        Assert.Equal("5", await RunAsync(
            """
            class B { static prop S = 1 }
            B.S = 5
            B.S
            """));
    }

    [Fact]
    public async Task Assigning_through_a_subclass_writes_the_declaring_class_slot()
    {
        // The half that would be easy to get wrong: a subclass must not acquire a copy. Both
        // spellings have to answer 7 afterwards or the class and its subclass have silently
        // diverged — which is what `TrySetStaticMember`'s base walk was written for.
        Assert.Equal("7,7", await RunAsync(
            """
            class B { static prop S = 1 }
            class D extends B { }
            D.S = 7
            B.S
            D.S
            """));
    }

    [Fact]
    public async Task Assigning_on_the_declaring_class_is_seen_through_the_subclass()
    {
        Assert.Equal("7,7", await RunAsync(
            """
            class B { static prop S = 1 }
            class D extends B { }
            B.S = 7
            B.S
            D.S
            """));
    }

    // ── Every assignment operator ──────────────────────────────────────────────

    [Theory]
    [InlineData("B.S += 2", "3")]
    [InlineData("B.S -= 1", "0")]
    [InlineData("B.S *= 4", "4")]
    [InlineData("B.S = 9", "9")]
    public async Task Compound_assignment_reads_then_writes_the_static(string assignment, string expected)
    {
        Assert.Equal(expected, await RunAsync(
            $"class B {{ static prop S = 1 }}\n{assignment}\nB.S"));
    }

    [Fact]
    public async Task Null_coalescing_assignment_fills_an_unset_static()
    {
        Assert.Equal("4", await RunAsync(
            """
            class B { static prop S = null }
            B.S ??= 4
            B.S
            """));
    }

    [Fact]
    public async Task Null_coalescing_assignment_leaves_a_set_static_alone()
    {
        Assert.Equal("1", await RunAsync(
            """
            class B { static prop S = 1 }
            B.S ??= 4
            B.S
            """));
    }

    // ── The instance rules, applied to statics ─────────────────────────────────

    [Fact]
    public async Task A_static_setter_body_runs_instead_of_storing()
    {
        // Also covers assigning a static from *inside* a class body, which is the same
        // statement form reached through a different scope.
        Assert.Equal("4", await RunAsync(
            """
            class B {
                static prop Raw = 0
                static prop S { get { return B.Raw } set { B.Raw = $value } }
            }
            B.S = 4
            B.S
            """));
    }

    [Fact]
    public async Task A_computed_static_with_no_setter_refuses_assignment()
    {
        var diagnostic = await RunForDiagnosticAsync("class B { static prop Y => 7 }\nB.Y = 9");

        Assert.Equal("tosh.runtime.member_assignment_failed", diagnostic.Code);
        Assert.Contains("read-only", diagnostic.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_fixed_static_refuses_reassignment()
    {
        var diagnostic = await RunForDiagnosticAsync("class B { fixed static prop S = 1 }\nB.S = 9");

        Assert.Equal("tosh.runtime.member_assignment_failed", diagnostic.Code);
        Assert.Contains("fixed", diagnostic.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_fixed_static_still_reaches_its_declared_value()
    {
        // The control for the rule above: `fixed` must refuse a later write without refusing
        // the declaration's own initializer, which travels the same code path.
        Assert.Equal("1", await RunAsync("class B { fixed static prop S = 1 }\nB.S"));
    }

    [Fact]
    public async Task An_undeclared_static_member_is_reported_as_missing()
    {
        var diagnostic = await RunForDiagnosticAsync("class B { static prop S = 1 }\nB.Nope = 9");

        Assert.Equal("tosh.runtime.member_assignment_failed", diagnostic.Code);
        Assert.Contains("not found", diagnostic.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_enum_member_is_reported_as_read_only_rather_than_missing()
    {
        // "Not found" about a member whose value the user can print would send them looking
        // for a typo, so the refusal asks whether the member reads before it answers.
        var diagnostic = await RunForDiagnosticAsync("enum F : int { A = 1 }\nF.A = 2");

        Assert.Equal("tosh.runtime.member_assignment_failed", diagnostic.Code);
        Assert.Contains("read-only", diagnostic.Title, StringComparison.OrdinalIgnoreCase);
    }

    // ── Nested types ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_static_on_a_nested_class_is_assignable_by_its_qualified_name()
    {
        // `Outer.Inner.V` has to split as type `Outer.Inner` plus member `V`, not type `Outer`
        // plus member path `Inner.V`. Longest prefix first is what decides it.
        Assert.Equal("9", await RunAsync(
            """
            class Outer { class Inner { static prop V = 1 } }
            Outer.Inner.V = 9
            Outer.Inner.V
            """));
    }

    // ── CLR statics ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_clr_static_is_split_at_the_type_rather_than_the_namespace()
    {
        // `System.Math.PI` must find the type `System.Math`, not stop at the namespace. The
        // refusal proves the split landed: a wrong split cannot name the field at all.
        var diagnostic = await RunForDiagnosticAsync("System.Math.PI = 3");

        Assert.Equal("tosh.runtime.member_assignment_failed", diagnostic.Code);
        Assert.Contains("read-only", diagnostic.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.Math", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_clr_static_member_is_reported_against_its_type()
    {
        var diagnostic = await RunForDiagnosticAsync("System.Math.Nope = 3");

        Assert.Equal("tosh.runtime.member_assignment_failed", diagnostic.Code);
        Assert.Contains("not found", diagnostic.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.Math", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_settable_clr_static_property_is_written()
    {
        StaticAssignmentProbe.Reset();

        Assert.Equal("41", await RunAsync(
            $"{typeof(StaticAssignmentProbe).FullName}.Value = 41\n"
            + $"{typeof(StaticAssignmentProbe).FullName}.Value"));
        Assert.Equal(41, StaticAssignmentProbe.Value);
    }

    [Fact]
    public async Task A_settable_clr_static_field_is_written()
    {
        StaticAssignmentProbe.Reset();

        Assert.Equal("7", await RunAsync(
            $"{typeof(StaticAssignmentProbe).FullName}.Count = 7\n"
            + $"{typeof(StaticAssignmentProbe).FullName}.Count"));
        Assert.Equal(7, StaticAssignmentProbe.Count);
    }

    // ── The forgotten `$`, moved rather than lost ──────────────────────────────

    [Fact]
    public void A_dollarless_member_assignment_no_longer_fails_to_parse()
    {
        // The phase move, stated directly: the parser can no longer tell these apart, so it
        // stops guessing. Losing the diagnostic entirely is what the next test rules out.
        Assert.Empty(ToshParser.Parse("person.Name = \"toast\"").Diagnostics);
        Assert.Empty(ToshParser.Parse("B.S = 5").Diagnostics);
    }

    [Fact]
    public async Task A_forgotten_dollar_is_still_named_as_the_problem()
    {
        var diagnostic = await RunForDiagnosticAsync(
            """
            var person = {| Name = "a" |}
            person.Name = "toast"
            """);

        Assert.Equal("tosh.runtime.variable_reference_requires_dollar", diagnostic.Code);
        Assert.Contains("$person.Name", diagnostic.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_variable_beats_a_type_of_the_same_name()
    {
        // Type resolution matches simple names across every loaded assembly, ignoring case, so
        // a variable can collide with a type nobody in this script has heard of. The full suite
        // hit exactly that — a compiler test had emitted a `Person`, and `person.Name = "x"`
        // stopped reporting the forgotten `$`. The variable is asked about first because a
        // wrong hint costs a message while a wrong static write mutates shared state.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        // Half one: the collision is real, or the rest of this test proves nothing.
        Assert.NotNull(engine.TryResolveTypeName("staticassignmentprobe"));

        StaticAssignmentProbe.Reset();
        await engine.ExecuteToListAsync("var staticassignmentprobe = {| Value = 1 |}");

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("staticassignmentprobe.Value = 41"));

        Assert.Equal("tosh.runtime.variable_reference_requires_dollar", exception.Diagnostics[0].Code);
        Assert.Equal(0, StaticAssignmentProbe.Value);
    }

    [Fact]
    public async Task A_head_that_names_neither_type_nor_variable_says_so()
    {
        var diagnostic = await RunForDiagnosticAsync("nope.Thing = 1");

        Assert.Equal("tosh.runtime.unknown_static_assignment_target", diagnostic.Code);
        Assert.Contains("nope", diagnostic.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_variable_assignment_still_demands_its_dollar_at_parse_time()
    {
        // Only the *member* form moved. `foo = 1` has no member path, is parsed by a different
        // statement form, and is still refused before anything runs — nothing about a type
        // could make that spelling mean something else.
        var diagnostic = Assert.Single(ToshParser.Parse("foo = 1").Diagnostics);

        Assert.Equal("tosh.parser.variable_references_require_dollar", diagnostic.Code);
    }

    [Fact]
    public async Task The_hint_survives_a_member_path_written_as_a_separate_token()
    {
        // `foo .Bar` reaches the target parser with the path in a following token rather than
        // embedded in the first, which is a different route to the same node.
        var diagnostic = await RunForDiagnosticAsync(
            """
            var foo = {| Bar = 1 |}
            foo .Bar = 2
            """);

        Assert.Equal("tosh.runtime.variable_reference_requires_dollar", diagnostic.Code);
        Assert.Contains("$foo.Bar", diagnostic.Label, StringComparison.Ordinal);
    }

    // ── Nothing that already worked changed ────────────────────────────────────

    [Fact]
    public async Task Assigning_a_record_member_through_a_variable_is_unchanged()
    {
        Assert.Equal("b", await RunAsync(
            """
            var p = {| Name = "a" |}
            $p.Name = "b"
            $p.Name
            """));
    }

    [Fact]
    public async Task Assigning_an_index_through_a_variable_is_unchanged()
    {
        Assert.Equal("5", await RunAsync(
            """
            var d = {% "k" => 1 %}
            $d["k"] = 5
            $d["k"]
            """));
    }

    [Fact]
    public async Task Assigning_a_clr_instance_property_is_unchanged()
    {
        Assert.Equal("to", await RunAsync(
            """
            var sb = new System.Text.StringBuilder("toast")
            $sb.Length = 2
            $sb.ToString()
            """));
    }

    [Fact]
    public async Task Assigning_an_instance_property_of_a_tosh_class_is_unchanged()
    {
        Assert.Equal("9", await RunAsync(
            """
            class B { prop S = 1 }
            var b = new B()
            $b.S = 9
            $b.S
            """));
    }

    [Fact]
    public async Task Reading_a_static_is_unchanged()
    {
        Assert.Equal("1,1", await RunAsync(
            """
            class B { static prop S = 1 }
            class D extends B { }
            B.S
            D.S
            """));
    }
}

/// <summary>
/// A CLR type owned by the tests, so the settable half of the static assignment path can be
/// asserted without writing to any static the rest of the process reads.
/// </summary>
public static class StaticAssignmentProbe
{
    public static int Value { get; set; }

    public static int Count;

    public static void Reset()
    {
        Value = 0;
        Count = 0;
    }
}
