# Tōast / TōSh — separation, organisation, and the evaluator

A plan for three efforts that touch the same code, written 2026-08-16.

## Decisions taken

- **The language is Tōast.** Everything else in the suite is *Toasted* — ToastedWM,
  ToastedShell — which are things made *from* something. The language is the thing
  they are made from, so it takes the bare noun. The macron ties it to TōSh and Tōme.
- **TōSh remains the shell**, and becomes *a* host for Tōast rather than the thing
  Tōast lives inside.
- **Source files become `.toast`**, with `.tosh` kept as a recognised alias so no
  existing script breaks.
- **Sequence: organisation and separation together, then the evaluator.**

Suffix forms (`Tōast#`, `Tōast++`, `Tōast.NET`) were considered and rejected. `#` is
the language's own comment character, which would collide in docs, code fences and
filenames. `++` implies a plain predecessor. `.NET` contradicts the roadmap already on
the board — `TS-P3-15` through `TS-P3-22` describe a `no_clr` subset, Tōast-owned core
types, a native regex engine and a backend emitting C, all of which would date a name
tied to the CLR.

## Why this sequence

Organisation and separation are **the same motion**. Splitting `ToshEngine.cs` means
deciding, statement by statement, which pile each piece belongs in — and that decision
*is* the language/shell boundary. Doing them separately means touching the same code
twice and making the boundary call twice.

The evaluator rewrite is a **different kind of change**: it rewrites
semantics-carrying code. It wants small reviewable files and a settled boundary to
land against. Done during a reorganisation, it would be a behavioural change hidden
inside a large mechanical diff — the hardest thing to review and the easiest place for
a regression to survive.

---

## Phase A — Organisation and separation

### A1. The boundary is one type at two sites

Measured, not assumed: deleting `using Tosh.Stdlib;` from `ToshEngine.cs` and
compiling produces **two errors, both for `ExternalProcessCommand`** — one where a
resolved name turns out to be a program on `PATH`, one where `&` requires the stage to
be external. `ExternalCommandLookupStatus` already lives in the runtime, so the
*lookup* is abstracted; only construction and a type test are not.

Invert those two through an interface the shell registers, and `Tosh.Language` drops
its dependency on the shell's command library entirely. **This is the whole hard
coupling.** Do it first: it is small, it is verifiable by the reference disappearing
from the `.csproj`, and it makes every later step a matter of moving files rather than
untangling logic.

### A2. The monoliths

| File | Lines |
|---|---:|
| `ToshEngine.cs` | 19,318 |
| `ToshParser.cs` | 13,931 |
| `BuiltInDisplayProfiles.cs` | 6,501 |
| `DisplayEngine.cs` | 4,308 |
| `ToshClassDefinition.cs` | 3,769 |
| `ToshLanguageFeatures.cs` | 3,363 |
| `ToshHost.cs` | 3,042 |

The first two are two-thirds of the language project. Split into partial classes by
concern — statements, expressions, declarations, classes, native interop, diagnostics
— with **no behavioural change in this phase**. A partial-class split is verifiable:
the suite must pass unchanged, and every moved method should move *verbatim*.

The discipline that makes this safe is refusing to fix anything while moving it. A
defect noticed during the split gets filed, not fixed, so that a behavioural change
never hides inside a 19,000-line diff.

### A3. Assembly layout

| Tōast (the language) | TōSh (the shell) |
|---|---|
| lexer, parser, binder, lowerer, evaluator | REPL, line editor, prompt, job control |
| type system — refinements, generics, traits | display engine, themes, profiles |
| value model — quantities, complex, vectors | help catalog, config browser |
| FFI and CLR interop | external processes, TSSP |
| diagnostics infrastructure | packaging, publishing |

`Tosh.Runtime` (56,373 lines) is the real work here, not `Tosh.Language`. It currently
holds the value model *and* the display engine *and* the help catalog *and* command
metadata. The split runs through it, not around it.

### A4. The standard library

