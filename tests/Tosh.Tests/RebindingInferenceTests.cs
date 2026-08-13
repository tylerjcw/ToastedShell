using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// An inferred variable's type follows its assignments.
///
/// A <c>BoundSymbol</c> records the type inferred where the variable was
/// declared and is shared by identity across every reference, so it cannot
/// itself carry a value that changes. References are lowered in source order,
/// though, so a per-scope record of reassignments consulted during lowering
/// gives the flow-sensitive answer — and a use *before* the rebinding still sees
/// the declaration's type. `TS-P2-87`.
/// </summary>
public class RebindingInferenceTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public RebindingInferenceTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    private IReadOnlyList<ToshDiagnostic> Check(string source)
    {
        var engine = new ToshEngine(_runtime);
        var unit = Lowerer.Lower(engine.Parse(source, "<rebinding-test>"), _runtime.Commands);
        return TypeChecker.Check(unit);
    }

    private static async Task<string> RunAsync(string source)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        var results = await engine.ExecuteToListAsync(source);
        return string.Join(",", results.Select(value => value?.ToString() ?? "null"));
    }

    /// <summary>
    /// The reported case, from `examples/showcase2.tosh`: an int rebound to a
    /// string and then to an array. The member check used to be made against
    /// `Int32`, the type from the declaration three assignments earlier.
    /// </summary>
    [Fact]
    public void A_member_check_follows_the_latest_assignment()
    {
        var diagnostics = Check(
            """
            var x = 10
            $x = "hello"
            $x = [1, 2, 3]
            $x.Length
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "tosh.type.member_not_found");
    }

    [Fact]
    public async Task The_rebound_value_is_still_produced()
    {
        var output = await RunAsync(
            """
            var x = 10
            $x = [1, 2, 3]
            $x.Length
            """);

        Assert.Equal("3", output);
    }

    /// <summary>
    /// A use before the rebinding must keep the earlier type — flow sensitivity
    /// in the wrong direction would be no better than none.
    /// </summary>
    [Fact]
    public async Task A_use_before_the_rebinding_sees_the_earlier_type()
    {
        var output = await RunAsync(
            """
            var x = "hello"
            $x.Length
            $x = [1, 2, 3]
            $x.Length
            """);

        Assert.Equal("5,3", output);
    }

    /// <summary>
    /// An annotated variable does not adopt the assigned type: the annotation is
    /// the contract, and an assignment that disagrees is an error to report
    /// rather than a new type to infer.
    /// </summary>
    [Fact]
    public async Task An_annotated_variable_keeps_its_declared_type()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync(
                """
                var y: int = 10
                $y = "nope"
                """));
    }

    /// <summary>
    /// A rebinding inside a branch says nothing certain about the type after it —
    /// the branch may not have run. The variable becomes dynamic rather than
    /// adopting a type that only one path produces, which suppresses the member
    /// check instead of making a wrong one.
    /// </summary>
    [Fact]
    public void A_rebinding_inside_a_branch_does_not_leak_a_wrong_type()
    {
        var diagnostics = Check(
            """
            var z = 1
            if (true) { $z = "text" }
            $z.Length
            """);

        Assert.DoesNotContain(diagnostics, d => d.Code == "tosh.type.member_not_found");
    }
}
