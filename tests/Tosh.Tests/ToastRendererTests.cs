using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// The portable value-rendering contract — `TOAST-0014`, Phase A stage 1.
///
/// Every case here is written from `docs/plan/SPEC_DRAFT_value_rendering.md`, **not** from
/// what the current formatter happens to produce. That direction is the point: nine of
/// these disagree with today's `$"{x}"`, and a corpus derived from current behaviour would
/// have pinned the bugs instead of the contract.
///
/// `ToastRenderer` is not wired to anything yet. It is built and pinned first so the
/// behaviour change lands as one reviewable flip of the four language call sites, rather
/// than as a rewrite whose correctness has to be argued from the diff.
///
/// The rule the whole thing exists for: **rendering never consults display
/// configuration.** `$"{$d}"` produced three different strings depending on
/// `$tosh.Config.Display.DateTime.ScalarMode`, changed mid-script. This type cannot do that
/// because it has no way to reach a profile, a preference, or a `DisplayEngine`.
/// </summary>
public sealed class ToastRendererTests
{
    private static async Task<object?> EvalAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? null : results[^1];
    }

    private static async Task<string> RenderScriptAsync(string source)
        => ToastRenderer.Render(await EvalAsync(source));

    // ------------------------------------------------------------------ scalars

    [Theory]
    [InlineData(null, "null")]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(42, "42")]
    [InlineData(-7, "-7")]
    [InlineData(9999999999L, "9999999999")]
    [InlineData(3.14, "3.14")]
    [InlineData(0.1, "0.1")]
    [InlineData('c', "c")]
    public void Scalars_render_as_specified(object? value, string expected)
        => Assert.Equal(expected, ToastRenderer.Render(value));

    /// <summary>
    /// The special floating values are named rather than spelled by the platform, and
    /// **negative zero keeps its sign**. `-0` and `0` are different values; collapsing them
    /// would make output depend on how a zero was reached.
    /// </summary>
    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void The_special_floating_values_are_named(double value, string expected)
        => Assert.Equal(expected, ToastRenderer.Render(value));

    [Fact]
    public void Negative_zero_keeps_its_sign()
    {
        Assert.Equal("-0", ToastRenderer.Render(-0.0));
        Assert.Equal("0", ToastRenderer.Render(0.0));
    }

    /// <summary>
    /// Rendering is invariant. A machine whose locale uses a comma decimal separator still
    /// renders `3.14` — a program's output must not depend on where it runs, for the same
    /// reason it must not depend on how the shell is configured.
    /// </summary>
    [Fact]
    public void Rendering_does_not_follow_the_machine_locale()
    {
        var original = Thread.CurrentThread.CurrentCulture;

        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("3.14", ToastRenderer.Render(3.14));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // ------------------------------------------------------------------ strings

    /// <summary>
    /// A string is its own characters at the top level — the case where the caller is
    /// putting text into a sentence.
    /// </summary>
    [Theory]
    [InlineData("hi", "hi")]
    [InlineData("", "")]
    [InlineData(" x ", " x ")]
    [InlineData("he said \"hi\"", "he said \"hi\"")]
    public void A_string_renders_bare_at_the_top_level(string value, string expected)
        => Assert.Equal(expected, ToastRenderer.Render(value));

    /// <summary>
    /// Nested, it is quoted and escaped. Without this a container of strings is ambiguous:
    /// `["a b", "c"]` reads as three words, and the string `"null"` as the value `null`.
    /// </summary>
    [Fact]
    public void A_nested_string_is_quoted_and_escaped()
    {
        Assert.Equal("""["a b", "c"]""", ToastRenderer.Render(new object?[] { "a b", "c" }));
        Assert.Equal("""["null", null]""", ToastRenderer.Render(new object?[] { "null", null }));
        Assert.Equal("""["a\nb"]""", ToastRenderer.Render(new object?[] { "a\nb" }));
        Assert.Equal("""["a\"b"]""", ToastRenderer.Render(new object?[] { "a\"b" }));
    }

    // --------------------------------------------------------------- containers

    /// <summary>
    /// A container renders in its own literal syntax. Today a list renders `1 2 3` at the
    /// top level and something else entirely when nested; this is the single largest
    /// change in the contract.
    /// </summary>
    [Fact]
    public async Task Containers_render_in_their_own_literal_syntax()
    {
        Assert.Equal("[1, 2, 3]", await RenderScriptAsync("[1, 2, 3]"));
        Assert.Equal("[]", await RenderScriptAsync("[]"));
        Assert.Equal("""{% "a" => 1 %}""", await RenderScriptAsync("""{% "a" => 1 %}"""));
        Assert.Equal("""{| Name = "a", N = 1 |}""", await RenderScriptAsync("""{| Name = "a", N = 1 |}"""));
        Assert.Equal("(1, \"a\")", await RenderScriptAsync("""(1, "a")"""));
    }

    /// <summary>
    /// **Uniform at every depth**, which is the rule that fixes the worst of the current
    /// behaviour: a list renders three different ways today depending on whether it is the
    /// whole hole, an element, or a dictionary value — one of which is the bare CLR type
    /// name `System.Int32[]` with the contents missing.
    /// </summary>
    [Fact]
    public async Task A_container_renders_the_same_way_at_every_depth()
    {
        Assert.Equal("[[1, 2], [3]]", await RenderScriptAsync("[[1, 2], [3]]"));
        Assert.Equal("""{% "k" => [1, 2] %}""", await RenderScriptAsync("""{% "k" => [1, 2] %}"""));
        Assert.Equal("{| Items = [1, 2] |}", await RenderScriptAsync("{| Items = [1, 2] |}"));
        Assert.Equal("""[{% "a" => 1 %}]""", await RenderScriptAsync("""[{% "a" => 1 %}]"""));
    }

    /// <summary>
    /// No CLR type name ever appears. `Int32[]`, `System.Int32[][]` and `Object[]` all leak
    /// into rendered strings today, in a language whose specification is meant to be
    /// independent of BCL names.
    /// </summary>
    [Fact]
    public async Task No_clr_type_name_appears_in_a_rendered_container()
    {
        var rendered = await RenderScriptAsync("[[[1]]]");

        Assert.Equal("[[[1]]]", rendered);
        Assert.DoesNotContain("Int32", rendered);
        Assert.DoesNotContain("System.", rendered);
        Assert.DoesNotContain("[]", rendered.Replace("[[[1]]]", string.Empty));
    }

    /// <summary>
    /// A range renders as a range, not as the list it would produce. Materialising it loses
    /// the distinction between the range and its elements.
    /// </summary>
    /// <remarks>
    /// Built directly rather than evaluated, because a range in expression position is
    /// materialised by the pipeline before a test could see it — the harness would render
    /// the last element, not the range.
    /// </remarks>
    [Fact]
    public void A_range_renders_as_a_range()
    {
        Assert.Equal("1..3", ToastRenderer.Render(new ToshRange(1, null, 3)));
        Assert.Equal("1..", ToastRenderer.Render(new ToshRange(1, null, null)));
    }

    // ------------------------------------------------------------ named values

    /// <summary>
    /// An enum member renders as its name. Today it renders its own implementation —
    /// `Definition`, `EnumTypeName`, `ShellTypeDescriptor`, `UnderlyingValue` — where the
    /// reader wrote `Color.Red`.
    /// </summary>
    [Fact]
    public async Task An_enum_member_renders_as_its_name()
        => Assert.Equal("Red", await RenderScriptAsync("enum Color { Red, Green }\nColor.Red"));

    /// <summary>
    /// A class carries its type name, so it is distinguishable from a record. Today both
    /// render `{| N = 5 |}` and a reader cannot tell a `Point` from an anonymous record
    /// with the same fields.
    /// </summary>
    [Fact]
    public async Task A_class_renders_with_its_type_name()
        => Assert.Equal("R { N = 5 }", await RenderScriptAsync("class R { prop N: int = 5 }\nnew R()"));

    /// <summary>
    /// A record does not, because its literal syntax already says what it is.
    /// </summary>
    [Fact]
    public async Task A_record_renders_without_a_type_name()
        => Assert.Equal("{| N = 5 |}", await RenderScriptAsync("{| N = 5 |}"));

    /// <summary>
    /// The decided extension point: a type controls its own rendering by implementing the
    /// `Display` trait.
    /// </summary>
    [Fact]
    public async Task A_type_implementing_Display_renders_through_it()
        => Assert.Equal("21degC", await RenderScriptAsync(
            """
            trait Display { func render() -> string }
            class Temperature uses Display {
                prop Celsius: int = 21
                func render() -> string => $"{$this.Celsius}degC"
            }
            new Temperature()
            """));

    /// <summary>
    /// `ToString` still works, so classes written before the trait existed keep rendering
    /// as their authors intended. `Display` is what the spec teaches; `ToString` is what it
    /// tolerates.
    /// </summary>
    [Fact]
    public async Task A_declared_ToString_is_still_honoured()
        => Assert.Equal("Q<5>", await RenderScriptAsync(
            """
            class Q { prop N: int = 5
              func ToString() -> string => $"Q<{$this.N}>" }
            new Q()
            """));

    /// <summary>
    /// A method named `render` on a type that does not implement `Display` is **not** the
    /// extension point. The trait is the contract; the name alone is a coincidence.
    /// </summary>
    [Fact]
    public async Task A_render_method_without_the_trait_is_not_the_extension_point()
        => Assert.Equal("N { N = 1 }", await RenderScriptAsync(
            """
            class N { prop N: int = 1
              func render() -> string => "not this" }
            new N()
            """));

    // --------------------------------------------------------- depth and cycles

    /// <summary>
    /// Rendering is bounded, and the bound is fixed rather than configurable — a
    /// configurable depth would make output depend on configuration, which is the defect
    /// being removed.
    /// </summary>
    [Fact]
    public void Depth_is_bounded()
    {
        object? nest = 1;

        for (var i = 0; i < ToastRenderer.MaximumDepth + 4; i++)
        {
            nest = new object?[] { nest };
        }

        var rendered = ToastRenderer.Render(nest);

        Assert.Contains("…", rendered);
        Assert.True(rendered.Length < 200, $"expected an elided rendering, got {rendered.Length} chars");
    }

    /// <summary>
    /// A cycle elides rather than recursing, and detection is by reference identity — a
    /// sibling that merely *equals* an ancestor is a different value and must render in
    /// full.
    /// </summary>
    [Fact]
    public void A_cycle_elides_but_a_repeated_equal_value_does_not()
    {
        var cyclic = new object?[2];
        cyclic[0] = 1;
        cyclic[1] = cyclic;

        Assert.Equal("[1, …]", ToastRenderer.Render(cyclic));

        var shared = new object?[] { 1 };
        Assert.Equal("[[1], [1]]", ToastRenderer.Render(new object?[] { shared, shared }));
    }

    // ------------------------------------------------------------------ formats

    /// <summary>
    /// A format clause is the same operation with a different argument, not a second
    /// mechanism.
    /// </summary>
    [Theory]
    [InlineData(42, "X", "2A")]
    [InlineData(42, "D5", "00042")]
    [InlineData(3.14159, "F2", "3.14")]
    public void A_format_clause_selects_a_rendering(object value, string format, string expected)
        => Assert.Equal(expected, ToastRenderer.Render(value, format));

    /// <summary>
    /// A clause the value cannot honour is an error. Today it is silently ignored and the
    /// value renders plainly — the same silent-wrong-answer shape as `TOSH-0001`, where a
    /// program succeeds and produces text nobody asked for.
    /// </summary>
    [Fact]
    public void A_clause_the_value_cannot_honour_is_an_error()
    {
        Assert.Throws<FormatException>(() => ToastRenderer.Render("a string", "F2"));
        Assert.Throws<FormatException>(() => ToastRenderer.Render(true, "X"));
        Assert.Throws<FormatException>(() => ToastRenderer.Render(null, "F2"));
    }

    // --------------------------------------------------------------- the point

    /// <summary>
    /// The assertion the whole item exists to make: rendering does not move when display
    /// configuration does. `$"{$d}"` gives three different strings today.
    /// </summary>
    [Fact]
    public void Rendering_does_not_change_when_display_configuration_does()
    {
        var runtime = ToshRuntime.CreateDefault();
        var value = new DateTime(2026, 8, 17, 12, 0, 0);

        var before = ToastRenderer.Render(value);

        runtime.Config.Display.DateTime.ScalarMode = TemporalDisplayMode.Unix;
        Assert.Equal(before, ToastRenderer.Render(value));

        runtime.Config.Display.DateTime.ScalarMode = TemporalDisplayMode.Relative;
        Assert.Equal(before, ToastRenderer.Render(value));
    }

    /// <summary>
    /// And an unspecified `DateTime` is not shifted by the local offset — `TOAST-0017`.
    /// Writing `12:00` and reading back `08:00` is wrong whatever the configuration says.
    /// </summary>
    [Fact]
    public void An_unspecified_datetime_is_not_shifted()
    {
        var rendered = ToastRenderer.Render(new DateTime(2026, 8, 17, 12, 0, 0));

        Assert.Contains("12:00:00", rendered);
    }

    /// <summary>
    /// Structural, not behavioural: the renderer must not be able to reach display at all.
    /// A rule held by discipline is a rule that erodes; this one is held by the type graph.
    /// </summary>
    [Fact]
    public void The_renderer_holds_no_reference_to_display_machinery()
    {
        var referenced = typeof(ToastRenderer).Assembly
            .GetType(typeof(ToastRenderer).FullName!)!
            .GetFields(System.Reflection.BindingFlags.Static |
                       System.Reflection.BindingFlags.Public |
                       System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType.Name)
            .ToArray();

        Assert.DoesNotContain("DisplayProfileRegistry", referenced);
        Assert.DoesNotContain("DisplayPreferences", referenced);
        Assert.DoesNotContain("DisplayEngine", referenced);
        Assert.DoesNotContain("ObjectFormatter", referenced);
    }
}
