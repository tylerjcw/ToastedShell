---
id: TOAST-0042
title: "A compiled program did not convert its arguments, and toshc named the one file you must not run"
status: partial
area: toast
priority: 2
opened: 2026-08-21
---

## Problem

Reported from a real session: compiling `examples/mandelbrot.tosh` and trying to run the
result.

```
❯ tosh --compile examples/mandelbrot.tosh -o ./mandelbrot --publish-single-file
toshc: wrote .../mandelbrot.deps.json
toshc: wrote .../mandelbrot
toshc: wrote single-file bundle .../mandelbrot
toshc: wrote .../mandelbrot.dll        ← last line

❯ ./mandelbrot.dll
0024:err:mscoree:CLRRuntimeInfo_GetRuntimeHost Wine Mono is not installed
```

Three separate faults, only one of which is cosmetic.

### 1. The output named the wrong artifact last

`mandelbrot` is an **ELF 64-bit executable**, 6.1 MB. `mandelbrot.dll` is a **PE32 image**,
13.8 kB — a managed assembly, which on Linux `binfmt_misc` hands to Wine. A reader takes
the last line as the result, and the last line was the `.dll`.

Nothing in the sequence said which file to run.

### 2. A compiled program did not convert its arguments

`ConvertCompiledScriptArg` was a hand-written switch over four literal type names — `int`,
`long`, `bool`, `string` — and returned everything else **untouched**. So a declared
`double`, an enum, or a refinement alias arrived as the raw `string` from the command line.

`mandelbrot.tosh` declares `arg frames: PosInt` and later divides by it, so the compiled
program failed with:

```
Operator operands 'System.Int32' and 'System.String' are not compatible
```

— reported from a line of arithmetic, which never mentions arguments. The interpreted
program ran, because the interpreter converts through the annotation machinery that
resolves the alias and applies its `coerce` clause.

Same shape as `TOAST-0030`'s causes: one rule, implemented twice, and the copies drifted.

### 3. An uncaught failure prints a CLR stack trace — **not fixed**

Same condition, two presentations:

| | |
|---|---|
| interpreted | `✖ error tosh.runtime.unknown_script_flag` … `help: this script does not declare any flags.` |
| compiled | `Unhandled exception. System.InvalidOperationException: …` + stack trace + core dump |

Even a `ToshDiagnosticException` — which carries the code, title, span and help — is
printed as a raw .NET unhandled exception.

## Resolution so far — 2026-08-21

**Arguments convert.** `ConvertCompiledScriptArg` keeps its four fast paths and now falls
through to `CheckType`, the same annotation bridge this file already uses for
`var x: T = …`. `mandelbrot` runs compiled.

**The output names what to run, last.** The assembly and `deps.json` come first, then the
apphost and bundle, then a final line: `toshc: run it with './mandelbrot'` — or
`'dotnet out.dll'` when no launcher was emitted.

### Left open, deliberately

Fault 3. `Main` is emitted with no exception handling, and wrapping it in IL is not a small
change: a `ret` inside a try block is illegal, and a top-level `return` emits one. It also
belongs with `TOAST-0037`, which owns compiler diagnostics — a rendered failure needs the
code manifest that item is for.

### One difference that is already filed

`Zoom Level: 1%` compiled against `1.00%` interpreted. That is `TOAST-0022` — compiled
interpolation drops format clauses — not a new fault.

## Acceptance

- [x] A declared argument type is converted, whatever kind of type it is
- [x] The four types the old switch handled still work, pinned as controls
- [x] `toshc` names the runnable artifact last and says how to run it
- [x] `examples/mandelbrot.tosh` runs compiled
- [x] A negative control — reverting the conversion fails exactly the four types that
      fell through and leaves the four controls passing
- [ ] An uncaught failure in a compiled program is reported as a Tōast diagnostic rather
      than an unhandled CLR exception

## Notes

The argument corpus runs the compiled program with real `argv`, which the differential
corpus cannot: `DifferentialExecutionTests` invokes `Main` with an empty array, so no case
there can reach argument binding at all. That is why four broken conversions survived a
corpus built to catch exactly this kind of divergence.
