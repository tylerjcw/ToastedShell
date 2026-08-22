using Tosh.Compiler;
using Tosh.Language;
using Tosh.Language.Binding;
using Tosh.Runtime;

namespace Tosh.Tests;

/// <summary>
/// What still falls back to source replay, enumerated — `TOAST-0035`.
/// </summary>
/// <remarks>
/// <para>
/// Phase B's second bullet is "remove compiler-subset source replay". When the emitter
/// cannot produce IL for a construct it embeds the original text and re-evaluates it through
/// the tree-walking engine at load. The `runtime` profile refuses that, so it is the
/// instrument for finding out what is still replayed — which the item asked to be
/// enumerated with a program that triggers each one, rather than described.
/// </para>
/// <para>
/// The shape follows `DifferentialExecutionTests`: what emits is a corpus, what does not is
/// a **tripwire** asserted to still fall back. When one is fixed the test fails and says to
/// move it up, so progress on this item cannot happen silently.
/// </para>
/// <para>
/// Measured 2026-08-22 against a real library — the 16 files of this machine's `ToastLib`.
/// Fifteen were rejected, every one of them for the same reason: `module body`. The blocking
/// constructs below are what that reduces to.
/// </para>
/// </remarks>
[Collection(ConsoleSerialCollection.Name)]
public sealed class SourceReplaySurfaceTests : IClassFixture<ToshRuntimeFixture>
{
    private readonly ToshRuntime _runtime;

    public SourceReplaySurfaceTests(ToshRuntimeFixture fixture) => _runtime = fixture.Runtime;

    /// <summary>Emits under the `runtime` profile, which refuses source replay.</summary>
    private bool EmitsWithoutReplay(string source, out string reasons)
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse(source, "<replay-surface>");
        Assert.True(parse.Diagnostics.Count == 0, $"parse: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, _runtime.Commands);
        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(unit, $"ToshTest_{Guid.NewGuid():N}", stream, CompileProfile.Runtime);

        reasons = string.Join("; ", result.UnsupportedShapes);
        return result.IsClean;
    }

    private static string InModule(string body) => $"export module Probe {{\n{body}\n}}\n";

    // ── What emits: the corpus ────────────────────────────────────────────

    /// <summary>These module members become real IL, with no source carried.</summary>
    [Theory]
    [InlineData("a plain function", "    export func F(a: int) -> int => $a + 1")]
    [InlineData("a function with no parameters", "    export func F() -> int => 1")]
    [InlineData("several functions", "    export func F(a: int) -> int => $a\n    export func G(b: int) -> int => $b")]
    [InlineData("a variable", "    export var V: int = 1")]
    [InlineData("a class", "    export class C {\n        prop X: int = 1\n    }")]
    [InlineData("a derived class", "    export class B { }\n    export class D extends B { }")]
    [InlineData("a nested module", "    export module Inner {\n        export func G() -> int => 1\n    }")]
    [InlineData("a pipeline body", "    export func F(a: list<int>) -> int {\n        return ($a | count)\n    }")]
    [InlineData("an interpolation", "    export func F(a: int) -> string => $\"{$a}\"")]
    public void A_module_member_that_emits(string what, string body)
    {
        Assert.True(EmitsWithoutReplay(InModule(body), out var reasons), $"{what}: {reasons}");
    }

    // ── What still replays: the tripwires ─────────────────────────────────

    /// <summary>
    /// A declaration kind a module body cannot yet hold without replay.
    /// </summary>
    /// <remarks>
    /// Each of these has a CLR shell already, emitted when it appears at the **top level** —
    /// `DeclareClrEnumType`, `DeclareClrRecordShell`, `DeclareClrInterfaceShell` and the
    /// rest. What is missing is that the module path (`DeclareClrShellsInsideModule` and
    /// `ModuleNeedsSourceReplay`) knows only about classes and nested modules, so the same
    /// declaration one level in falls back.
    ///
    /// Usage counts in the library measured: `type` 18, `enum` 11, `interface` 2,
    /// `record` 1, and `trait` / `union` / `struct` not at all.
    /// </remarks>
    [Theory]
    [InlineData("a record", "    export record R(A: int, B: string)")]
    [InlineData("an enum", "    export enum E {\n        One\n        Two\n    }")]
    [InlineData("an interface", "    export interface I {\n        func M() -> int\n    }")]
    [InlineData("a trait", "    export trait T {\n        prop N: string = \"x\"\n    }")]
    [InlineData("a union", "    export union U {\n        Ok(value)\n        Err(message)\n    }")]
    [InlineData("a struct", "    export struct S {\n        prop X: int = 0\n    }")]
    [InlineData("a refinement type", "    export type Positive = int where $_ > 0")]
    public void A_declaration_kind_that_still_replays(string what, string body)
    {
        Assert.False(
            EmitsWithoutReplay(InModule(body), out _),
            $"{what} no longer falls back to source replay — move it into the corpus above " +
            "and record it on TOAST-0035.");
    }

    /// <summary>
    /// A function shape a module body cannot yet hold without replay.
    /// </summary>
    /// <remarks>
    /// This is the one that matters most, and it is not a declaration kind at all:
    /// `CanEmitClrModuleMethod` refuses any parameter that is optional, rest, or defaulted.
    /// Five of the sixteen library files measured have no blocking *declaration* whatsoever
    /// and are replayed for this alone.
    ///
    /// The machinery already exists one level up. A **top-level** function with a default
    /// parameter emits under this same profile: `DeclareUserFunction` switches to packed
    /// arguments and `EmitUserFunctionBody` substitutes a missing-argument sentinel,
    /// evaluating the default expression in the body. The module path does not use it.
    /// </remarks>
    [Theory]
    [InlineData("a defaulted parameter", "    export func F(a: int, b: int = 2) -> int => $a + $b")]
    [InlineData("a rest parameter", "    export func F(rest...) -> int => 1")]
    public void A_function_shape_that_still_replays(string what, string body)
    {
        Assert.False(
            EmitsWithoutReplay(InModule(body), out _),
            $"{what} no longer falls back to source replay — move it into the corpus above " +
            "and record it on TOAST-0035.");
    }

    /// <summary>
    /// The control: the same function shape emits at the top level.
    /// </summary>
    /// <remarks>
    /// What makes the row above a *gap* rather than a limitation of the language. If this
    /// ever stops emitting, the tripwire above is passing for the wrong reason.
    /// </remarks>
    [Fact]
    public void A_defaulted_parameter_emits_at_the_top_level()
    {
        Assert.True(
            EmitsWithoutReplay("export func F(a: int, b: int = 2) -> int => $a + $b\n", out var reasons),
            reasons);
    }
}
