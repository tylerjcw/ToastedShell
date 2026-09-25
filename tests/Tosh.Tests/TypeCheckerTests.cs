using Tosh.Language;
using Tosh.Language.Binding;
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
        var engine = new ToshEngine(_runtime.Language);
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

    /// <summary>
    /// A value whose type genuinely cannot be known is not second-guessed.
    /// </summary>
    /// <remarks>
    /// This used `(ls)` as the unknowable value, which stopped being unknowable in
    /// `TOAST-0034`: `ls` declares `[CommandOutput]`, and parentheses now keep the type of
    /// what they group instead of erasing it. A function with no declared return is
    /// unknowable for a reason that will not quietly go away.
    /// </remarks>
    [Fact]
    public void No_diagnostics_when_value_type_unknown()
    {
        var diags = Check("func g() => 7\nvar x: int = (g)");
        Assert.Empty(diags);
    }

    /// <summary>
    /// And a value whose type *is* known is checked, even through parentheses.
    /// </summary>
    /// <remarks>
    /// The other half of the case above, and the reason it had to change. `ls` produces a
    /// stream of entries; assigning one to an `int` is a mismatch, and saying so is the
    /// improvement — silence here used to be a limitation wearing the name of a rule.
    /// </remarks>
    [Fact]
    public void A_parenthesised_value_is_still_checked()
    {
        var diags = Check("var x: int = (ls)");
        Assert.Single(diags);
        Assert.Equal("tosh.type.mismatch", diags[0].Code);
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

    /// <summary>
    /// `TOAST-0101`. A pipeline supplies values through <c>$tosh.Function.Input</c>, never
    /// through the parameter list — that is the decision, not an omission. So the arity
    /// warning still fires, and says where the pipeline actually arrives.
    /// </summary>
    /// <remarks>
    /// "Received 0 arguments" is true of the parenthesised list and false of the invocation
    /// the author wrote, and on its own it points away from the answer. A builtin decrements
    /// its required count for a piped subject instead, because a builtin genuinely takes one
    /// from the pipe; these two behaving differently at the definition while reading
    /// identically at the call site is what the item was filed about.
    /// </remarks>
    [Fact]
    public void A_required_parameter_with_a_pipeline_is_told_where_the_pipeline_arrives()
    {
        var diags = Check("""
            func mine(items) { return 1 }
            [1, 2, 3] | mine
            """);

        var arity = Assert.Single(diags, d => d.Code == "tosh.type.arity");
        Assert.Contains("$tosh.Function.Input", arity.Help ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Without a pipeline there is nothing to explain, so nothing is said.</summary>
    [Fact]
    public void A_required_parameter_without_a_pipeline_is_not_told_about_the_pipeline()
    {
        var diags = Check("""
            func mine(items) { return 1 }
            mine
            """);

        var arity = Assert.Single(diags, d => d.Code == "tosh.type.arity");
        Assert.DoesNotContain("Function.Input", arity.Help ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two shapes that consume a pipeline correctly warn about nothing: a function that
    /// declares no parameter, and one whose parameter is defaulted.
    /// </summary>
    [Theory]
    [InlineData("func mine() { return 1 }\n[1, 2, 3] | mine")]
    [InlineData("func mine(items = []) { return 1 }\n[1, 2, 3] | mine")]
    public void Consuming_a_pipeline_correctly_warns_about_nothing(string source)
        => Assert.DoesNotContain(Check(source), d => d.Code == "tosh.type.arity");

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

}