`Tosh.Stdlib` is already grouped by category, which makes this unusually tractable:

| Language-level (moves to Tōast) | Shell-level (stays TōSh) |
|---|---:|
| Pipeline (4,327), Clr (2,213), Text (1,748) | Filesystem (6,279), Sys (4,203) |
| Concurrency (1,045), Functional (648) | Shell (3,754), Net (1,931) |
| Time (615), Data (507), Maths | Processes (1,636), Display, Tssp |

`map`, `where`, `count` and `sort` are as much part of Tōast as `for` is. `ls`, `ps`
and `systemctl` are not.

### A5. Rename mechanics — the measured surface

| Surface | Count | Recommendation |
|---|---:|---|
| Assemblies `Tosh.*` | 18 | Rename in one commit; mechanical |
| Diagnostic codes `tosh.*` | 534 | See below |
| `$tosh.` in docs, examples, libraries | 131 | Alias `$toast`, keep `$tosh` working |
| `.tosh` source files | 51 | Both extensions recognised |

**Diagnostic codes need a decision.** They are already namespaced by area —
`tosh.parser.*`, `tosh.type.*`, `tosh.bind.*`, `tosh.native.*`, `tosh.runtime.*` —
and those areas map almost exactly onto the language/shell split. The mechanical move
is `tosh.<area>` → `toast.<area>` for language areas, leaving shell diagnostics as
`tosh.*`. The cost is that every `hush` directive naming a language code changes;
mitigate by having `hush` accept both spellings for a release, and by regenerating
`docs/diagnostic-codes.md` from the manifest as it already is.

**The parity tripwires will fire, and that is correct.** `EditorSurfaceParityTests`,
`LanguageSurfaceParityTests` and `SyncAsyncTwinInventoryTests` all encode current
names. They are the checklist for the rename, not an obstacle to it.

---

## Phase B — The evaluator

### The evidence

Measured this session with `GC.GetTotalAllocatedBytes`:

| Shape | Before | After the fast path |
|---|---:|---:|
| empty `for` iteration | 2,797 B | 2,187 B |
| `$s = ($t)` | 8,034 B | 2,825 B |
| `$s = ($t + 1)` | 11,146 B | 6,057 B |

The cause is not that evaluation is async. It is that **`EvaluateArgumentAsync` is one
`async` method handling thirty-nine node shapes**, so its state-machine box carries
the locals of every branch — about **2,545 bytes per entry, whatever the expression
was**. A literal paid for the largest case in the switch.

The synchronous pre-dispatch added this session is a workaround: it keeps the simple
shapes out of that method. It cannot be extended indefinitely, because every shape
added is a second copy of semantics that must be kept in step — the `TS-P1-24` failure
mode, and the reason two further shapes were declined.

### The structural answer already exists

`Lowerer` produces a `BoundUnit`; the engine runs it for its side effects and
**discards it**, with a comment saying evaluation will route through it. That is the
rewrite: a bound-tree evaluator where each node type carries its own small evaluate
method. Dispatch becomes a virtual call rather than a thirty-nine-case switch, each
state machine is small, and shapes that cannot suspend are genuinely synchronous
rather than special-cased.

Two things make it more feasible than it sounds: the IR exists (`Tosh.Compiler.IR`),
and `TS-P3-23`'s differential corpus already compares interpreted against compiled
results — the harness this work wants.

### What it must not break

Each of these has a scar on the board:

- **Streaming laziness** — `TS-P2-113` (a collection expanded one level too far) and
  `TS-P2-89` (`defer` forcing buffering).
- **Generators** — `yield` depends on the async-iterator machinery directly.
- **Cancellation**, threaded through every call today.
- **Native callback re-entrancy** — `NativeCallbackScope` exists because a callback
  cannot throw across the C frames it runs on.
- **The compiled backend**, which shares the parse tree.

### Expected result

Getting within 5–10× of CPython on a tight loop seems plausible. Parity does not:
CPython interns small integers and runs a purpose-built bytecode loop. Saying so now
avoids the work being judged against a target it was never going to reach.

