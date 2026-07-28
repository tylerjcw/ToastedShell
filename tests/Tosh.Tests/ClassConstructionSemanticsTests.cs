using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

public sealed class ClassConstructionSemanticsTests
{
    [Fact]
    public async Task Construction_binds_each_layer_locals_and_runs_base_to_leaf_once()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var trace = ""
            class Root {
                prop RootValue
                Root(root) {
                    $trace += "R"
                    $this.RootValue = $root
                }
            }
            class Middle extends Root($middle + 1) {
                prop MiddleValue = $middle
                Middle(middle) { $trace += "M" }
            }
            class Leaf extends Middle($leaf + 1) {
                prop LeafValue = $leaf
                Leaf(leaf) { $trace += "L" }
            }
            var value = new Leaf(40)
            echo $trace
            echo $value.RootValue
            echo $value.MiddleValue
            echo $value.LeafValue
            """);

        Assert.Equal("RML", results[0]);
        Assert.Equal([42, 41, 40], results.Skip(1).Select(Convert.ToInt32).ToArray());
    }

    [Fact]
    public async Task Leading_super_call_is_lifted_before_derived_initialization()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            var trace = ""
            class Base {
                prop Value
                Base(value) {
                    $trace += "B"
                    $this.Value = $value
                }
            }
            class Child extends Base {
                prop Snapshot = $this.Value
                Child(value) {
                    $super($value)
                    $trace += "C"
                }
            }
            var child = new Child(9)
            echo $trace
            echo $child.Snapshot
            """);

        Assert.Equal("BC", results[0]);
        Assert.Equal(9, Convert.ToInt32(results[1]));
    }

    [Theory]
    [InlineData("extends Base")]
    [InlineData("extends Base()")]
    public async Task Zero_argument_base_constructor_is_invoked_implicitly(string extendsClause)
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            $$"""
            var trace = ""
            class Base { Base() { $trace += "B" } }
            class Child {{extendsClause}} { Child() { $trace += "C" } }
            var child = new Child()
            echo $trace
            """);

        Assert.Equal("BC", Assert.Single(results));
    }

    [Fact]
    public async Task Header_and_super_initializers_are_rejected_before_side_effects()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            var calls = 0
            class Base { Base(value) { $calls += 1 } }
            class Child extends Base($value) {
                Child(value) { $super($value) }
            }
            """);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("var child = new Child(1)"));

        Assert.Contains(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == "tosh.runtime.duplicate_base_constructor_initializer");
        Assert.Equal(
            0,
            Convert.ToInt32(Assert.Single(await engine.ExecuteToListAsync("echo $calls"))));
    }

    [Fact]
    public async Task Non_leading_super_initializer_is_rejected_before_side_effects()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            var trace = ""
            class Base { Base(value) { $trace += "B" } }
            class Child extends Base {
                Child(value) {
                    $trace += "C"
                    $super($value)
                }
            }
            """);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("var child = new Child(1)"));

        Assert.Contains(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == "tosh.runtime.super_initializer_must_be_first");
        Assert.Equal(
            string.Empty,
            Assert.Single(await engine.ExecuteToListAsync("echo $trace")));
    }

    [Fact]
    public async Task Nested_super_call_cannot_reinitialize_a_completed_base_layer()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            var calls = 0
            class NestedBase { NestedBase() { $calls += 1 } }
            class NestedChild extends NestedBase {
                NestedChild() {
                    if (true) { $super() }
                }
            }
            """);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("var child = new NestedChild()"));

        Assert.Contains(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == "tosh.runtime.base_constructor_already_initialized");
        Assert.Equal(
            1,
            Convert.ToInt32(Assert.Single(await engine.ExecuteToListAsync("echo $calls"))));
    }

    [Fact]
    public async Task Required_base_arguments_without_initializer_report_targeted_diagnostic()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());
        await engine.ExecuteToListAsync(
            """
            class Base { Base(value) { } }
            class Child extends Base { }
            """);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(
            () => engine.ExecuteToListAsync("var child = new Child()"));

        Assert.Contains(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == "tosh.runtime.missing_base_constructor_initializer");
    }

    [Fact]
    public async Task Generic_root_binding_survives_non_generic_intermediate_class()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            class Root<T>(value: T) { prop Value: T = $value }
            class Middle extends Root<int>($value) { Middle(value) { } }
            class Leaf extends Middle($value) { Leaf(value) { } }
            var leaf = new Leaf(42)
            echo (type-of $leaf.Value | get Name)
            echo $leaf.Value
            """);

        Assert.Equal("Int32", results[0]);
        Assert.Equal(42, Convert.ToInt32(results[1]));
    }

    [Fact]
    public async Task Clr_base_is_initialized_before_derived_properties()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault());

        var results = await engine.ExecuteToListAsync(
            """
            class HeaderUri(url) extends System.Uri($url) {
                prop InitialHost = $this.Host
            }
            class BodyUri extends System.Uri {
                prop InitialHost = $this.Host
                BodyUri(url) { $super($url) }
            }
            var header = new HeaderUri("https://header.example/path")
            var body = new BodyUri("https://body.example/path")
            echo $header.InitialHost
            echo $body.InitialHost
            """);

        Assert.Equal(["header.example", "body.example"], results);
    }
}
