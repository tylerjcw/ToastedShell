---
id: TOAST-0035
title: "Source replay and implicit dynamic are how the compiler handles what it cannot emit"
status: partial
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

Phase B's second bullet: "remove compiler-subset source replay and implicit dynamic
fallbacks". Both mechanisms are live.

**Source replay** — when the emitter cannot produce IL for a construct, the construct is
left to be re-executed by the tree-walking evaluator at runtime. `TOAST-0030` showed what
that costs when the fallback itself does not work: `class E extends Error { }` was handed
to replay and failed at runtime with "Command 'class' was not found", so a declaration that
runs interpreted did not run at all compiled. It is referenced in **61 places** across
`src/Tosh.Compiler`, `src/Tosh.Compiler.Runtime` and `src/Tosh.Language`, under the name
"Tier 3".

**Implicit dynamic** — `--compile-allow-dynamic` exists so a program with un-inferrable
locals can compile anyway, by falling back to dynamic dispatch.

## Measured 2026-08-21

- Eleven distinct "unsupported *X*" emitter diagnostics: assignment operator, block body
  stage, destructuring pattern, expression, first pipeline stage, interpolated part,
  literal type, member assignment target, numeric op, pipeline stage, statement.
- Constructs explicitly documented as staying Tier 3 include native `require` blocks and
  bind blocks.
- The readiness probe (`TOAST-0038`) hits `implicit_dynamic` four times.

## Why the order matters

Removing the fallbacks *first* would simply make working programs stop compiling.
`TOAST-0034` is the prerequisite for the dynamic half — with inference propagating,
most `implicit_dynamic` sites disappear rather than needing annotation — and the emitter
gaps behind the eleven "unsupported" messages are the prerequisite for the replay half.

The honest sequence is: make the fallback unnecessary, then remove it, then keep it removed
with a strict-mode gate.

## Measured against a real library — 2026-08-22

The `runtime` profile already refuses source replay, so it is the instrument for finding out
what is still replayed. Pointed at this machine's `ToastLib` — sixteen files, 2,559 lines,
the thing a user would actually want compiled:

**Fifteen of sixteen were rejected, every one for the same reason: `module body`.** Only
`Math.tosh` emitted.

That reduces to a short list, now pinned as `SourceReplaySurfaceTests` — what emits is a
corpus, what does not is a tripwire asserted to *still* fall back, so a fix cannot land
silently:

| Emits inside a module | Still replays |
|---|---|
| `func`, `var`, `class`, `class extends`, nested `module`, pipeline bodies, interpolation | `record`, `enum`, `interface`, `trait`, `union`, `struct`, refinement `type` |

### The blocker is not a declaration kind

Five of the sixteen files — `Bluetooth`, `Git`, `Native`, `Shell`, `System` — contain **none**
of the blocking declarations and are replayed anyway. `CanEmitClrModuleMethod` refuses any
parameter that is optional, rest, or defaulted, and a library is full of
`func FromGl(r: double, g: double, b: double, a: double = 1.0)`.

**A default parameter is therefore the single highest-value gap**, ahead of every declaration
kind. Usage in the measured library: `type` 18, `enum` 11, `interface` 2, `record` 1, and
`trait` / `union` / `struct` zero.

### Both halves already exist one level up

Neither gap needs new machinery, which is what makes this tractable:

- **Declaration kinds.** Every one of them already has a CLR shell emitted when it appears at
  the **top level** — `DeclareClrEnumType`, `DeclareClrRecordShell`,
  `DeclareClrInterfaceShell`, and the rest, in one switch. `DeclareClrShellsInsideModule`
  knows only about classes and nested modules, and `ModuleNeedsSourceReplay` allows only the
  same two. The top-level switch is the list the module path is missing.
