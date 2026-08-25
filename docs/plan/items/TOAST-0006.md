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

## "Streams" meant three things, and only one of them is this item's

The decision above was recorded as "no `TextWriter` in `Tosh.Language`", which reads as
though Tōast should not have stream I/O at all. It should. Three separate things were
being called streams:

1. **The session's output** — `Runtime.Output`/`Runtime.Error`, meaning "where this
   REPL's text goes": a terminal, a `StringWriter` in a test, a pipe. Shell session
   state, and the only one this item touches.
2. **File and stream I/O as a language capability** — open, read, write, seek, close.
   C has `FILE*`; C# has `Stream`. **Tōast needs this and always did.**
   `ManagedFileHandle` already lives on the value-model side, which is correct.
3. **Redirection** — `cmd > file`. Parsed by `ToshParser`, so it is language syntax, and
   it stays in Tōast.

What changes for (3) is its *target*, not its home. It currently swaps `Runtime.Output`
— the session writer — for a composite writer. It should redirect to a **Tōast stream
handle**, which is a language concept, so redirection works identically in a `no_clr`
host with no shell present.

This matters beyond tidiness because the end goal is a self-hosting Tōast with native
targets and TōSh rewritten in it. A language that cannot open a file cannot compile
itself.

## `ReflectionInvoker` is now behind an interface, decided in 2d

`no_clr` needs the value model to work without .NET reflection. Before this slice, two of
the three core abstractions permitted that and one did not:

| | |
|---|---|
| `IObjectAccessor` | interface — a `no_clr` implementation can be substituted |
| `ITypeResolver` | interface — likewise |
| **`ReflectionInvoker`** | **sealed class, and the language's most-used member at 26 references** |

The reflection weight behind them was not the problem — `DotNetTypeResolver` has 122
reflection uses and `TypeConversion` 58 — because those are *the CLR implementation*
behind an interface. The problem was that `Invoker` was typed as the concrete class, so a
native target had nowhere to plug in. `IObjectInvoker` is now the contract, and
`ReflectionInvoker` is the default .NET implementation.

Small while `ToastRuntime`'s member types are being decided; painful afterwards. So it
belongs in 2d rather than in a later `no_clr` item.

## State after 2026-08-24

The language reads no shell configuration for any decision, holds its own runtime, and
that runtime stands alone. The three prerequisite seams identified on 2026-08-17 are now
complete:

| | |
|---|---|
| `Output`/`Error` (11) | **Done in `TOAST-0015`.** Redirection targets Tōast streams |
| `Formatter` (4) | **Done in `TOAST-0014`.** Language rendering is independent of display profiles |
| `Invoker` | **Done 2026-08-24.** `IObjectInvoker` is the language contract; the .NET host supplies `ReflectionInvoker` |

**The step that made this real rather than structural was constructing `ToshEngine`
without a `ToshRuntime`.** This public API boundary landed on 2026-08-24 as an
additional `ToshEngine(ToastRuntime)` constructor, without constructing a hidden shell
runtime. The compatibility constructor remains for TōSh hosts while their shell-only call
sites are migrated.

An engine constructed from a `ToastRuntime` alone now evaluates an ordinary script, pinned
by `A_language_engine_runs_without_a_shell_runtime`, and invokes declared functions through
a command context whose language runtime is mandatory and shell runtime is optional, pinned
by `A_language_engine_invokes_a_declared_function_without_a_shell_runtime`. Shell-only
operations remain explicit capabilities and are the remaining assembly-division work,
rather than a prerequisite for constructing or running the language.

The last direct session-writer access left redirection on 2026-08-25.
`IToastSessionRedirection` accepts language-owned `IToastStream` destinations and returns a
scope; its embedded default is inert, while `ToshRuntime` adapts the streams for legacy shell
commands and external-process plumbing and restores the exact session writers on disposal.
An unhosted engine now executes an output redirection end to end, and hosted stdout/stderr
redirections are pinned to follow the scope and restore both session writers. Auto-help also
buffers into `LanguageRuntime.Output`, so `Tosh.Language` no longer reads or assigns
`ToshRuntime.Output` or `ToshRuntime.Error`.

Background pipelines crossed the boundary on 2026-08-25 as well. Tōast now submits a
`ToastBackgroundPipelineRequest` containing neutral process and redirection specifications
through the optional `IToastBackgroundJobHost`. TōSh translates that request into its
`ShellJob` model, allocates the session job ID, registers the job, and returns its
host-defined display value. A language-only host can supply another implementation without
constructing `ToshRuntime`, or leave the capability absent and receive a specific diagnostic.
No `ShellJob*` type or job-table operation remains in `Tosh.Language`.

The `$tosh` root crossed next. Tōast owns only the live `Script` and `Function` views;
it passes those through `IToastRuntimeNamespaceFactory`, and TōSh composes them with
its `Config`, `Last`, `Session` and `Host` views. The shell root and all four shell-only
children now compile from `Tosh.Runtime`, while a language-only host may inject its own
root or retain the empty embedded default. The first TōSh root is still published on
`ToshRuntime.RuntimeNamespace` for completion and introspection, and forked engines receive
their own evaluator-backed views without replacing it.

`$env` assignment no longer hides a concrete shell runtime inside
`ShellEnvironmentNamespace`. The namespace accepts an optional `IToastEnvironmentExporter`:
without one it updates the process environment directly, while TōSh supplies itself to
also track the name as exported and mirror its value into session variables. Hosted and
unhosted assignments are both pinned end to end.

AutoCd no longer implements a shell command inside the evaluator. Command resolution still
identifies that a name denotes a directory, then asks the optional
`IToastAutoCdCommandFactory` for the host's navigation command. TōSh owns directory-history
updates, event raising and the resulting `FileSystemEntry`; an embedded host can substitute
another command or receives a capability diagnostic. No evaluator command calls
`CommandContext.Runtime` now.

