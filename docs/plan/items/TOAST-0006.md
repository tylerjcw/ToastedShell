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
- [x] `src/Tosh.Language/Bridge/Shell/` resolved — all four commands moved to Tosh.Stdlib; the language registers none. `Bridge/` now holds only language machinery

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

## Measured 2026-08-17: the division is far smaller than it looks

`Tosh.Runtime` is 59,093 lines and reads like a thorough untangle. It is not.

**The value model has zero references to the shell clusters.** Checked across
`OperatorEvaluator`, `DotNetTypeResolver`, `ReflectionInvoker`, `TypeConversion`,
`ToshMatrix`, `ToshVector`, `AggregationUtilities`, `ShellIndexingUtilities`,
`LanguageSurface`, `TomlParser`, `Units/` and `Formats/` — not one names
`DisplayEngine`, `HelpCatalog`, `ShellJob` or the system-info services in code. The
coupling is not distributed through the runtime.

**An earlier measurement here was misleading and is corrected.** Counting "inbound
references" to each shell cluster from *any* runtime file gave display 8 and help 3, and
suggested those clusters were entangled. Most of those references are shell-to-shell —
`DisplayEngine` renders a `HelpTopic`, `CommandMetadataExporter` calls `HelpCatalog`.
One of help's three was a **doc comment**. Counting only language-side files gives zero.

**What the language actually uses from the runtime**, by public type:

| | Types | References |
|---|---:|---:|
| language-shaped (the value model) | 120 | 1,618 |
| shell-shaped | **12** | ~24 |

The twelve are the whole remaining boundary:

```
ShellJob, ShellJobProcessSpec, ShellJobRedirectionSpec,      background jobs
ShellJobRedirectionMode, ShellJobRedirectionStream
HelpTopic, HelpOptionInfo, HelpArgumentInfo, HelpSubjectKind  help metadata
RuntimeNamespaceDisplaySummary, ToshConfig                    display, config
IExternalProcessCommand                                       already correct (TOAST-0004)
```

So the shape is: the value model moves to a language-side assembly, `Tosh.Runtime`
keeps display, help, jobs, system services and the composition root, and roughly
twenty-four references across eleven types are the coupling to resolve — the same order
of magnitude as `TOAST-0004`'s two, not a rewrite.

**The one structural question left** is `ToshEngine(ToshRuntime)`. The engine takes the
shell's runtime object, which holds `Commands`, `Display`, `History` and `Config`
alongside the value model. Splitting it means deciding what a *language* runtime is and
letting the shell extend it. That is a design decision rather than a move, and it should
be taken deliberately rather than discovered halfway through an assembly split.

## Notes

Depends on `TOAST-0004`; the boundary inversion has to land first or this becomes
untangling rather than moving.

The real test of this arrives with `TOSH-0003`: a machine with Tōast installed and
TōSh absent must still run a script. Until that works, the boundary is a directory
layout rather than a dependency.
