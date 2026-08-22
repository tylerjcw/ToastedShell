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

## Acceptance

- [x] Every "unsupported" emitter diagnostic is enumerated with a program that triggers it —
      `SourceReplaySurfaceTests`, as a corpus plus tripwires
- [ ] Each is either implemented, or recorded as a deliberate and documented exclusion
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