The runtime helpers shared by pipeline, aggregation, path and reflection commands also use
`CommandContext.LanguageRuntime` directly. None of those language-side utilities requires a
`ToshRuntime`; the remaining `CommandContext.Runtime` callers are commands in `Tosh.Stdlib`
and can now be divided according to the standard-library boundary in `TOAST-0007`.

That command migration has begun at its cleanest edge: the CLR command group now resolves
declared types and invokes or constructs values solely through `LanguageRuntime`. A test
registers its legacy `new` and `call` commands on a standalone `ToastRuntime` and runs the
pipeline without constructing a shell runtime.

The language-level `hash` path and `time` block execution follow the same boundary. `time`
also clones the caller's command context when forwarding instead of reconstructing a
shell-only context and dropping scoped services; its standalone-runtime block path is pinned.

Pipeline commands now take object access, nested evaluation, block execution, variables,
working directory and output from `LanguageRuntime`; `tee` renders invariantly with
`ToastRenderer` and writes through `IToastStream`. Only `inspect` retains a shell dependency
inside that category because its object inspector and inline prompt are presentation services.
A standalone `sort | tee | get` pipeline pins the language-only path.

Text commands likewise resolve file arguments against the language working directory.
`template` uses the language object accessor plus invariant rendering, while `write` and
`writeline` target the language stream. A standalone `template | writeline` pipeline is pinned;
only `wc`'s optional shell display-column override still needs a host presentation port.

The pure concurrency commands (`async`, `race`, `settle`, and `timeout`) obtain forkable block
execution from the command context or `LanguageRuntime`; a standalone `timeout` block is pinned.
`spawn` and `scope` deliberately remain shell-bound for now because they manipulate TōSh's
concrete job table rather than the neutral background-pipeline capability.

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
| `Output`/`Error` (16) | **The language emits values; the shell renders.** Scoped 2026-08-17 to the *session's* output only — see the note below, because "no streams in the language" was the wrong way to say it |
| `Events` (10) | **Language-side.** `event` is language syntax and the language calls `Register` and `MarkRequired`, not only `Raise`, so the bus goes with it |
| `ExitRequested` (4), `ExportedEnvironmentVariables` | **`IToastHostSignals`** — done. The language reads exit state and requests exit for script `--help`; export creation stays shell-side, while the language queries, synchronizes and removes names that the host has already exported |
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
  2c  Events onto ToastRuntime                       DONE 2026-08-17
  2d  ToastRuntime itself, composed into ToshRuntime the bulk of the 182
      IN PROGRESS — object access, type resolution and invocation are injectable host
      contracts; `ReflectionInvoker` now implements `IObjectInvoker`. `ToshEngine` exposes
      the composed `LanguageRuntime`, and every language-owned state/service access now
      routes through it rather than through a forwarding property on `ToshRuntime`. The
      existing host-signal and diagnostic-sink ports are also supplied on `ToastRuntime`,
      with inert defaults for a language-only host. Shell-session mirroring during
      redirection now uses the scoped `IToastSessionRedirection` port, and auto-help writes
      through the language stream. Background pipelines now use the optional
      `IToastBackgroundJobHost` port and language-neutral request records. `$tosh` is now
      composed through `IToastRuntimeNamespaceFactory`, with only the script/function
      views left in the language assembly. `$env` export bookkeeping now uses the optional
      `IToastEnvironmentExporter`, and AutoCd command creation uses
      `IToastAutoCdCommandFactory`. The remaining compatibility use of `ToshRuntime` is the
      shell constructor/fork and its guarded `Runtime` property; that adapter must move or
      disappear before the physical assembly split. `CommandContext`
      now requires a language runtime and carries
      the shell runtime only when one exists, so declared functions run unhosted. The
      host-signal port now
      describes the language's real environment duties—synchronize an already-exported
      global and remove it on `forget`—rather than the stale claim that it only queried
      membership. Result and exit-code updates now go through an
      `IToastExecutionObserver`; the unhosted default ignores them and TōSh retains them
      for `$tosh.Last`. Script invocation arguments and the evaluator/block
      callbacks exposed to host commands now live on `ToastRuntime`; `ToshRuntime` forwards
      the same slots. External-process construction is
      now an optional host capability on `ToastRuntime`, still supplied by TōSh's existing
      `IExternalCommandFactory`
  2e  Streams: emit rather than write                DONE as far as it goes
      diagnostics + trace moved; value formatting -> TOAST-0014 (Phase A);
      redirection retarget -> TOAST-0015 (Phase A, needs the stream handle)
  2f  CurrentDirectory per evaluation                DEFERRED 2026-08-17
      Measured: none of the 15 sites has a context parameter and only two have
      even a CancellationToken, so "pass it per evaluation" means adding a
      parameter to a dozen synchronous helpers deep in the engine, with no
      useful half-way state — two sources of truth is worse than either end.
      The benefit chosen for it, concurrent evaluations with different working
      directories, is real but future-facing and unexercised today. Held on
      ToastRuntime for now; revisit when concurrent evaluation is on the table,
      at which point the context probably threads alongside something else
      rather than alone.

2e and 2f are the two that deserve their own scrutiny, and both are better done after
2d, when there is a `ToastRuntime` for the new shapes to live on.

## Notes

Depends on `TOAST-0004`; the boundary inversion has to land first or this becomes
untangling rather than moving.

The real test of this arrives with `TOSH-0003`: a machine with Tōast installed and
TōSh absent must still run a script. Until that works, the boundary is a directory
layout rather than a dependency.
