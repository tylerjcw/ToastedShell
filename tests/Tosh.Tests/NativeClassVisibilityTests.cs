using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>Native class members obey the same declaring-class privacy as ordinary statics.</summary>
public class NativeClassVisibilityTests
{
    private static ToshEngine NewEngine() => new(ToshRuntime.CreateDefault().Language);

    private const string Library = """
        class Magnitudes {
            shy bind native "libc.so.6" { func abs(number: int) -> int }
            proud shared func Read(n: int) -> int { return Magnitudes.abs($n) }
            proud shared prop Seven: int => Magnitudes.abs(-7)
            proud func InstanceRead(n: int) -> int { return Magnitudes.abs($n) }
        }
        class Child extends Magnitudes {
            proud shared func Reach() -> int { return Magnitudes.abs(-7) }
        }

        """;

    [Theory]
    [InlineData("Magnitudes.abs(-7)")]
    [InlineData("Child.abs(-7)")]
    [InlineData("Child.Reach()")]
    [InlineData("Magnitudes.abs()")]
    [InlineData("Magnitudes.abs")]
    [InlineData("var hidden = &Magnitudes.abs\n$hidden(-7)")]
    public async Task External_native_access_is_rejected_for_privacy(string call)
    {
        if (!OperatingSystem.IsLinux()) return;
        var engine = NewEngine();
        var control = await engine.ExecuteToListAsync(Library + "Magnitudes.Read(-7)");
        Assert.Equal(7, Convert.ToInt32(Assert.Single(control)));

        var error = await Assert.ThrowsAnyAsync<Exception>(() => NewEngine().ExecuteToListAsync(Library + call));
        Assert.Contains("shy", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("abs", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Magnitudes.Read(-7)")]
    [InlineData("Magnitudes.Seven")]
    [InlineData("Child.Read(-7)")]
    [InlineData("var instance = new Magnitudes()\n$instance.InstanceRead(-7)")]
    [InlineData("var wrapper = &Magnitudes.Read\n$wrapper(-7)")]
    public async Task Declaring_class_wrappers_retain_native_access(string call)
    {
        if (!OperatingSystem.IsLinux()) return;
        var values = await NewEngine().ExecuteToListAsync(Library + call);
        Assert.Equal(7, Convert.ToInt32(Assert.Single(values)));
    }

    [Theory]
    [InlineData("shy bind native \"libc.so.6\" { func abs(number: int) -> int }")]
    [InlineData("shy raw func abs(number: int) -> int from \"libc.so.6\"")]
    [InlineData("bind native \"libc.so.6\" { func abs(number: int) -> int }")]
    [InlineData("raw func abs(number: int) -> int from \"libc.so.6\"")]
    public async Task Both_declaration_forms_preserve_wrappers_but_refuse_outsiders(string binding)
    {
        if (!OperatingSystem.IsLinux()) return;
        var engine = NewEngine();
        var values = await engine.ExecuteToListAsync($$"""
            hermit class Native {
                {{binding}}
                proud shared func Read(n: int) -> int { return Native.abs($n) }
            }
            Native.Read(-9)
            """);
        Assert.Equal(9, Convert.ToInt32(Assert.Single(values)));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync("Native.abs(-9)"));
        Assert.Contains("shy", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("proud bind native \"libc.so.6\" { func abs(number: int) -> int }")]
    [InlineData("proud raw func abs(number: int) -> int from \"libc.so.6\"")]
    public async Task Public_native_calls_remain_available(string binding)
    {
        if (!OperatingSystem.IsLinux()) return;
        var values = await NewEngine().ExecuteToListAsync($$"""
            class Native { {{binding}} }
            class Derived extends Native { }
            Native.abs(-7)
            Derived.abs(-9)
            """);
        Assert.Equal(new[] { 7, 9 }, values.Select(value => Convert.ToInt32(value)));
    }

    [Fact]
    public async Task Nested_class_does_not_acquire_its_owners_native_privileges()
    {
        if (!OperatingSystem.IsLinux()) return;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => NewEngine().ExecuteToListAsync("""
            class Outer {
                shy bind native "libc.so.6" { func abs(number: int) -> int }
                class Nested { shared func Read() -> int { return Outer.abs(-7) } }
            }
            Outer.Nested.Read()
            """));
        Assert.Contains("shy", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Type_valued_variable_preserves_wrappers_but_not_private_access()
    {
        if (!OperatingSystem.IsLinux()) return;
        var engine = NewEngine();
        var values = await engine.ExecuteToListAsync(
            "module Types {\n" + Library + "\n}\nvar alias = Types.Magnitudes\n$alias.Read(-7)");
        Assert.Equal(7, Convert.ToInt32(Assert.Single(values)));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => engine.ExecuteToListAsync("$alias.abs(-7)"));
        Assert.Contains("shy", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
