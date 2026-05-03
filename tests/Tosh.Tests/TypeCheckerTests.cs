using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Language.Binding.BoundNodes;
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
    public void Compile_audit_passes_fully_typed_program()
    {
        var diags = CheckCompile("""
            func add(a: int, b: int) -> int { return $a + $b }
            var result: int = (add 1 2)
            """, allowDynamic: false);
        Assert.Empty(diags);
    }
}
