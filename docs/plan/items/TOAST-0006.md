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
- [ ] `src/Tosh.Language/Bridge/Shell/` resolved — `EvalCommand` and `DebugCommand` are language-level, `source` is a shell verb and names the config directory

## Notes

Depends on `TOAST-0004`; the boundary inversion has to land first or this becomes
untangling rather than moving.

The real test of this arrives with `TOSH-0003`: a machine with Tōast installed and
TōSh absent must still run a script. Until that works, the boundary is a directory
layout rather than a dependency.
