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

## Before starting

One question is still open: **does the compiled backend stay in scope?** It is
currently deferred on the board as an experiment, it shares the parse tree, and a
bound-tree evaluator changes its relationship to the interpreter. Deciding that first
avoids designing the evaluator twice.
