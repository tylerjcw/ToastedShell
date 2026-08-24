---
id: TOAST-0042
title: "A compiled program did not convert its arguments, and toshc named the one file you must not run"
status: complete
area: toast
priority: 2
opened: 2026-08-21
closed: 2026-08-24
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

### 3. An uncaught failure printed a CLR stack trace

Same condition, two presentations:

| | |
|---|---|
| interpreted | `✖ error tosh.runtime.unknown_script_flag` … `help: this script does not declare any flags.` |
| compiled | `Unhandled exception. System.InvalidOperationException: …` + stack trace + core dump |

Even a `ToshDiagnosticException` — which carries the code, title, span and help — is
printed as a raw .NET unhandled exception.

## Earlier resolution — 2026-08-21

**Arguments convert.** `ConvertCompiledScriptArg` keeps its four fast paths and now falls
through to `CheckType`, the same annotation bridge this file already uses for
`var x: T = …`. `mandelbrot` runs compiled.

**The output names what to run, last.** The assembly and `deps.json` come first, then the
apphost and bundle, then a final line: `toshc: run it with './mandelbrot'` — or
`'dotnet out.dll'` when no launcher was emitted.

### Initially left open

Fault 3. `Main` is emitted with no exception handling, and wrapping it in IL is not a small
change: a `ret` inside a try block is illegal, and a top-level `return` emits one. It also
belongs with `TOAST-0037`, which owns compiler diagnostics — a rendered failure needs the
code manifest that item is for.

## Final resolution — 2026-08-24

The emitter now puts the existing `Program.Main(string[] args)` body inside one outer
exception region. Source-level `return` already leaves through a defer-aware epilogue, so
that epilogue was moved outside the new catch and `Main` still contains exactly one legal
`ret`. The public `void Main(string[])` CLR ABI is unchanged.

The catch delegates presentation to `Tosh.Runtime.CompiledProgramBoundary`. A
`ToshDiagnosticException` retains its original code, source span, label and help; another
exception is rendered through the existing `tosh.runtime.error` diagnostic rather than the
CLR unhandled-exception printer. The process exits with status 1, and redirected stderr uses
the plain diagnostic format.

This boundary is process-aware. It handles the failure only when the emitted assembly is
`Assembly.GetEntryAssembly()`, as it is under `dotnet program.dll`, an apphost or a bundle.
Reflection and embedding callers still receive the original exception unchanged, because
their host owns presentation. The helper lives in `Tosh.Runtime`, so pure-profile artifacts
do not acquire a dependency on `Tosh.Compiler.Runtime` merely to report a failure.

This does not depend on `TOAST-0037`: that item names failures produced *by the compiler*.
The entry-point boundary renders runtime diagnostics whose manifest codes already exist.
End-to-end child-process tests pin a structured diagnostic, an ordinary exception, non-zero
exit status, absence of a CLR stack trace, reflected-call propagation, and a successful
top-level `return` as the control.

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
- [x] An uncaught failure in a compiled program is reported as a Tōast diagnostic rather
      than an unhandled CLR exception

## Notes

The argument corpus runs the compiled program with real `argv`, which the differential
corpus cannot: `DifferentialExecutionTests` invokes `Main` with an empty array, so no case
there can reach argument binding at all. That is why four broken conversions survived a
corpus built to catch exactly this kind of divergence.
