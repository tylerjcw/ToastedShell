using Tosh.Language;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// TS-P1-21 — a method parameter default may reference <c>$this</c>,
/// because the instance already exists when the call binds. A
/// constructor default may not: it binds before that layer's properties
/// are initialised, so it would read uninitialised state. The
/// constructor case reports a targeted diagnostic rather than the
/// generic unknown-variable error.
/// </summary>
public sealed class SelfInParameterDefaultTests
{
    [Fact]
    public async Task Method_default_can_read_an_instance_property()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            class C {
                prop V = 5
                func m(a, b = $this.V) { return $b }
            }
            var got = ((new C()).m(1))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(5, got);
    }

    [Fact]
    public async Task Method_default_sees_both_this_and_earlier_parameters()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            class C {
                prop V = 10
                func m(a, b = $this.V + $a) { return $b }
            }
            var got = ((new C()).m(5))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(15, got);
    }

    [Fact]
    public async Task Method_default_can_read_an_inherited_property()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            class B { prop V = 3 }
            class D extends B { func m(a, b = $this.V) { return $b } }
            var got = ((new D()).m(1))
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(3, got);
    }

    [Fact]
    public async Task Constructor_default_referencing_this_is_diagnosed()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        var exception = await Assert.ThrowsAsync<ToshDiagnosticException>(() =>
            engine.ExecuteToListAsync(
                """
                class D {
                    prop V = 7
                    D(a, b = $this.V) { }
                }
                new D(1)
                """));

        // Locks the coupling to the evaluator's unknown-variable
        // diagnostic that ReferencesUnavailableSelf keys on: if that
        // diagnostic changes shape, this fails loudly instead of
        // silently degrading to the generic message.
        Assert.Equal(
            "tosh.runtime.self_unavailable_in_constructor_default",
            exception.Diagnostics[0].Code);
        Assert.Contains("$this", exception.Diagnostics[0].Label ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_method_and_static_defaults_are_unaffected()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            class C { func m(a, b = 2) { return $b } }
            class S { shared func m(a, b = $a + 1) { return $b } }
            var instanceDefault = ((new C()).m(1))
            var staticDefault = (S.m(1))
            """);

        Assert.True(engine.TryGetVariableValue("instanceDefault", out var instanceDefault));
        Assert.Equal(2, instanceDefault);
        Assert.True(engine.TryGetVariableValue("staticDefault", out var staticDefault));
        Assert.Equal(2, staticDefault);
    }

    [Fact]
    public async Task A_constructor_default_that_does_not_use_this_still_works()
    {
        var engine = new ToshEngine(ToshRuntime.CreateDefault().Language);

        await engine.ExecuteToListAsync(
            """
            class D {
                prop B = 0
                D(a, b = $a * 3) { $this.B = $b }
            }
            var got = ((new D(4)).B)
            """);

        Assert.True(engine.TryGetVariableValue("got", out var got));
        Assert.Equal(12, got);
    }
}
