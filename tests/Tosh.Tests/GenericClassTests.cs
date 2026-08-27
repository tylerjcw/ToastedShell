using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// Tests covering user-defined generic classes (`class Box&lt;T&gt;`) in the
/// REPL/interpreter path. End-to-end compiled tests live alongside the
/// interpreter cases — when behaviour diverges it is a regression.
/// </summary>
public sealed class GenericClassTests
{
    [Fact]
    public async Task Generic_class_substitutes_type_parameter_at_property_storage()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            class Box<T>(initial) {
                prop value: T = $initial
            }

            var bi = new Box<int>(42)
            echo (type-of $bi.value | get Name)
            echo $bi.value

            var bs = new Box<string>("hello")
            echo (type-of $bs.value | get Name)
            echo $bs.value
            """);

        Assert.Equal("Int32", results[0]);
        Assert.Equal(42, results[1]);
        Assert.Equal("String", results[2]);
        Assert.Equal("hello", results[3]);
    }

    [Fact]
    public async Task Generic_class_strictly_rejects_constructor_argument_with_mismatched_type()
    {
        var engine = ShellEngine.CreateFullShell();

        // Assigning an integer to a Box<string> must reject under strict
        // no-coercion semantics for type-parameter bindings.
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync(
                """
                class Box<T>(initial) {
                    prop value: T = $initial
                }

                var b = new Box<string>(42)
                """));

        Assert.Contains(ex.Diagnostics, d =>
            d.Code == "tosh.runtime.annotation_conversion_failed");
    }

    [Fact]
    public async Task Generic_class_rejects_constructor_argument_that_cannot_be_converted()
    {
        var engine = ShellEngine.CreateFullShell();

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync(
                """
                class Box<T>(value: T) {
                    prop value: T = $value
                }

                var b = new Box<int>("not a number")
                """));

        Assert.Contains(ex.Diagnostics, d =>
            d.Code == "tosh.runtime.annotation_conversion_failed");
    }

    [Fact]
    public async Task Generic_class_rejects_wrong_arity_at_instantiation()
    {
        var engine = ShellEngine.CreateFullShell();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await engine.ExecuteToListAsync(
                """
                class Pair<A, B>(a, b) { prop a: A = $a; prop b: B = $b }
                var p = new Pair<int>(1, 2)
                """));

        Assert.Contains("type argument", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Generic_class_rejects_empty_angle_bracket_list()
    {
        var engine = ShellEngine.CreateFullShell();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await engine.ExecuteToListAsync(
                """
                class Box<T>(initial) { prop value: T = $initial }
                var b = new Box<>(1)
                """));

        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Generic_class_requires_type_arguments_when_class_is_generic()
    {
        var engine = ShellEngine.CreateFullShell();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await engine.ExecuteToListAsync(
                """
                class Box<T>(initial) { prop value: T = $initial }
                var b = new Box(1)
                """));

        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Generic_inheritance_propagates_concrete_clr_type_through_base_binding()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            class Base<A>(value) {
                prop value: A = $value
            }

            class IntChild extends Base<int>($value) {
                IntChild(value) { }
            }

            var c = new IntChild(99)
            echo (type-of $c.value | get Name)
            echo $c.value
            """);

        Assert.Equal("Int32", results[0]);
        Assert.Equal(99, results[1]);
    }

    [Fact]
    public async Task Generic_inheritance_with_extends_arity_mismatch_throws()
    {
        var engine = ShellEngine.CreateFullShell();

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await engine.ExecuteToListAsync(
                """
                class Base<A, B>(a, b) {
                    prop a: A = $a
                    prop b: B = $b
                }
                class Bad extends Base<int>(1, 2) {
                    Bad() { }
                }
                var x = new Bad()
                """));

        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Generic_method_substitutes_return_type_per_instance()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            class Box<T>(initial) {
                prop value: T = $initial
                func unwrap() -> T { return $this.value }
            }

            var bi = new Box<int>(7)
            echo (type-of ($bi.unwrap()) | get Name)

            var bs = new Box<string>("hi")
            echo (type-of ($bs.unwrap()) | get Name)
            """);

        Assert.Equal("Int32", results[0]);
        Assert.Equal("String", results[1]);
    }

    [Fact]
    public async Task Generic_method_parameter_type_is_strictly_enforced_at_call_time()
    {
        var engine = ShellEngine.CreateFullShell();

        // Box<string>.set takes a `T` parameter. With strict no-coercion
        // semantics for type-parameter-bound parameters, passing an int
        // (42) where T=string is bound must reject rather than silently
        // stringifying.
        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync(
                """
                class Box<T>(initial) {
                    prop value: T = $initial
                    func set(v: T) { $this.value = $v }
                }

                var bs = new Box<string>("seed")
                $bs.set(42)
                """));

        Assert.Contains("annotation_conversion_failed", ex.Diagnostics[0].Code);

        // Sanity: passing the right type works.
        var results = await engine.ExecuteToListAsync(
            """
            class Box<T>(initial) {
                prop value: T = $initial
                func set(v: T) { $this.value = $v }
            }

            var bs = new Box<string>("seed")
            $bs.set("hi")
            echo $bs.value
            """);

        Assert.Equal("hi", results[0]);
    }

    [Fact]
    public async Task Generic_class_method_accepts_same_generic_class_parameter()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            class Point<T>(x: T) {
                prop x: T = $x
                func otherX(other: Point<T>) { return $other.x }
            }

            var a = new Point<int>(1)
            var b = new Point<int>(2)
            echo ($a.otherX($b))
            """);

        Assert.Equal(2, results[0]);
    }

    [Fact]
    public async Task Generic_class_user_interface_constraint_accepts_implementing_class()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            interface IShape { func area() }
            class Circle fulfills IShape {
                func area() { return 3 }
            }
            class Holder<T>(item: T) where T: IShape {
                prop item: T = $item
            }

            var h = new Holder<Circle>(new Circle())
            echo ($h.item.area())
            """);

        Assert.Equal(3, results[0]);
    }

    [Fact]
    public async Task Generic_class_user_interface_constraint_rejects_non_implementing_class()
    {
        var engine = ShellEngine.CreateFullShell();

        var ex = await Assert.ThrowsAsync<ToshDiagnosticException>(async () =>
            await engine.ExecuteToListAsync(
                """
                interface IShape { func area() }
                class Square { prop side: int = 1 }
                class Holder<T>(item: T) where T: IShape {
                    prop item: T = $item
                }

                var h = new Holder<Square>(new Square())
                """));

        Assert.Contains(ex.Diagnostics, d =>
            d.Title.Contains("requires type parameter")
            && d.Title.Contains("'IShape'")
            && d.Title.Contains("'Square'"));
    }

    [Fact]
    public async Task Generic_class_user_interface_constraint_accepts_inherited_interface()
    {
        var engine = ShellEngine.CreateFullShell();

        var results = await engine.ExecuteToListAsync(
            """
            interface IShape { func area() }
            class Shape fulfills IShape {
                func area() { return 0 }
            }
            class Triangle extends Shape {
                overrule func area() { return 6 }
            }
            class Holder<T>(item: T) where T: IShape {
                prop item: T = $item
            }

            var h = new Holder<Triangle>(new Triangle())
            echo ($h.item.area())
            """);

        Assert.Equal(6, results[0]);
    }
}
