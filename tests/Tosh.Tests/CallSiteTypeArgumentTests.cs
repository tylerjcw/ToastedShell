using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Type arguments written at a member call site — <c>TS-P2-49</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>$a.m&lt;int&gt;(11)</c> did not parse, while the free-function form and inference both did.
/// Three pieces were needed: the parser reading the list, the engine resolving the names, and the
/// invoker carrying them to whichever route serves the call.
/// </para>
/// <para>
/// Names resolve in the engine rather than the invoker, because scope, aliases and
/// ToastScript-declared types are knowledge the invoker does not have. And the invoker's overload
/// <i>refuses</i> by default rather than ignoring: a target that does not understand type
/// arguments says so, because binding by inference instead would answer a request for a specific
/// instantiation with a different one — worse than the parse error this replaced.
/// </para>
/// </remarks>
public sealed class CallSiteTypeArgumentTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    private const string Generic =
        "class A { func m<U>(x: U) -> U { return $x } }\nvar a = new A()\n";

    // ── a generic class can see what it was closed over ───────────────────────
    //
    // `type-of $v` answered `Box<Int32>` and carried TypeArguments, while `type-of
    // $this` inside that same class answered a bare `Box` and carried none: the
    // self-reference handed back the class's *open* definition rather than the
    // instance's own descriptor. The only way to recover T from the inside was to
    // ask a member that happened to be typed T.

    [Fact]
    public async Task An_expression_in_a_type_annotation_says_what_went_wrong()
    {
        // `var y: $x.Type = $x.Value` is a natural thing to reach for once `type-of`
        // can hand back a type, and it cannot work: an annotation is resolved where
        // the declaration is written, and a value exists only once the program runs.
        // The declaration simply stopped looking like one, so the line was re-read as
        // a command and the reader was told `Command 'var' is not a registered
        // builtin — did you mean 'vars'?`, which describes nothing that happened.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                class Box<T>(v: T) {
                    prop Value: T = $v
                    prop Type => ((type-of $this).TypeArguments[0])
                }
                var x = new Box(13)
                var y: $x.Type = $x.Value
                """));

        Assert.Contains(error.Diagnostics, d => d.Code == "tosh.parser.expression_type_annotation");
    }

    [Theory]
    [InlineData("var a: int = 5", "5")]
    [InlineData("var a: string = \"hi\"", "hi")]
    [InlineData("const a: double = 1.5", "1.5")]
    public async Task An_ordinary_type_annotation_still_parses(string declaration, string expected)
    {
        // The check above must recognise an expression in the annotation slot without
        // taking any real annotation with it.
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync($"{declaration}\necho $a");

        Assert.Equal(expected, Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task A_generic_class_reads_its_own_type_argument_from_the_inside()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            class Box<T>(v: T) {
                prop Value: T = $v
                prop TypeOfT => ((type-of $this).TypeArguments[0])
            }
            echo (new Box<int>(5)).TypeOfT.Name
            """);

        Assert.Equal("Int32", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task The_inside_and_outside_views_of_a_closed_generic_agree()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var results = await engine.ExecuteToListAsync(
            """
            class Box<T>(v: T) {
                prop Value: T = $v
                prop SeenFromInside => ((type-of $this).Name)
            }
            var b = new Box<int>(5)
            echo $"{$b.SeenFromInside}:{((type-of $b).Name)}"
            """);

        Assert.Equal("Box<Int32>:Box<Int32>", Assert.Single(results)?.ToString());
    }

    [Fact]
    public async Task A_member_call_accepts_explicit_type_arguments()
    {
        Assert.Equal("11", await RunAsync($"{Generic}$a.m<int>(11)"));
    }

    [Fact]
    public async Task An_inherited_generic_method_accepts_them_too()
    {
        Assert.Equal("5", await RunAsync(
            "class B { func m<U>(x: U) -> U { return $x } }\nclass D extends B { }\n(new D()).m<int>(5)"));
    }

    [Fact]
    public async Task Explicit_arguments_replace_inference_rather_than_merging()
    {
        // Asking for <string> while passing an int must not quietly bind U to int. The conversion
        // is what fails, which is the point: the request was honoured, not second-guessed.
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync($"{Generic}$a.m<string>(11)"));
    }

    [Fact]
    public async Task An_arity_mismatch_is_refused()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(
            async () => await RunAsync($"{Generic}$a.m<int, string>(11)"));

        Assert.Contains("type parameter", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_non_generic_method_refuses_type_arguments()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            "class A { func plain(x) { return $x } }\nvar a = new A()\n$a.plain<int>(11)"));

        Assert.Contains("not generic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unresolvable_type_argument_is_a_diagnostic()
    {
        // Never a quiet fall back to inference.
        await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync($"{Generic}$a.m<NoSuchType>(11)"));
    }


    // ── TOAST-0116: a rebuild inside a method keeps the closure ───────────────
    //
    // `new A<T>(…)` written inside a method of `A<T>` resolved `T` by name against the
    // session's types, found nothing, and bound null. `type-of` then answered `A<T>`,
    // and — because a null binding is read as "nominal only, accept anything" — the
    // rebuilt value stopped enforcing its own `where` clause. Every ToastLib operator
    // result was that shape, since each ends in `new Point2D<T>(…)`.

    private const string Rebuilding =
        "class A<T>(x: T) where T: Numeric {\n"
        + "    prop X: T = $x\n"
        + "    func Rebuild() -> A<T> { return (new A<T>($this.X)) }\n"
        + "    func Concrete() { return (new A<int>(7)) }\n"
        + "}\n";

    [Theory]
    [InlineData("(new A<int>(1))", "A<Int32>")]
    [InlineData("(new A<int>(1)).Rebuild()", "A<Int32>")]
    [InlineData("(new A<int>(1)).Rebuild().Rebuild()", "A<Int32>")]
    [InlineData("(new A<double>(1.5)).Rebuild()", "A<Double>")]
    // A rebuild that names a real type is not a type parameter and must be left alone.
    [InlineData("(new A<double>(1.5)).Concrete()", "A<Int32>")]
    public async Task A_rebuilt_instance_reports_what_it_was_closed_over(string expression, string expected)
    {
        Assert.Equal(expected, await RunAsync($"{Rebuilding}var t = (type-of {expression})\n$t.Name"));
    }

    [Fact]
    public async Task A_rebuilt_instance_still_enforces_its_constraint()
    {
        // The reason this is a soundness hole rather than a display problem.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await RunAsync(
            $"{Rebuilding}var a = (new A<int>(1))\nvar r = $a.Rebuild()\n$r.X = \"not a number\""));

        Assert.Contains("A.X", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_type_parameters_are_matched_by_name_not_by_position()
    {
        // `new Pair<V, K>` inside `Pair<K, V>` swaps them. Resolving positionally would
        // silently keep the original order and produce a value whose type is a lie.
        var source =
            "class Pair<K, V>(k: K, v: V) {\n"
            + "    prop K1: K = $k\n"
            + "    prop V1: V = $v\n"
            + "    func Swap() -> Pair<V, K> { return (new Pair<V, K>($this.V1, $this.K1)) }\n"
            + "}\n"
            + "var t = (type-of (new Pair<int, string>(1, \"a\")).Swap())\n"
            + "$t.Name";

        Assert.Equal("Pair<String, Int32>", await RunAsync(source));
    }

    [Fact]
    public async Task A_base_class_method_resolves_its_own_type_parameter()
    {
        // The bindings are keyed on the class whose code is running, so a base method
        // writing `new Base<T>` reads `T` as Base declared it.
        var source =
            "class Base<T>(x: T) {\n"
            + "    prop X: T = $x\n"
            + "    func Mine() -> Base<T> { return (new Base<T>($this.X)) }\n"
            + "}\n"
            + "var t = (type-of (new Base<int>(2)).Mine())\n"
            + "$t.Name";

        Assert.Equal("Base<Int32>", await RunAsync(source));
    }

    [Fact]
    public async Task A_record_rebuilt_inside_a_generic_class_resolves_its_field_type()
    {
        // Records take a different construction path and had the same hole, reported as
        // `'R.Value' uses unknown type annotation 'T'` against the record's declaration —
        // several lines from where the mistake was written.
        var source =
            "record R<T>(Value: T)\n"
            + "class Holder<T>(v: T) {\n"
            + "    prop V: T = $v\n"
            + "    func Wrap() -> R<T> { return (new R<T>($this.V)) }\n"
            + "}\n"
            + "(new Holder<int>(5)).Wrap().Value";

        Assert.Equal("5", await RunAsync(source));
    }

    [Fact]
    public async Task A_union_variant_built_inside_a_generic_class_resolves_its_payload()
    {
        // Unions bind their arguments as annotation *names* rather than CLR types, so they
        // needed the same resolution on their own path.
        var source =
            "union Box<T> { Full(v: T), Empty }\n"
            + "class UHolder<T>(v: T) {\n"
            + "    prop V: T = $v\n"
            + "    func Wrap() { return (Box.Full<T>($this.V)) }\n"
            + "}\n"
            + "var t = (type-of (new UHolder<int>(5)).Wrap())\n"
            + "$t.Name";

        Assert.Equal("Box<int>", await RunAsync(source));
    }

    [Theory]
    // The controls: inference, and the comparison that makes `<` ambiguous in the first place.
    [InlineData("class A { func m<U>(x: U) -> U { return $x } }\nvar a = new A()\n$a.m(11)", "11")]
    [InlineData("var a = 1\nvar b = 2\n($a < $b)", "True")]
    [InlineData("class C { prop V = 1 }\nvar c = new C()\nvar b = 5\n($c.V < $b)", "True")]
    public async Task Forms_that_already_worked_are_unchanged(string source, string expected)
    {
        Assert.Equal(expected, await RunAsync(source));
    }
}
