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

## Decision taken 2026-08-17: composition, and a ToastOptions

`ToshRuntime` has **38 public members**. The language touches 24 across 182 references
and **never touches 14** — the display stack (`Display`, `DisplayPreferences`,
`DisplayProfiles`, `Inspector`), terminal and session (`Terminal`, `ExecHandler`,
`InlinePrompts`, `CommandLineInsertion`, `History`), and the `$tosh.Last` state
(`LastExitCode`, `LastResult`, `LastError`, `LastDiagnostic`, `LastCommandDuration`).
A third of the object is already cleanly shell-only.

**Chosen: composition.** A `ToastRuntime` in the language layer holds what the language
needs; `ToshRuntime` holds one as a field and adds the shell's own. `ToshEngine` takes a
`ToastRuntime`. Rejected: an interface (separates the contract but not the code, leaving
`ToshRuntime` one large class) and inheritance (every future member needs a judgement
about which class it lands in — the judgement that produced
`Config.Shell.MaxRecursionDepth` in the first place). Composition costs the most churn
and is the only one where a language-only host constructs a `ToastRuntime` and nothing
else.

**Chosen: a `ToastOptions` the language owns and TōSh populates.** This is what actually
holds the `~/.config/tosh` invariant, and it is worth being precise that the invariant is
**not held today**: `Tosh.Language` never reads the config *directory* — that was checked
in August and is true — but it does read values the shell loaded from one.

```
Config.Shell.MaxRecursionDepth   x4   a language limit, filed under "Shell"
Config.Diagnostics.Hushed        x2   `hush`, a language feature
Config.Theme.Diagnostics         x3   shell: how diagnostics are coloured
Config.Shell.Pipefail            x2   genuinely shell semantics
```

So the question a language-only host asks — "what is `MaxRecursionDepth` when there is
no TōSh?" — currently has no answer. `ToastOptions` gives it one: the language declares
its settings with defaults, and TōSh copies values in at startup.

## Staging

Two stages, because 182 references is not one commit.

1. **`ToastOptions`** — extract the language settings. Self-contained, and it is the part
   that holds the invariant, so it is worth having even if the rest waits.
2. **`ToastRuntime`** — the composition split. The borderline members were decided
   2026-08-17; see below.

## Stage 2 decisions, taken 2026-08-17

| Member | Decision |
|---|---|
| `Commands` (14) | **One `ICommandTable` of the six members the language uses** — `TryGet`, `All`, `AllNames`, `RegisterOrReplace`, `Remove`, `GetAliasMap`. Done 2026-08-17. The original decision was to split reads from writes, and it rested on my incorrect claim that the language never registers: a `global` or `export` function declaration must put a name in the runtime table, so a read-only view would not have compiled |
| `Output`/`Error` (16) | **The language emits values; the shell renders.** No `TextWriter` in `Tosh.Language` |
| `Events` (10) | **Language-side.** `event` is language syntax and the language calls `Register` and `MarkRequired`, not only `Raise`, so the bus goes with it |
| `ExitRequested` (4), `ExportedEnvironmentVariables` (1) | **`IToastHostSignals`** — done 2026-08-17. Membership differs from the plan: the language *reads* ExitRequested in four loop-stop checks and requests exit in exactly one place (the `--help` path, `TS-P2-52`), and exports were already shell-side — `ExportCommand` writes them, the language only asks `IsExported` once, in `forget` |
| `CurrentDirectory` (15) | **Passed per evaluation**, not held on any runtime. 14 of the 15 are path resolution; the single write is the AutoCd path, gated on `Config.Shell.AutoCd` and therefore shell behaviour by its own configuration |

**These are consistently the strictest option available, and that is a coherent choice
rather than an accidental one — but it changes the size of stage 2.** The original
estimate was "182 references to re-point", which described a *relocation*. Two of these
decisions change the language's **interface to its host** instead:

- *Emitting rather than writing* means every site that writes to a stream has to become a
  value or a reported diagnostic. `echo` is already yielded and rendered by the host, so
  the shape exists; script tracing and diagnostic output are not.
- *Per-evaluation directory* means threading a context through to fifteen call sites deep
  in the engine. It is mechanical, but it is invasive, and it is the one decision with a
  benefit beyond tidiness: two concurrent evaluations can have different working
  directories, which is not expressible today.

So stage 2 is not one commit. Proposed order, each independently verifiable:

  2a  ICommandTable                                  DONE 2026-08-17
  2b  IToastHostSignals                              DONE 2026-08-17
  2c  Events onto ToastRuntime                       a move
  2d  ToastRuntime itself, composed into ToshRuntime the bulk of the 182
  2e  Streams: emit rather than write                behaviour surface
  2f  CurrentDirectory per evaluation                threads a context

2e and 2f are the two that deserve their own scrutiny, and both are better done after
2d, when there is a `ToastRuntime` for the new shapes to live on.

## Notes

Depends on `TOAST-0004`; the boundary inversion has to land first or this becomes
untangling rather than moving.

The real test of this arrives with `TOSH-0003`: a machine with Tōast installed and
TōSh absent must still run a script. Until that works, the boundary is a directory
layout rather than a dependency.