- **Default parameters.** A top-level function with one emits under this same profile:
  `DeclareUserFunction` switches to packed arguments and `EmitUserFunctionBody` substitutes a
  missing-argument sentinel, evaluating the default expression in the body. That prologue is
  inline in `EmitUserFunctionBody` rather than shared, so the module path cannot call it.

### Why this stopped at the enumeration

The module shell's methods are not what tosh code calls. There are no IL call sites against
them — the shell exists "so external .NET callers can reflect over compiled tosh types", and
tosh-internal calls resolve through the engine, which today learns the module's contents from
the replayed source. Removing the replay therefore has to answer *where the engine learns
them instead*, and getting that wrong does not fail loudly: it makes a module's functions
silently unresolvable at run time, in a shell's own library.

That is a design question about the calling convention, not a missing case in a switch, and
it is the next thing this item needs.

## Decision 1 done — packed arguments, 2026-08-22

Chosen by the user over CLR-optional constants, because a default may be any expression and
theirs are not all constants: `span: TimeSpan = 1d` is not a CLR constant, and 67 defaulted
parameters were measured across the library.

A module method with an optional, rest, or defaulted parameter now takes its arguments packed
into one `object[]`, substitutes a missing-argument sentinel, and evaluates the default in its
own body — **the same shape a top-level function already used**. The prologue was inline in
`EmitUserFunctionBody`, which is exactly why the module path could not have it; it is now
shared, and the extraction was verified behaviour-neutral before anything else changed.

`ToshPackedArgumentsAttribute` marks the shape, because it is not inferable: a tosh function
of one array parameter emits the same signature.

### Three defects underneath it, none of which were failing before

Each was unreachable while these modules were replayed, and each became reachable the moment
they were not. That is the shape of this whole item.

1. **A module method with an expression body returned `null`.** `func Add(a, b) -> int =>
   $a + $b` computed its value, discarded it, and fell out through the implicit
   `return null`. The trailing-expression collapse that top-level functions and class methods
   both use was never applied to module methods. It emitted cleanly and answered nothing —
   **on both profiles**, so this was not a strict-mode problem.
2. **A dotted name in command position did not resolve.** `Probe.Add 1` reached the engine's
   command table by way of the replayed source; without replay it reported *"unknown command:
   'Probe.Add'"* while `Probe.Add(1)` resolved. `InvokeAndDrain` now asks the compiled shells
   first, the same order `InvokeQualifiedMethod` already used.
3. **A block argument inside a module method crashed the compiler, then the program.**
   `EmitBlockBodyMethod` adds a helper to `Program`, which was already created by the time
   module bodies were emitted — *"Unable to change after type has been created"*. Emitting
   module bodies first fixed that and exposed the next one: the helper was `Private`, and a
   module shell is a different type, so the first call threw `MethodAccessException`.

The corpus was strengthened for the same reason. `SourceReplaySurfaceTests` asserts a shape
**emits**; that is what let defect 1 hide, since a method that returns nothing emits
perfectly. `DifferentialExecutionTests` now carries four module cases asserting what they
*answer*.

## Decision 2 measured and not kept — 2026-08-22

The user chose "all seven declaration kinds, mirroring the top level". Mirroring the switch
is a small change and it was made: `DeclareClrShellsInsideModule` and
`ModuleNeedsSourceReplay` gained enum, record, interface, struct, trait and union.

**All six then emitted without replay, and all six failed at run time.** One root cause,
three faces of it:

```
record   No overload matched static method 'Point' on type 'p.M'
struct   unknown type 'M.Vec' in `new` expression
union    Static member 'Result' was not found on type 'p.M'
```

A shell is declared under its **bare** name and never wired to the module-qualified path
`ToshHost.ResolveQualifiedAccess` walks. Emitting the shell is not the missing piece; being
findable as `M.Point` is.

### The correction this forced

**A module-scoped *class* has the same defect, and always did.**
`ModuleNeedsSourceReplay` has accepted one since "step 1 of the first-class .NET plan", so
such a module already emits with no source carried — and the emitted program then fails with
*"unknown type 'M.Box'"*. Measured on the pushed commit as well, so it predates this work.

