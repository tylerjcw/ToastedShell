---
id: TOAST-0006
title: "Divide the assemblies along the language/shell boundary"
status: open
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

Phase A3 of [the separation plan](../../TOAST_SEPARATION_PLAN.md).

| Tōast (the language) | TōSh (the shell) |
|---|---|
| lexer, parser, binder, lowerer, evaluator | REPL, line editor, prompt, job control |
| type system — refinements, generics, traits | display engine, themes, profiles |
| value model — quantities, complex, vectors | help catalog, config browser |
| FFI and CLR interop | external processes, TSSP |
| diagnostics infrastructure | packaging, publishing |

`Tosh.Runtime` (56,373 lines) is the real work, not `Tosh.Language`. It holds the
value model *and* the display engine *and* the help catalog *and* command metadata.
The split runs through it, not around it.

## Acceptance

- [ ] `Tosh.Runtime` divided, with the value model on the language side and display, help and command metadata on the shell side
- [ ] No language assembly references a shell assembly, verified by project references rather than by inspection
- [ ] The suite passes; assembly moves do not change behaviour
- [x] The language's transitive dependency on `Tosh.Tui` cut, and guarded by `AssemblyBoundaryTests` walking the emitted assembly graph
- [ ] `src/Tosh.Language/Bridge/Shell/` resolved — **decided 2026-08-16: all four commands move to the shell.** See below

## Decision: the language registers no commands

`source`, `eval`, `debug` and `format` are registered by the `ToshEngine` constructor
and live under `src/Tosh.Language/Bridge/`. All four move to the shell, because **a
command is a shell concept**: the language should expose capabilities and TōSh should
name them. That is the only reading under which "does the language own commands?" has a
clean answer.

The alternative considered and rejected was moving only `source` — it is a shell
convention (bash `source`/`.`) while `eval` and `debug` are language capabilities. That
splits four siblings across two assemblies on a judgement about *naming* rather than
behaviour, and `source` in fact executes a script into the caller's scope, which is a
language operation wearing a shell name.

**What the move requires**, measured rather than assumed. `ToshRuntime.Evaluator`
already exists as `IShellEvaluator?` and `ToshEngine` already assigns itself to it — at
line 102, before the registrations at 113–130, so the ordering works. But
`IShellEvaluator` does not expose what three of the commands need:

| Command | Needs |
|---|---|
| `eval` | `EvaluateAsync` — already on the interface |
| `source` | `ResolveSourcePath`, `ExecuteScriptFileAsync` |
| `debug` | `ExecuteScriptFileAsync`, `DebugHook` (get and set) |
| `format` | nothing — it does not touch the engine at all |

So the work is: widen `IShellEvaluator` (or add a companion interface) with those three
members, move the four classes to `Tosh.Stdlib`, register them there, and delete the
registrations from the engine constructor. The commands resolve the evaluator from the
runtime at execute time rather than taking an engine at construction, which is what lets
them be registered before an engine exists.

## Notes

Depends on `TOAST-0004`; the boundary inversion has to land first or this becomes
untangling rather than moving.

The real test of this arrives with `TOSH-0003`: a machine with Tōast installed and
TōSh absent must still run a script. Until that works, the boundary is a directory
layout rather than a dependency.