---

## Carried items

- **Member-access fast path.** Safe today and small. `ResolveSegmentAsync` is an
  *async prefix over a shared core* — it handles the one genuinely async case
  (`IShellRecordObject.TryGetMemberAsync`) and then calls the synchronous
  `ResolveSegment`. When the target is not an `IShellRecordObject`, the async path
  provably *is* the sync path, so routing there is a shortcut into the same
  implementation rather than a second copy. Declining it earlier was pattern-matching
  on "there is a sync twin" instead of reading it.
- **Equality convergence.** `OperatorEvaluator.AreEqual` and `ToshEngine.AreEqualAsync`
  are 108 and 114 structurally parallel lines sharing only `TryCompareByName`, and
  this is the pair that has already diverged twice (`TS-P1-14`, `TS-P1-15`). They live
  in different types, so the name-based twin discovery does not even pair them.
  **Guard first, converge second**: add parity assertions driving both surfaces over
  the same value pairs — records, collections, enums, cross-type coercions — because
  given the history, agreement today should not be assumed. If they already disagree,
  that is a defect to find before deciding how much refactoring it deserves.

---

## Risks

- **The rename touching everything at once.** Mitigate by doing the boundary inversion
  (A1) *before* any rename, so the rename is a pure find-and-replace over a tree that
  already compiles in the target shape.
- **A behavioural change hiding in a mechanical diff.** Mitigate by the file-split
  discipline above: move verbatim, file what you notice, fix nothing in flight.
- **The evaluator rewrite being judged by the wrong benchmark.** Two of the standing
  benchmarks are built from `$x += 1` and `$x == N` and did not move at all for a
  change that was worth 25% on expressions. Benchmarks must be chosen per change, and
  compared A/B against a worktree build — cross-run comparison produced a false
  regression twice this session.

## Phase 0 — Decisions now settled

- **The compiled backend freezes out of the solution** (option C). `Tosh.Compiler`
  and `Tosh.Compiler.Runtime` leave `Tosh.slnx` and stay in the tree. The IR stays in
  the build, because the evaluator wants it. A compiler gets written again later
  against the new bound tree, where it can share the interpreter's decisions instead
  of re-deriving them.
- **`.toshproj` packages and runs interpreted** when the emitter is gone. That is what
  it does at runtime anyway; only the MSBuild `ToshCompile` step goes.
- **Diagnostic codes take one prefix, `toast.*`**, with `tosh.*` accepted by `hush`
  indefinitely. A two-prefix split was considered and rejected: `tosh.runtime.*` is
  **327 of 534 codes** and is the genuinely mixed bucket, holding
  `annotation_unknown_type` beside `unknown_command`. Splitting means hand-judging 61%
  of the codes with no mechanical rule, to correctly place the **fourteen** that are
  unambiguously shell-only (`tui`, `edit`, `config`, `history`, `help`). Move those
  fourteen later if it ever matters.

## Freezing the compiler costs ~10,000 lines of tests — triage them first

Eight test files touch the compiler, **10,024 lines** in total, plus the
`Tosh.ParityCheck` tool. They do not all belong to the emitter:

| Freeze with the emitter | Keep in the build |
|---|---|
| `BoundUnitEmitterTests` (3,351) | `ConstantFoldingTests` — tests the *lowerer* |
| `CompiledDeferSemanticsTests` | `MemberCheckSoundnessTests` — tests the *binder* |
| `DifferentialExecutionTests` (compiled half) | `ChainedComparisonTests` — language semantics |

Triage before freezing, or ~5,000 lines of tests for code that is staying will be
switched off by accident.

**The differential harness needs replacing, not mourning.** `TS-P3-23` compares
interpreted against compiled; freezing removes its second side. The evaluator rewrite
wants a *new* differential — old evaluator against new — which is a better fit anyway,
since both sides are then present and expected to agree exactly.

## The specification is its own phase

