using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Compiler.IR;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Tests for <see cref="TypeChecker"/> — the post-bind pass that
/// validates assignment / return compatibility against the typed
/// annotations resolved by <see cref="TypeNameResolver"/>.
/// </summary>
public sealed class TypeCheckerTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public TypeCheckerTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private IReadOnlyList<ToshDiagnostic> Check(string source)
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse(source, "<check-test>");
        var unit = Lowerer.Lower(parse, _runtime.Commands);
        return TypeChecker.Check(unit);
    }

    [Fact]
    public void No_diagnostics_for_well_typed_var_decl()
    {
        var diags = Check("var x: int = 42");
        Assert.Empty(diags);
    }

    [Fact]
    public void No_diagnostics_for_dynamic_var_decl()
    {
        var diags = Check("var x = 42");
        Assert.Empty(diags);
    }

    [Fact]
    public void Reports_mismatch_assigning_string_to_int_var()
    {
        var diags = Check("var x: int = \"hello\"");
        Assert.Single(diags);
        Assert.Equal("tosh.type.mismatch", diags[0].Code);
        Assert.Equal(ToshDiagnosticSeverity.Warning, diags[0].Severity);
        Assert.Equal(ToshDiagnosticCategory.Type, diags[0].Category);
    }

    [Fact]
    public void Allows_numeric_widening_int_to_long()
    {
        var diags = Check("var x: long = 42");
        Assert.Empty(diags);
    }

    [Fact]
    public void Disallows_numeric_narrowing_double_to_int()
    {
        var diags = Check("var x: int = 1.5");
        Assert.Single(diags);
        Assert.Equal("tosh.type.mismatch", diags[0].Code);
    }

    [Fact]
    public void No_diagnostics_when_value_type_unknown()
    {
        // ls returns dynamic — checker stays silent.
        var diags = Check("var x: int = (ls)");
        Assert.Empty(diags);
    }

    [Fact]
    public void Reports_mismatched_return_type()
    {
        var diags = Check("""
            func f() -> int {
                return "hello"
            }
            """);
        Assert.Single(diags);
        Assert.Equal("tosh.type.mismatch", diags[0].Code);
        Assert.Contains("return", diags[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_diagnostic_for_correct_return_type()
    {
        var diags = Check("""
            func f() -> int {
                return 42
            }
            """);
        Assert.Empty(diags);
    }

    [Fact]
    public void No_diagnostic_for_dynamic_return_type()
    {
        var diags = Check("""
            func f() {
                return "anything"
            }
            """);
        Assert.Empty(diags);
    }

    [Fact]
    public void PromoteSeverity_flips_warning_to_error()
    {
        var diags = Check("var x: int = \"hello\"");
        var promoted = TypeChecker.PromoteSeverity(diags[0], ToshDiagnosticSeverity.Error);
        Assert.Equal(ToshDiagnosticSeverity.Error, promoted.Severity);
        Assert.Equal(diags[0].Code, promoted.Code);
    }

    [Fact]
    public void Reports_arity_too_few_args()
    {
        var diags = Check("""
            func add(a: int, b: int) -> int { return $a + $b }
            add 1
            """);
        Assert.Single(diags);
        Assert.Equal("tosh.type.arity", diags[0].Code);
        Assert.Contains("2", diags[0].Title);
    }

    [Fact]
    public void Reports_arity_too_many_args()
    {
        var diags = Check("""
            func add(a: int, b: int) -> int { return $a + $b }
            add 1 2 3
            """);
        Assert.Single(diags);
        Assert.Equal("tosh.type.arity", diags[0].Code);
    }

    [Fact]
    public void No_arity_diagnostic_for_correct_call()
    {
        var diags = Check("""
            func add(a: int, b: int) -> int { return $a + $b }
            add 1 2
            """);
        Assert.Empty(diags);
    }

    [Fact]
    public void Quantity_string_boundaries_and_unary_operators_are_known_conversions()
    {
        var diags = Check("""
            func in_feet(distance: length) -> length {
                return (-$distance as `ft)
            }
            in_feet "2mi"
            """);

        Assert.DoesNotContain(diags, diagnostic => diagnostic.Code is
            "tosh.type.mismatch" or "tosh.type.operator");
    }

    [Fact]
    public void Reports_argument_type_mismatch()
    {
        var diags = Check("""
            func add(a: int, b: int) -> int { return $a + $b }
            add 1 "hello"
            """);
        Assert.NotEmpty(diags);
        Assert.Equal("tosh.type.mismatch", diags[0].Code);
        Assert.Contains("Argument", diags[0].Title);
    }

    [Fact]
    public void No_diagnostic_for_unknown_function_call()
    {
        // External commands and unresolved callees stay silent.
        var diags = Check("ls -la");
        Assert.Empty(diags);
    }

    [Fact]
    public void Allows_truthy_if_and_while_conditions()
    {
        var diags = Check("if 42 { echo 1 }; while \"nope\" { break }");
        Assert.Empty(diags);
    }

    [Theory]
    [InlineData("var value = (1 and \"yes\")")]
    [InlineData("var value = (0 or \"yes\")")]
    [InlineData("var value = (not \"\")")]
    public void Allows_truthy_logical_and_not_operands(string source)
    {
        var diags = Check(source);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.type.operator");
    }

    [Fact]
    public void Reports_operator_incompatible_operands()
    {
        var diags = Check("var x = (true - 1)");
        Assert.Contains(diags, d => d.Code == "tosh.type.operator");
    }

    [Fact]
    public void Reports_missing_member_on_concrete_type()
    {
        var diags = Check("var s: string = \"abc\"; echo $s.NotARealMember");
        Assert.Contains(diags, d => d.Code == "tosh.type.member_not_found");
    }

    [Fact]
    public void Reports_method_overload_mismatch_on_concrete_type()
    {
        var diags = Check("var s: string = \"abc\"; echo ($s.Substring(\"x\"))");
        Assert.Contains(diags, d => d.Code == "tosh.type.mismatch");
    }

    [Fact]
    public void Reports_index_type_mismatch()
    {
        var diags = Check("var s: string = \"abc\"; echo $s[true]");
        Assert.Contains(diags, d => d.Code == "tosh.type.index");
    }

    [Fact]
    public void Reports_builtin_command_arity_from_metadata()
    {
        var diags = Check("first 1 2");
        Assert.Contains(diags, d => d.Code == "tosh.type.command_arity");
    }

    [Theory]
    [InlineData("mkdir -p foo")]
    [InlineData("cut -d \":\" -f 2")]
    [InlineData("sort -d Count")]
    [InlineData("ping -c 1 localhost")]
    public void Does_not_report_builtin_command_arity_for_shell_options(string source)
    {
        var diags = Check(source);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.type.command_arity");
    }

    // ── CheckCompileAnnotations ─────────────────────────────────

    private IReadOnlyList<ToshDiagnostic> CheckCompile(string source, bool allowDynamic)
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse(source, "<compile-test>");
        var unit = Lowerer.Lower(parse, _runtime.Commands);
        return TypeChecker.CheckCompileAnnotations(unit, allowDynamic);
    }

    [Fact]
    public void Compile_audit_flags_missing_return_type()
    {
        var diags = CheckCompile(
            "func f(a: int) { return $a }", allowDynamic: false);
        Assert.Single(diags);
        Assert.Equal("tosh.compile.missing_type_annotation", diags[0].Code);
        Assert.Equal(ToshDiagnosticSeverity.Error, diags[0].Severity);
    }

    [Fact]
    public void Compile_audit_flags_missing_param_type()
    {
        var diags = CheckCompile(
            "func f(a) -> int { return $a }", allowDynamic: false);
        Assert.Single(diags);
        Assert.Equal("tosh.compile.missing_type_annotation", diags[0].Code);
        Assert.Contains("a", diags[0].Title);
    }

    [Fact]
    public void Compile_audit_passes_explicit_dynamic_param()
    {
        var diags = CheckCompile(
            "func f(a: dynamic) -> dynamic { return $a }", allowDynamic: false);
        Assert.Empty(diags);
    }

    [Fact]
    public void Compile_audit_flags_implicit_dynamic_var()
    {
        var diags = CheckCompile("var x = (ls)", allowDynamic: false);
        Assert.Single(diags);
        Assert.Equal("tosh.compile.implicit_dynamic", diags[0].Code);
    }

    [Fact]
    public void Compile_audit_suppresses_implicit_dynamic_when_flag_set()
    {
        var diags = CheckCompile("var x = (ls)", allowDynamic: true);
        Assert.Empty(diags);
    }

    [Fact]
    public void Compile_audit_passes_explicit_dynamic_var_annotation()
    {
        // `var f: dynamic = ...` is an explicit opt-out, equivalent
        // to the parameter-level `: dynamic` case above. The compile
        // audit must not flag it as implicit dynamic.
        var diags = CheckCompile("var f: dynamic = (ls)", allowDynamic: false);
        Assert.Empty(diags);
    }

    [Fact]
    public void Compile_audit_passes_fully_typed_program()
    {
        var diags = CheckCompile("""
            func add(a: int, b: int) -> int { return $a + $b }
            var result: int = (add 1 2)
            """, allowDynamic: false);
        Assert.Empty(diags);
    }

    [Fact]
    public void Compile_audit_allows_var_new_user_type_without_dynamic_opt_in()
    {
        var diags = CheckCompile("""
            class Point(x, y) { prop X = $x
            prop Y = $y }
            var p = new Point(1, 2)
            """, allowDynamic: false);
        Assert.Empty(diags);
    }

    // ---------------------------------------------------------------
    // Phase 5.11: type-alias transparency in IsAssignable.
    //
    // A `type` declaration projects to a `RefinementType` wrapper
    // around the resolved base. The type checker must unwrap that
    // wrapper so assignment compatibility is judged against the base,
    // not the alias name. These tests lock that behavior in for both
    // refinement-bearing and plain aliases, including parameterized
    // bases.
    // ---------------------------------------------------------------

    [Fact]
    public void Plain_alias_to_primitive_accepts_base_value()
    {
        var diags = Check("type Id = int\nvar a: Id = 42");
        Assert.Empty(diags);
    }

    [Fact]
    public void Plain_alias_to_parameterized_list_accepts_list_literal()
    {
        var diags = Check("type IntList = list<int>\nvar d: IntList = [1,2,3]");
        Assert.Empty(diags);
    }

    [Fact]
    public void Refinement_alias_accepts_base_value_statically()
    {
        // The refinement clause is enforced dynamically; the static
        // checker should only see `int -> int` and stay silent.
        var diags = Check("type Positive = int where _ > 0\nvar b: Positive = 5");
        Assert.Empty(diags);
    }

    [Fact]
    public void Generic_refinement_alias_accepts_concrete_value()
    {
        var diags = Check("type Bounded<T> = T where _ > 0\nvar c: Bounded<int> = 7");
        Assert.Empty(diags);
    }

    [Fact]
    public void Generic_alias_forwarding_accepts_constructed_list_literal()
    {
        var diags = Check("type MyList<T> = list<T>\nvar e: MyList<string> = [\"a\",\"b\"]");
        Assert.Empty(diags);
    }

    [Fact]
    public void Plain_alias_still_reports_truly_incompatible_assignment()
    {
        // Alias transparency must not silence real mismatches: `Id`
        // unwraps to `int`, and `"hello"` is a string.
        var diags = Check("type Id = int\nvar a: Id = \"hello\"");
        Assert.Single(diags);
        Assert.Equal("tosh.type.mismatch", diags[0].Code);
    }

    // ---------------------------------------------------------------
    // Phase 5 followup: precise generic-alias substitution.
    //
    // When a generic alias is used at a concrete type-arg site (e.g.
    // `MyList<int>`), the resolver substitutes the type parameters
    // structurally rather than erasing them to `Dynamic`. The
    // resulting `RefinementType` still wraps the alias name for
    // diagnostics, but the `Base` is precise.
    // ---------------------------------------------------------------

    [Fact]
    public void Generic_alias_substitution_produces_precise_base_for_int()
    {
        var registry = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        var alias = new Tosh.Language.Parsing.TypeAliasStatementSyntax(
            Name: "MyList",
            TypeParameters: new[] { "T" },
            BaseTypeName: "list<T>",
            Refinement: null,
            Modifier: DeclarationModifier.Default,
            Span: default);
        registry["MyList"] = new RefinementType(BoundType.Dynamic, "MyList", alias);

        var resolver = new TypeNameResolver(registry);
        var resolved = resolver.Resolve("MyList<int>");

        var refType = Assert.IsType<RefinementType>(resolved);
        Assert.Equal("MyList<Int32>", refType.DisplayName);
        var listType = Assert.IsType<ListType>(refType.Base);
        Assert.Equal(typeof(int), listType.Element.ClrType);
    }

    [Fact]
    public void Generic_alias_substitution_produces_precise_base_for_string()
    {
        var registry = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        var alias = new Tosh.Language.Parsing.TypeAliasStatementSyntax(
            Name: "MyList",
            TypeParameters: new[] { "T" },
            BaseTypeName: "list<T>",
            Refinement: null,
            Modifier: DeclarationModifier.Default,
            Span: default);
        registry["MyList"] = new RefinementType(BoundType.Dynamic, "MyList", alias);

        var resolver = new TypeNameResolver(registry);
        var resolved = resolver.Resolve("MyList<string>");

        var refType = Assert.IsType<RefinementType>(resolved);
        var listType = Assert.IsType<ListType>(refType.Base);
        Assert.Equal(typeof(string), listType.Element.ClrType);
    }

    [Fact]
    public void Generic_alias_substitution_handles_two_parameter_alias()
    {
        var registry = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        var alias = new Tosh.Language.Parsing.TypeAliasStatementSyntax(
            Name: "Pair",
            TypeParameters: new[] { "A", "B" },
            BaseTypeName: "tuple<A,B>",
            Refinement: null,
            Modifier: DeclarationModifier.Default,
            Span: default);
        registry["Pair"] = new RefinementType(BoundType.Dynamic, "Pair", alias);

        var resolver = new TypeNameResolver(registry);
        var resolved = resolver.Resolve("Pair<int,string>");

        var refType = Assert.IsType<RefinementType>(resolved);
        Assert.Equal("Pair<Int32, String>", refType.DisplayName);
        var tup = Assert.IsType<TupleType>(refType.Base);
        Assert.Equal(2, tup.Elements.Count);
        Assert.Equal(typeof(int), tup.Elements[0].ClrType);
        Assert.Equal(typeof(string), tup.Elements[1].ClrType);
    }

    [Fact]
    public void Generic_alias_arity_mismatch_emits_diagnostic()
    {
        var registry = new Dictionary<string, BoundType>(StringComparer.Ordinal);
        var alias = new Tosh.Language.Parsing.TypeAliasStatementSyntax(
            Name: "Pair",
            TypeParameters: new[] { "A", "B" },
            BaseTypeName: "tuple<A,B>",
            Refinement: null,
            Modifier: DeclarationModifier.Default,
            Span: default);
        registry["Pair"] = new RefinementType(BoundType.Dynamic, "Pair", alias);

        var diagnostics = new List<string>();
        var resolver = new TypeNameResolver(registry, onDiagnostic: diagnostics.Add);
        resolver.Resolve("Pair<int>");

        Assert.Contains(diagnostics, d => d.Contains("expects 2 type argument", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------
    // Phase 6.12: type-parameter variance on generic interfaces.
    //
    // Interfaces may declare each type parameter as `out T`
    // (covariant), `in T` (contravariant), or omit the marker
    // (invariant — default). The static type-checker honors those
    // markers when judging assignability between two
    // `GenericInstanceType`s wrapping the same interface template.
    // Variance only applies to interface templates; classes,
    // records, and structs remain invariant regardless of any
    // accidental annotations.
    // ---------------------------------------------------------------

    [Fact]
    public void Covariant_interface_accepts_widening_type_arg()
    {
        // Numeric widening is one-way: int → long. With `out T`
        // the covariant slot accepts the narrower IBox<int>.
        var src = """
            interface IBox<out T> { func get() -> T }
            func consume(b: IBox<int>) {
                var a: IBox<long> = $b
            }
            """;
        var diags = Check(src);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.type.mismatch");
    }

    [Fact]
    public void Invariant_interface_rejects_widening_type_arg()
    {
        // Without `out`, the slot is invariant and bidirectional
        // assignability is required. `long` is not assignable to
        // `int` (narrowing), so the invariant pair must fail.
        var src = """
            interface IBox<T> { func get() -> T }
            func consume(b: IBox<int>) {
                var a: IBox<long> = $b
            }
            """;
        var diags = Check(src);
        Assert.Contains(diags, d => d.Code == "tosh.type.mismatch");
    }

    [Fact]
    public void Contravariant_interface_accepts_widening_in_reverse()
    {
        // `in T` flips the direction. `IBox<long>` fits into an
        // `IBox<int>` slot because the LHS slot's int is
        // assignable to the RHS value's long (the contravariant
        // direction).
        var src = """
            interface IBox<in T> { func put(x: T) }
            func consume(b: IBox<long>) {
                var a: IBox<int> = $b
            }
            """;
        var diags = Check(src);
        Assert.DoesNotContain(diags, d => d.Code == "tosh.type.mismatch");
    }

    [Fact]
    public void Covariant_interface_rejects_narrowing_type_arg()
    {
        // `out T` only allows widening: IBox<long> assigned to
        // IBox<int> slot must still fail (narrowing).
        var src = """
            interface IBox<out T> { func get() -> T }
            func consume(b: IBox<long>) {
                var a: IBox<int> = $b
            }
            """;
        var diags = Check(src);
        Assert.Contains(diags, d => d.Code == "tosh.type.mismatch");
    }
}
