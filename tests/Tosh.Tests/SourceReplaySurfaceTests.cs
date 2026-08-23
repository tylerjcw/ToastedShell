using System.Reflection;
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
    // Lifted out of replay by TOAST-0035 on 2026-08-22, by giving a module method the same
    // packed-argument shape a top-level function already had.
    [InlineData("a defaulted parameter", "    export func F(a: int, b: int = 2) -> int => $a + $b")]
    [InlineData("a rest parameter", "    export func F(rest...) -> int => 1")]
    [InlineData("a block argument", "    export func F(xs: list<int>) -> int {\n        return ($xs | each { $_ * 2 } | count)\n    }")]
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
    [InlineData("a trait", "    export trait T {\n        prop N: string = \"x\"\n    }")]
    [InlineData("a union", "    export union U {\n        Ok(value)\n        Err(message)\n    }")]
    [InlineData("a struct", "    export struct S {\n        prop X: int = 0\n    }")]
    [InlineData("a refinement type", "    export type Positive = int where $_ > 0")]
    // Deliberate: a refinement's predicate still lives in replayed source, so lifting the
    // alias out would take the check with it. Stamped as a different metadata kind from a
    // plain alias so the two cannot be confused at load.
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
    /// <summary>
    /// Emitting is not behaving, and these assert only the first.
    /// </summary>
    /// <remarks>
    /// Worth stating where it can be read next to the corpus above. Every row here was
    /// verified to *emit*, and a module method that emitted cleanly still returned `null`
    /// for an expression body — it computed the value, discarded it, and fell out through
    /// the implicit `return null`. That is the same trap `TOAST-0065` records for compiled
    /// `match`: a compiled backend can accept a shape and produce a different answer.
    ///
    /// What each shape *does* is asserted by `DifferentialExecutionTests`, which runs both
    /// backends and compares. A row belongs in both.
    /// </remarks>
    [Fact]
    public void Emitting_is_not_the_same_as_behaving()
    {
        // A module method with an expression body: emitted, and for a while returned null.
        Assert.True(
            EmitsWithoutReplay(InModule("    export func F(a: int) -> int => $a + 1"), out var reasons),
            reasons);
    }

    /// <summary>
    /// Emits, loads and runs under the `runtime` profile, returning what it printed.
    /// </summary>
    private string RunWithoutReplay(string source)
    {
        var engine = new ToshEngine(_runtime);
        var parse = engine.Parse(source, "<replay-surface-run>");
        Assert.True(parse.Diagnostics.Count == 0, $"parse: {string.Join(", ", parse.Diagnostics)}");

        var unit = Lowerer.Lower(parse, _runtime.Commands);
        var assemblyName = $"ToshTest_{Guid.NewGuid():N}";
        using var stream = new MemoryStream();
        var result = BoundUnitEmitter.Emit(unit, assemblyName, stream, CompileProfile.Runtime);
        Assert.True(result.IsClean, string.Join("; ", result.UnsupportedShapes));

        var program = Assembly.Load(stream.ToArray()).GetType($"{assemblyName}.Program");
        var main = program!.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;

        var originalOut = Console.Out;
        var capture = new StringWriter();
        try
        {
            Console.SetOut(capture);
            main.Invoke(null, new object?[] { Array.Empty<string>() });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return capture.ToString().Trim();
    }

    /// <summary>
    /// A module method emits *and answers* without replay — `TOAST-0035`.
    /// </summary>
    /// <remarks>
    /// The half of this item that is finished. Defaulted, rest and block-argument shapes all
    /// go through the packed-argument path, and the answers are pinned in
    /// `DifferentialExecutionTests` as well.
    /// </remarks>
    [Theory]
    [InlineData("export module M {\n    export func Add(a: int, b: int) -> int => $a + $b\n}\nvar r: int = M.Add(1, 5)\necho $r", "6")]
    [InlineData("export module M {\n    export func Add(a: int, b: int = 2) -> int => $a + $b\n}\nvar r: int = M.Add(1)\necho $r", "3")]
    public void A_module_method_answers_without_replay(string source, string expected)
        => Assert.Equal(expected, RunWithoutReplay(source));

    /// <summary>
    /// A module-scoped type answers without replay — `TOAST-0035`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These run the emitted program rather than checking that it emitted, and the
    /// distinction is the whole history of this item. A module-scoped class was accepted by
    /// `ModuleNeedsSourceReplay` from "step 1" onward, emitted with no source carried, and
    /// the emitted program then could not find the type — *"unknown type 'M.Box'"* — on the
    /// pushed commit as much as here. Nothing noticed, because nothing ran it.
    /// </para>
    /// <para>
    /// The cause was one line: a shell for a type declared inside a module is emitted as a
    /// top-level CLR type under its bare name, and `RegisterCompiledAssembly` aliases it by
    /// `ToshOriginalNameAttribute`, which was stamped only when the CLR could not spell the
    /// name. `Box` inside `M` was therefore registered as `Box`.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(
        "export module M {\n    export class Box {\n        prop X: int = 5\n    }\n}\n" +
        "var b: dynamic = new M.Box()\nvar r: int = $b.X\necho $r", "5")]
    [InlineData(
        "export module M {\n    export interface IShape {\n        func Area() -> int\n    }\n" +
        "    export class Square fulfills IShape {\n        func Area() -> int => 9\n    }\n}\n" +
        "var s: dynamic = new M.Square()\nvar r: int = $s.Area()\necho $r", "9")]
    // An enum reaches its members by a different route than `new` does: `M.Colour.Green` is
    // a member chain, and the walk looked for a static `Colour` on the module shell. It now
    // falls back to resolving the prefix as a compiled *type*.
    [InlineData(
        "export module M {\n    export enum Colour {\n        Red\n        Green\n    }\n}\n" +
        "var r: string = $\"{M.Colour.Green}\"\necho $r", "Green")]
    // A record constructs with `new`, so the type-alias route the stamp populates is the one
    // it uses. The first probe for this wrote `M.Point(3, 4)` and the engine answered
    // "Construct instances with 'new M.Point(...)'" — the test was wrong, not the compiler.
    [InlineData(
        "export module M {\n    export record Point(X: int, Y: int)\n}\n" +
        "var p: dynamic = new M.Point(3, 4)\nvar r: int = $p.Y\necho $r", "4")]
    // A plain `type` alias. Emitting the shell was never the problem — nothing read its
    // BaseTypeName back, so `M.Meters` named the shell class rather than `int` and a value
    // could not be assigned to it.
    [InlineData(
        "export module M {\n    export type Meters = int\n}\n" +
        "var d: M.Meters = 5\necho $d", "5")]
    public void A_module_scoped_type_answers_without_replay(string source, string expected)
        => Assert.Equal(expected, RunWithoutReplay(source));

    /// <summary>
    /// A computed property emits and answers — `TOAST-0038`.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `prop Label: string => …` is a getter whose body is an ordinary expression. It was
    /// refused by the class-shell guard, so any class declaring one was replayed whole —
    /// and this was the **only** thing between the readiness probe and a strict compile.
    /// The probe declares three of them and uses no other unsupported shape.
    /// </para>
    /// <para>
    /// Emitted as a real CLR property with a getter and no backing field, so reflection
    /// finds it the ordinary way. A field would be one nothing ever writes, shadowing the
    /// getter for any reader that looks at fields first.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(
        "export class Item(pos: int) {\n    prop Position: int = $pos\n" +
        "    prop Label: string => $\"at offset {$this.Position}\"\n}\n" +
        "var i: dynamic = new Item(7)\nvar r: string = $i.Label\necho $r", "at offset 7")]
    [InlineData(
        "export class Counter(n: int) {\n    prop N: int = $n\n" +
        "    prop Doubled: int => $this.N * 2\n}\n" +
        "var c: dynamic = new Counter(21)\nvar r: int = $c.Doubled\necho $r", "42")]
    public void A_computed_property_answers_without_replay(string source, string expected)
        => Assert.Equal(expected, RunWithoutReplay(source));

    /// <summary>A settable computed property is still replayed, deliberately.</summary>
    /// <remarks>
    /// A setter body has to agree with the getter about where the value lives, and a
    /// computed property has no field to agree about. Left for whoever needs it.
    /// </remarks>
    [Fact]
    public void A_settable_computed_property_still_replays()
        => Assert.False(
            EmitsWithoutReplay(
                "export class Item {\n    prop Label: string {\n        get => \"x\"\n" +
                "        set { }\n    }\n}\n",
                out _));

    /// <summary>
    /// The kinds a stamp alone does not lift out of replay.
    /// </summary>
    /// <remarks>
    /// Tripwires. Each was measured with the stamp in place and each failed differently,
    /// which is why they are not simply "the rest of the switch":
    ///
    ///   record  `M.Point(3, 4)` — read as a static *method* on the module shell
    ///   union   `M.Result.Ok`   — static member not found on the module shell
    ///   struct  the property read came back as something `int` would not take
    ///   trait   the class using it still resolved to nothing
    ///
    /// Each needs its own construction or member path taught about a module-qualified
    /// shell. When one starts emitting, this fails and says to verify it by running it.
    /// </remarks>
    [Theory]
    [InlineData("a struct", "    export struct Vec {\n        prop X: int = 7\n    }")]
    [InlineData("a trait", "    export trait Named {\n        prop Name = \"anon\"\n    }")]
    [InlineData("a union", "    export union Result {\n        Ok(value)\n        Err(message)\n    }")]
    public void A_declaration_kind_a_stamp_does_not_lift(string what, string body)
    {
        Assert.False(
            EmitsWithoutReplay(InModule(body), out _),
            $"{what} now emits without replay — run the emitted program and check it gives " +
            "the interpreted answer before moving it into the corpus (TOAST-0035).");
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