So the replay-free module path has never worked end to end for any declaration kind. What
`SourceReplaySurfaceTests` recorded as "emits" was true and was not the question worth
asking, which is the second time this item has been caught by that distinction — the first
was a module method that emitted and returned `null`.

The six were reverted rather than shipped: a kind that emits and crashes is worse than one
that replays and works. The class case is now a tripwire
(`A_module_scoped_class_emits_without_replay_and_then_fails`) so the defect cannot stay
invisible, and the module-method successes are asserted by running them rather than emitting
them.

### The prerequisite, built — module-qualified stamping

One line, and it fixes the defect that was already shipping. A shell for a type declared
inside a module is emitted as a top-level CLR type under its **bare** name, and
`RegisterCompiledAssembly` aliases it by `ToshOriginalNameAttribute` — which
`StampOriginalNameIfMangled` writes only when the CLR cannot spell the name. So `class Box`
inside `module M` was registered as `Box`, and nothing could ask for `M.Box`.

`StampModuleQualifiedName` writes it unconditionally. A qualified name is never what the CLR
type is called, so there is nothing to compare against.

| Kind | With the stamp |
|---|---|
| class | **works** — `new M.Box()` gives 5, matching interpreted |
| interface | **works** — a class fulfilling it answers 9 |
| record | `M.Point(3, 4)` is read as a static *method* on the module shell |
| union | `M.Result.Ok` — static member not found on the module shell |
| struct | the property read comes back as something `int` will not take |
| trait | the class using it still resolves to nothing |

So the stamp is necessary and not sufficient: two kinds are lifted by it, four need their own
construction or member path taught about a module-qualified shell. The four are **left
replaying** and are tripwires — a kind that emits and fails is worse than one that replays
and works, which is the lesson this item keeps teaching.

The measured library is unchanged at 2 of 16, because its blockers are refinement types (18)
and enums (11) rather than classes. Enum is the next kind and is not in the table above: its
shell calls `CreateType()` during declaration, so it cannot be stamped afterwards the way
these are, and needs the qualified name passed in.

### Where the measured library stands

Two of sixteen files now emit with no source replay, against one before. The remaining
fourteen are decisions 2 and 3 — declaration kinds, and refinement types, which are the most
used blocking construct at 18.

## Acceptance

- [x] Every "unsupported" emitter diagnostic is enumerated with a program that triggers it —
      `SourceReplaySurfaceTests`, as a corpus plus tripwires
- [ ] Each is either implemented, or recorded as a deliberate and documented exclusion —
      **implemented**: default, rest and optional parameters; module-scoped classes and
      interfaces. **Remaining**: enum, record, struct, trait, union, and refinement types
- [ ] `--compile-allow-dynamic` is not needed by any program in `examples/` or `bench/`
- [ ] A strict profile fails the build rather than replaying source
- [ ] A negative control

## Why it is worth finishing

Raised by the user asking whether compiling a library would pay off. Measured, the answer is
"yes, but not yet, and for a reason worth stating": the library files that *do* compile embed
their module bodies as source and re-evaluate them through the interpreter at load, so a
compiled library pays the same parse cost from a string in a DLL instead of a file on disk.
`Point.tosh` compiles, and `IPoint` appears eleven times in the emitted assembly as a UTF-16
string constant.

The prize is measurable. That machine's library costs about 100 ms of a 320 ms shell
start-up, and the cost tracks line count — roughly 0.07 ms per line across files from 166 to
718 lines — which is parse-and-bind, not FFI. Removing source replay is what would make that
compilable away. `Sdl.tosh`'s `bind native "libSDL2-2.0.so.0"` would remain.

## Notes

`TOAST-0030` closed the one replay path that was actively wrong. This item is about the
mechanism rather than a single use of it.