`docs/spec/toastscript-spec.tex` is **7,010 lines across 27 chapters** and currently
documents the language and the shell together. Separating it means deciding, chapter
by chapter, which document each belongs to, and it is the user-facing definition of
Tōast — it cannot lag behind the code. Budget it as real work rather than a
find-and-replace, and give it its own phase.

## Identity is renamed; paths are not

A brand change must not break working machines. Explicitly **not** renamed:

- `~/.config/tosh` — configs, profiles and libraries live there
- the `tosh` binary and the Arch package — TōSh keeps its own name regardless
- `$tosh.` in scripts — `$toast` becomes the preferred spelling, both keep working

Rename for identity. Do not rename what someone has already typed into a file on
their machine.

### `~/.config/tosh` belongs to the shell alone

Not merely un-renamed — **out of scope for the language entirely**. Nothing in that
directory may affect Tōast: no settings, no profile, no library path, nothing. It
configures TōSh, and a Tōast embedded in some other host must behave identically
whether or not that directory exists.

This is an invariant to hold, not a migration to perform. It already holds:
`Tosh.Language` contains **zero** reads of a config directory — the single grep hit is
the string `"source ~/.config/tosh/profile.tosh"` inside a `[CommandExample]`
attribute, which is documentation text. The config-reading code lives in
`Tosh.Runtime` (4 files), `Tosh.Stdlib` (3) and `Tosh.Cli` (1), all of which are shell
side or split by A3 anyway.

Worth a guard test rather than trust, because this is the kind of coupling that
arrives by convenience — one lookup of a library path from the language layer and it is
gone. The cheap form is an assertion that the language assembly never resolves a path
under `~/.config`.

**A boundary question this turns up.** `src/Tosh.Language/Bridge/Shell/` holds three
commands — `EvalCommand`, `DebugCommand`, `SourceCommand`. The first two are
language-level and belong where they are. `source` runs a file into the current
session, which is a shell verb; it is also the one that names the config directory.
Decide it during A1 while the boundary is being drawn, not later.

## Packaging: Tōast is its own package

Tōast ships as a package in its own right, and **TōSh, Tōme and Crumb depend on it**
rather than each vendoring a copy. That is the packaging expression of the same
boundary A3 draws in the assemblies — if the split is real, the language is
installable without the shell.

This gives the separation a test no amount of file-moving can fake: a machine with
Tōast installed and TōSh absent must still run a `.toast` script. Until that works, the
boundary is a directory layout rather than a dependency.

The Arch packaging under `packaging/archlinux/` currently builds one `tosh` package.
Splitting it into `toast` plus a `tosh` that depends on it is the concrete deliverable,
and it wants doing *after* A3 settles which assemblies land on which side — otherwise
the package split has to be redone when an assembly moves.

---

## Should this be a new solution?

**No — refactor in place, on a branch.** The instinct is understandable and the
evidence does not support it.

**The fear driving it is already solved.** TōSh is the logon shell, and the worry is
breaking it. But `/usr/bin/tosh` is a *published artefact*: it changes when
`buildtosh publish` runs, not when the working tree changes. The tree can be broken
for weeks without the shell noticing. That safety already exists.

**Measure the mess before deciding it is a mess.** Of ~260,000 lines: two files are
monolithic (`ToshEngine.cs` 19,318 and `ToshParser.cs` 13,931 — 13% of the tree), the
docs have drifted, and three stale cache files are tracked. Everything else is in the
solution, builds, and is covered by **5,324 passing tests**. A codebase whose entire
language-to-shell coupling is *one type at two call sites* is not architecturally
dirty. It is a tidy-up, not a teardown.

**The tests are the argument.** 5,324 of them encode behaviour that was expensive to
learn — this session alone produced pins for pipeline expansion, top-level `defer`,
unary statements, interpolation caching and literal widths, each subtle and each found
the hard way. In-place refactoring keeps that suite green at *every step*. A greenfield
has no green suite until the end, which converts a continuously verified migration into
one big-bang integration.

