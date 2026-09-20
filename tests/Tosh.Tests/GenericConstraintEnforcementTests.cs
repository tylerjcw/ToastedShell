using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Constraints other than `Numeric`, and a closed generic contract — <c>TOAST-0125</c>.
/// </summary>
/// <remarks>
/// <para>
/// `where T: SomeInterface` and `where T: SomeBaseClass` were parsed, recorded and never
/// consulted: the checker accepted conservatively whenever the argument was not a
/// ToastScript class it recognised, which is every CLR type. So `where T: Drawable` took an
/// `int`. Only the built-in `Numeric` was ever enforced.
/// </para>
/// <para>
/// A CLR type cannot implement a ToastScript interface or extend a ToastScript class, so a
/// bound CLR argument is a definite failure rather than something to be conservative about.
/// A genuinely unrecognised name still is: refusing what cannot be checked turns an
/// unenforced constraint into a wrong error.
/// </para>
/// </remarks>
public sealed class GenericConstraintEnforcementTests
{
    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);
        var results = await engine.ExecuteToListAsync(source);
        return results.Count == 0 ? string.Empty : results[^1]?.ToString() ?? "null";
    }

    private const string Interfaces =
        "interface HasGet { func Get() -> int }\n"
        + "class Getter() fulfills HasGet { func Get() -> int => 7 }\n"
        + "class Plain() { }\n"
        + "class IfaceCon<T>(x: T) where T: HasGet { prop X: T = $x }\n";

    private const string Classes =
        "class Shape(n) { prop N = $n }\n"
        + "class Dot(n) extends Shape($n) { }\n"
        + "class Other() { }\n"
        + "class BaseCon<T>(x: T) where T: Shape { prop X: T = $x }\n";

    [Fact]
    public async Task An_interface_constraint_accepts_a_conforming_class()
        => Assert.Equal("7", await RunAsync($"{Interfaces}(new IfaceCon<Getter>(new Getter())).X.Get()"));

    [Theory]
    [InlineData("new IfaceCon<Plain>(new Plain())")]
    [InlineData("new IfaceCon<int>(1)")]
    [InlineData("new IfaceCon<string>(\"a\")")]
    public async Task An_interface_constraint_refuses_everything_else(string construction)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync($"{Interfaces}{construction}"));
        Assert.Contains("to satisfy 'HasGet'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    // A class satisfies its own name and anything extending it, as in C#.
    [InlineData("new BaseCon<Shape>(new Shape(1))", "1")]
    [InlineData("new BaseCon<Dot>(new Dot(2))", "2")]
    public async Task A_base_class_constraint_accepts_the_class_and_its_subclasses(
        string construction,
        string expected)
        => Assert.Equal(expected, await RunAsync($"{Classes}({construction}).X.N"));

    [Theory]
    [InlineData("new BaseCon<Other>(new Other())")]
    [InlineData("new BaseCon<int>(1)")]
    public async Task A_base_class_constraint_refuses_everything_else(string construction)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync($"{Classes}{construction}"));
        Assert.Contains("to satisfy 'Shape'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_message_does_not_invent_a_clr_name_for_a_script_type()
    {
        // A ToastScript class has no CLR type of its own, and saying `(CLR <unresolved>)`
        // about it reads as a second failure.
        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => RunAsync($"{Interfaces}new IfaceCon<Plain>(new Plain())"));

        Assert.Contains("'Plain' does not", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("<unresolved>", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    // `Numeric` is unchanged, and an unconstrained parameter still takes anything.
    [InlineData("class N<T>(x: T) where T: Numeric { prop X: T = $x }\n(new N<int>(1)).X", "1")]
    [InlineData("class U<T>(x: T) { prop X: T = $x }\n(new U<int>(1)).X", "1")]
    public async Task The_built_in_constraint_and_the_unconstrained_case_are_unchanged(
        string source,
        string expected)
        => Assert.Equal(expected, await RunAsync(source));

    // ── a closed generic contract ────────────────────────────────────────────

    [Theory]
    // `interface Co<T> { func Get() -> T }` fulfilled as `Co<int>` declares `Get` returning
    // `int`. Comparing the class's `-> int` against the unsubstituted `T` reported a
    // mismatch on every correct implementation there is.
    [InlineData("class I() fulfills Co<int> { func Get() -> int => 1 }\n(new I()).Get()", "1")]
    [InlineData("class S() fulfills Co<string> { func Get() -> string => \"s\" }\n(new S()).Get()", "s")]
    [InlineData("class O<T>(v: T) fulfills Co<T> { prop V: T = $v\n func Get() -> T => $this.V }\n(new O<int>(4)).Get()", "4")]
    public async Task A_closed_generic_interface_is_implemented_without_complaint(
        string implementation,
        string expected)
    {
        var source = "interface Co<T> { func Get() -> T }\n" + implementation;
        Assert.Equal(expected, await RunAsync(source));
    }

    [Fact]
    public async Task Two_parameters_are_both_substituted()
    {
        var source =
            "interface Two<A, B> { func Map(a: A) -> B }\n"
            + "class M() fulfills Two<int, string> { func Map(a: int) -> string => \"m\" }\n"
            + "(new M()).Map(1)";

        Assert.Equal("m", await RunAsync(source));
    }

    [Fact]
    public async Task A_genuine_mismatch_is_still_caught_and_names_the_closed_type()
    {
        var source =
            "interface Co<T> { func Get() -> T }\n"
            + "class Wrong() fulfills Co<int> { func Get() -> string => \"s\" }\n"
            + "(new Wrong()).Get()";

        var error = await Assert.ThrowsAnyAsync<ToshDiagnosticException>(() => RunAsync(source));
        var diagnostic = Assert.Single(
            error.Diagnostics,
            d => d.Code == "tosh.runtime.contract_member_type_mismatch");

        // Names `int`, not `T` — the reader is told what the contract actually requires.
        Assert.Contains("declares int", $"{diagnostic.Label}", StringComparison.Ordinal);
    }

    // ---- `TOAST-0055` ----

    /// <summary>
    /// A constraint name that resolves to nothing is a typo, not a vocabulary this session
    /// has not heard of yet.
    /// </summary>
    /// <remarks>
    /// It used to be accepted, which did not leave the constraint unchecked — it deleted it,
    /// while the declaration went on reading as though it constrained something. Silent, and
    /// in the safe-looking direction.
    /// </remarks>
    [Fact]
    public async Task An_unrecognised_constraint_name_is_refused()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            "class B<T>(v: T) where T: TotallyMadeUp { prop value: T = $v }\nnew B<string>(\"hi\")"));

        Assert.Contains("TotallyMadeUp", error.Message, StringComparison.Ordinal);
        Assert.Contains("not a known constraint", error.Message, StringComparison.Ordinal);
    }

    /// <summary>And a near miss says what was probably meant.</summary>
    [Fact]
    public async Task A_misspelt_constraint_suggests_the_real_one()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            "class B<T>(v: T) where T: Numric { prop value: T = $v }\nnew B<int>(1)"));

        Assert.Contains("did you mean 'Numeric'?", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Recognition asks about the name, not the argument.
    /// </summary>
    /// <remarks>
    /// The record and interface sites never consulted declared types at all — only the CLR
    /// resolver — so a real interface read as unrecognised there. Refusing on that would have
    /// turned this into a wrong error, which is the failure the conservative rule existed to
    /// avoid.
    /// </remarks>
    [Fact]
    public async Task A_declared_interface_is_recognised_by_a_record()
    {
        // No annotation on the field: `record R<T>(x: T)` is a separate gap — a generic
        // record cannot annotate a field with its own type parameter, which reports
        // "'R.x' uses unknown type annotation 'T'". Not what this measures.
        var source =
            "interface HasGet { func Get() -> int }\n"
            + "class Getter() fulfills HasGet { func Get() -> int => 7 }\n"
            + "record R<T>(x) where T: HasGet\n"
            + "new R<Getter>(new Getter())\n";

        await RunAsync(source);
    }

    /// <summary>And the record site refuses a name that resolves to nothing, as a class does.</summary>
    [Fact]
    public async Task An_unrecognised_constraint_name_is_refused_by_a_record()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            "record R<T>(x) where T: TotallyMadeUp\nnew R<int>(1)"));

        Assert.Contains("TotallyMadeUp", error.Message, StringComparison.Ordinal);
        Assert.Contains("not a known constraint", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Bounds may be separated by `+`, which used to end the clause mid-declaration.</summary>
    /// <remarks>
    /// There is no `Plus` token — `+` is an ordinary shell word — so the clause simply
    /// stopped, the class parser met a bareword where it wanted a body, and the error said
    /// the class needed one. A comma has always worked and still does.
    /// </remarks>
    [Fact]
    public async Task Bounds_can_be_separated_by_a_plus()
        => Assert.Equal("1", await RunAsync(
            "class B<T>(v: T) where T: Numeric + Comparable { prop value: T = $v }\n(new B<int>(1)).value"));

    [Fact]
    public async Task A_plus_separated_bound_is_still_enforced()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            "class B<T>(v: T) where T: Numeric + Comparable { prop value: T = $v }\nnew B<string>(\"x\")"));

        Assert.Contains("to satisfy 'Numeric'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// `new()` is spellable. The registry has carried the entry all along and nothing could
    /// write it: the parens ended the clause, so the declaration failed asking for a body.
    /// </summary>
    [Fact]
    public async Task The_new_constraint_can_be_written_with_parentheses()
        => Assert.Equal("1", await RunAsync(
            "class B<T>(v: T) where T: new() { prop value: T = $v }\n(new B<int>(1)).value"));

    [Theory]
    [InlineData("struct", "new B<int>(1)", "1")]
    [InlineData("class", "new B<string>(\"s\")", "s")]
    [InlineData("unmanaged", "new B<int>(1)", "1")]
    public async Task The_special_constraints_accept_what_they_should(
        string constraint, string construction, string expected)
        => Assert.Equal(expected, await RunAsync(
            $"class B<T>(v: T) where T: {constraint} {{ prop value: T = $v }}\n({construction}).value"));

    [Theory]
    [InlineData("struct", "new B<string>(\"s\")")]
    [InlineData("class", "new B<int>(1)")]
    [InlineData("unmanaged", "new B<string>(\"s\")")]
    public async Task The_special_constraints_refuse_what_they_should(string constraint, string construction)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            $"class B<T>(v: T) where T: {constraint} {{ prop value: T = $v }}\n{construction}"));

        Assert.Contains($"to satisfy '{constraint}'", error.Message, StringComparison.Ordinal);
    }

    // ---- `TOAST-0055`, second half: a declared type as the argument ----

    /// <summary>
    /// A built-in constraint is answered for a type the reader declared.
    /// </summary>
    /// <remarks>
    /// The built-ins are predicates over a CLR <see cref="System.Type"/>, and a ToastScript
    /// class has none of its own — user classes share a backing type — so the bound arrived
    /// null and every check was skipped, on the ground that precise enforcement happens at
    /// the next concrete instantiation. For a declared type that instantiation never comes,
    /// so `where T: Numeric` took any class at all.
    /// </remarks>
    [Theory]
    [InlineData("class Thing { }", "Numeric", "Thing", "new Thing()")]
    [InlineData("enum Col { Red }", "Numeric", "Col", "Col.Red")]
    [InlineData("class Thing { }", "struct", "Thing", "new Thing()")]
    [InlineData("class Thing { }", "Comparable", "Thing", "new Thing()")]
    [InlineData("class Thing { }", "Add", "Thing", "new Thing()")]
    public async Task A_builtin_constraint_refuses_a_declaration_that_cannot_satisfy_it(
        string declaration, string constraint, string argument, string construction)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            $"{declaration}\nclass B<T>(v: T) where T: {constraint} {{ prop value: T = $v }}\n"
            + $"new B<{argument}>({construction})"));

        Assert.Contains($"to satisfy '{constraint}'", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("class Thing { }", "class", "Thing", "new Thing()")]
    [InlineData("enum Col { Red }", "struct", "Col", "Col.Red")]
    [InlineData("enum Col { Red }", "Comparable", "Col", "Col.Red")]
    [InlineData("class Thing { }", "Eq", "Thing", "new Thing()")]
    public async Task A_builtin_constraint_accepts_a_declaration_that_does_satisfy_it(
        string declaration, string constraint, string argument, string construction)
        => await RunAsync(
            $"{declaration}\nclass B<T>(v: T) where T: {constraint} {{ prop value: T = $v }}\n"
            + $"new B<{argument}>({construction})");

    /// <summary>
    /// `where T: Add` is the constraint a math or graphics type actually wants, and it is
    /// answered by whether the class says it can be added — which is what an operator
    /// overload is.
    /// </summary>
    [Fact]
    public async Task Add_is_satisfied_by_overloading_the_operator()
        => await RunAsync(
            "class Money { prop V = 0\n    func +(o) { return 1 } }\n"
            + "class B<T>(v: T) where T: Add { prop value: T = $v }\n"
            + "new B<Money>(new Money())");

    /// <summary>An inherited overload counts: the operator is there to be called.</summary>
    [Fact]
    public async Task Add_is_satisfied_by_an_inherited_operator()
        => await RunAsync(
            "class Base { func +(o) { return 1 } }\nclass Derived extends Base { }\n"
            + "class B<T>(v: T) where T: Add { prop value: T = $v }\n"
            + "new B<Derived>(new Derived())");

    /// <summary>
    /// A CLR type argument still goes through the registry predicate, unchanged.
    /// </summary>
    [Fact]
    public async Task A_clr_argument_is_unaffected()
        => Assert.Equal("1", await RunAsync(
            "class B<T>(v: T) where T: Numeric { prop value: T = $v }\n(new B<int>(1)).value"));

    // ---- `TOAST-0055`, third half: how the type parameter got its type ----

    /// <summary>
    /// A `where` clause means the same thing whether the type argument was inferred or
    /// written out.
    /// </summary>
    /// <remarks>
    /// Only inference reached the check: a constraint was verified on the *first binding* of
    /// a type parameter, and an explicit call-site type argument seeds the binding directly,
    /// so the check was already behind it. `F("a")` was refused while `F<string>("a")` was
    /// accepted — same function, same constraint, opposite answers depending on whether the
    /// caller spelled the type out. A method was unaffected, which is what made it look like
    /// methods were the gap.
    /// </remarks>
    [Theory]
    [InlineData("F<string>(\"a\")")]
    [InlineData("F(\"a\")")]
    public async Task A_function_constraint_is_enforced_however_the_argument_arrives(string call)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            $"func F<T>(x: T) -> T where T: Numeric {{ return $x }}\n{call}"));

        Assert.Contains("to satisfy 'Numeric'", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The label blames the type argument, since that is what the caller wrote.</summary>
    /// <remarks>
    /// On the diagnostic rather than the message: `Message` carries the title only.
    /// </remarks>
    [Fact]
    public async Task An_explicit_type_argument_is_what_the_diagnostic_blames()
    {
        var error = await Assert.ThrowsAsync<ToshDiagnosticException>(() => RunAsync(
            "func F<T>(x: T) -> T where T: Numeric { return $x }\nF<string>(\"a\")"));

        Assert.Contains(
            error.Diagnostics,
            diagnostic => diagnostic.Label is not null &&
                          diagnostic.Label.Contains("type argument 'string'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("F<int>(1)")]
    [InlineData("F(1)")]
    public async Task A_satisfied_function_constraint_still_runs(string call)
        => Assert.Equal("1", await RunAsync(
            $"func F<T>(x: T) -> T where T: Numeric {{ return $x }}\n{call}"));

    /// <summary>An unrecognised name is refused on this path too.</summary>
    [Fact]
    public async Task An_unrecognised_constraint_is_refused_on_an_explicit_type_argument()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            "func F<T>(x: T) -> T where T: TotallyMadeUp { return $x }\nF<int>(1)"));

        Assert.Contains("not a known constraint", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An unconstrained type parameter is untouched by any of this.</summary>
    [Fact]
    public async Task An_unconstrained_function_takes_whatever_it_is_given()
        => Assert.Equal("a", await RunAsync(
            "func F<T>(x: T) -> T { return $x }\nF<string>(\"a\")"));

    /// <summary>A method's own type parameter was already enforced, and stays so.</summary>
    [Theory]
    [InlineData("F<string>(\"a\")")]
    [InlineData("F(\"a\")")]
    public async Task A_method_type_parameter_constraint_is_still_enforced(string call)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(
            "class C {\n    func F<T>(x: T) -> T where T: Numeric { return $x }\n}\n"
            + $"(new C()).{call}"));

        Assert.Contains("to satisfy 'Numeric'", error.Message, StringComparison.Ordinal);
    }
}