**"Selectively copy what we need" is a refactor with the history deleted.** If the code
is copied, it is the same code — minus `git blame`, minus the commit that explains why
a branch exists. This codebase leans on that heavily: its comments record *why*, and
reading them was the only way several decisions this session were made correctly.

**Where a rewrite is right, do it as a replacement inside the working tree.** The
evaluator genuinely deserves rewriting — that is Phase B — but as an isolated component
swapped in behind a differential harness, not as a new solution around it.

**To get the clean-slate ergonomics safely:** `git worktree add ../toast-refactor
refactor/toast-split`. A separate directory to work in, the main tree untouched, full
history, one command to throw away. And use `git mv` for the moves so history and blame
follow the files.

---

## Documentation triage

22 documents, ~17,000 lines, last touched between April and today. The rule that makes
this tractable: **a document is either generated, live, or history — and history should
say so in its first line.**

| Class | Documents | Action |
|---|---|---|
| **Generated** | `diagnostic-codes.md`, the command reference | Leave alone; regenerated by `buildtosh spec` |
| **Live** | `plan/` (the item boards), `TOAST_SEPARATION_PLAN.md`, `SPEC_STATUS.md` | Keep current through the work |
| **Needs rewriting** | `ARCHITECTURE.md` | Known stale — still describes `Tosh.Core` as present four months after `9d5b852` deleted it. Rewrite around the new boundary |
| **Freeze with the compiler** | `COMPILED_TOSH.md` (1,023) | Add a header saying it describes a frozen component |
| ~~**Decide: merge or archive**~~ | Resolved 2026-08-16 | Both dissolved into `plan/`; live work filed as items, remainder archived, speculation moved to `IDEAS.md` |
| **Settled RFCs** | `BRACE_DISAMBIGUATION_RFC`, `LINE_EDITOR_RFC`, `SELF_HOSTING_RFC` | Mark decided-and-implemented, or move to an `rfc/` directory |
| **Verify then keep** | `CONFIGURATION`, `EDITOR_SUPPORT`, `TSSP`, `CLR_ABI_v1`, `RUNTIME_NAMESPACES` | Read once against the code; correct or date-stamp |
| **Stale, low risk** | `BENCHMARKS` (April, 100 lines), `TUI_ARCHITECTURE`, `TOME` | Re-measure or mark as of-its-date |

The lesson worth encoding: `ARCHITECTURE.md` was written carefully and still drifted
within four months. **Whatever survives this triage needs an owner and a trigger** —
"update when the solution layout changes" — or the same thing happens again.

## Housekeeping found in the sweep

- ~~**Three tracked `.lscache` files.**~~ Resolved — no `.lscache` file is tracked at
  `HEAD`. Recorded here as an example of the failure mode rather than as work: they
  were committed before `*.lscache` was ignored, and an ignore rule does not untrack.
- **History carried 1.8 GB of build output**, found when a push was rejected for a
  418 MB AppImage. Stripped 2026-08-16 with `git filter-repo`; `.git` went 1,400 MB →
  16 MB with all 362 commits and 5,324 passing tests intact. The rules that would have
  prevented it are in `d153eee`. The rendered spec PDFs are now build output rather
  than tracked files — `docs/spec/*.tex` is the source and `buildtosh spec` regenerates
  them.
- **`Tosh.Dap`** (550 lines) builds and is in the solution. **Correction:** it is not
  referenced by nothing — `ProtocolSmokeTests` starts a server and drives the
  initialize handshake, and `Tosh.Tests.csproj` carries a project reference, so it is
  exercised on every suite run. What it lacks is a *product* caller, which is a
  different thing and a much weaker argument for removing it. Labelled
  dormant-by-intent in `ToshDapServer.cs` on 2026-08-16.
- **Test monoliths too**: `EngineTests.cs` (5,912) and `LanguageFeatureTests.cs`
  (2,841). Splitting them gives a second, independent read on the boundary being drawn
  in `src/`.
