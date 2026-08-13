# Self-Hosting ToastScript — Design Notes

**Status:** Exploratory discussion. Not scheduled. Assumes TōSh/ToastScript is
first finished and polished.
**Started:** 2026-08-13

## The proposal, as stated

Rewrite the core of ToastScript in a lower-level language (C, C++, Rust, or
assembly), write a lexer/parser/compiler alongside it, then use that toolchain
to rewrite the language in itself — making ToastScript a self-hosted, "native"
language. **Full .NET compatibility must be retained.** Compiled language
first, then self-hosting, then rebuild the tools (shell, MCP, LSP, DAP).

## Positions reached

- **Seam A was chosen out of .NET familiarity, and is withdrawn.** A compiler
  flag cannot paper over semantic divergence; the conflict is behavioural, not
  nominal.
- **Dynamic vs strict typing should be two explicit modes behind a flag** — this
  was the original motivation for the compiler, and it is also the gate on
  native mode.
- **Whether the shell carries CLR objects or ToastScript objects is unresolved,
  and is the one genuinely open question.** The CLR is expansive and already
  known by millions; owning the objects gives finer control and a native path.
  See "deferring the object-model decision" below — it does not have to be
  answered now, provided the core types are *specified* in ToastScript's terms
  starting now.

## Answers to the follow-up questions

### A. Can a compiler written in ToastScript emit real, non-CLR native binaries?

**Yes.** A compiler's implementation language is independent of its target — it
is a program that reads text and writes bytes. TypeScript's compiler is written
in TypeScript and emits JavaScript; rustc is Rust emitting machine code.

Practical route: **emit C and invoke the system compiler.** That avoids writing
ELF/Mach-O/PE object writers, relocations, DWARF and a linker, and inherits
every platform's optimiser. Nim and Vala both ship this way.

The bootstrap then removes .NET from the build too:

- **Stage 0** — ToastScript compiler, written in ToastScript, running on the
  CLR, emitting C → native.
- **Stage 1** — it compiles itself, producing a native compiler binary that
  needs no .NET.
- **Stage 2** — the native binary compiles itself; assert stage 2 is identical
  to stage 1.

### B. A shell with no .NET startup cost that still carries .NET objects?

**No — those are mutually exclusive, and the earlier wording should not be read
as saying otherwise.** If the pipeline holds CLR objects, a CLR is running in
the process, and .NET startup *is* the cost of getting one running. A natively
compiled shell would not carry CLR objects.

What actually fixes the pain:

- **Interactive startup is paid once per terminal.** Laziness recovers most of
  it (see the measurements) — this is not the real problem.
- **Scripted invocation is the real problem**: prompts, hooks and subshells pay
  ~120 ms *every* call. The fix is a **resident server plus a thin client** — a
  long-lived tosh process holding a warm CLR, and a tiny native client that
  connects over a unix socket, turning ~120 ms into ~1 ms. This is what nailgun
  does for the JVM and what `dotnet build-server` does for Roslyn.
- **ReadyToRun** would cut JIT time from the floor without breaking reflection,
  unlike NativeAOT.

The thin client is small enough to be ~100 lines of C. It could be written in
ToastScript only once native mode exists.

Hard part for an interactive shell specifically: handing the pty to the server,
plus cwd/env propagation, isolation and daemon lifetime.

### C. Two separate products, or one language with two targets?

**One language, two targets, one front end.** Splitting into "Native
ToastScript" and "T#" means two stdlibs, two doc sets, two test suites and two
communities, permanently — and for a single maintainer that is the more likely
failure mode than any technical risk. It also destroys the strongest story the
project has: *the same language scripts your shell and compiles to a binary*.

The instinct behind the question is still right, though: the two targets **will**
have different capability sets, and that should be explicit and named. The model
to copy is Rust's `no_std` — native mode is **`no_clr`**, a restricted profile of
one language, not a different language.

Naming them as separate languages (T#, ToastScript.NET) makes a promise that
cannot be walked back; naming them as profiles stays reversible.

### Deferring the object-model decision safely

The lock-in is not "which objects the pipeline carries today" — it is **whether
scripts and docs depend on CLR-specific semantics**. If the stdlib and
specification are written against ToastScript-defined types (`str`, `list`,
`date`), the underlying implementation can be the BCL now and something native
later. If they are written against `System.String` and `List<T>`, the choice is
already made.

**Actionable now, cheap now:** specify the core types in ToastScript's own terms
while continuing to implement them with the BCL. That preserves the option
without costing a rewrite.

## The target design, stated plainly

ToastScript owns itself. It has its own object model and its own core types, and
**two peer foreign-function interfaces**:

- a **C ABI** for native interop, and
- a **.NET bridge** for CLR interop,

both seamless, neither privileged. TōSh and ToastScript are written in
ToastScript. Cross-platform.

This is Seam B. The important consequence is that **.NET stops being *the* world
and becomes *a* world** — one of two foreign systems the language talks to.

**Both FFIs already exist.** `bind native` / `raw struct` / `raw callback` is a
real C ABI binding layer with calling conventions, struct layout, buffers and
callbacks; the CLR bridge is mature. The missing piece is not either FFI — it is
**ToastScript's own object model in the middle of them**. Today the CLR object
model occupies that position, which is exactly why .NET cannot currently be
demoted to a peer.

### Cross-platform consequences

- **Reinforces "emit C".** C compilers exist on every target. LLVM needs
  per-platform plumbing; hand-written codegen needs a backend per architecture.
- **The C ABI layer needs a portability story**: `libc.so.6` vs
  `libSystem.dylib` vs `msvcrt.dll`, and library-name resolution generally. The
  calling-convention support (`cdecl`/`stdcall`/`winapi`) shows the design
  already anticipates this.
- **The language will be portable long before the shell is.** The spec currently
  describes TōSh as Linux-native, and the shell leans on POSIX throughout —
  ptys, signals, `umask`, ownership. Windows needs ConPTY and much else. Expect
  ToastScript-the-language and TōSh-the-shell to reach cross-platform at
  different times, and plan them as separate milestones.
- Boehm GC is cross-platform, so the GC choice does not conflict.

## What self-hosting actually requires

### 1. The compiler must be *compiled*, not interpreted

This is the critical path and it is easy to miss. A compiler is an
allocation-heavy tree-walker; 50k lines of ToastScript running under the
interpreter would be far too slow to use. So the existing compiler has to be
able to compile the ToastScript-written compiler.

**The readiness dashboard already exists.**
`tests/Tosh.Tests/CompilerFeatureMatrixTests.cs` records, per feature, whether it
reaches Tier 1 (pure IL), Tier 2 (runtime-hosted) or Tier 3 (source replay) —
and its own summary calls it "a baseline, not an aspirational test", updated as
features move up.

So **"self-hosting readiness" has a precise definition**: every feature the
compiler-writing subset uses must be out of Tier 3. That is measurable today.

### 2. Language gaps that block writing a compiler at all

- **Interfaces and traits cannot be used as type annotations** (`TS-P2-99`).
  `func visit(node: AstNode)` is the shape of every compiler pass.
- **`&` cannot reference a method** (`TS-P2-94`) and **a callable in a property
  cannot be invoked directly** (`TS-P2-93`). Between them, dispatch tables and
  visitor patterns — a compiler's bread and butter — are awkward.
- **A class cannot name itself in a return annotation** (`TS-P2-106`). Static
  factories on AST nodes are ubiquitous.
- **No bitwise operators** (`TS-P3-14`). Flag sets and hashing.
- Recursion depth and stability under deep ASTs needs verifying.

### 3. Daily papercuts that become a tax at 50k lines

`TS-P2-89` (top-level `defer`), `TS-P2-92` (`T.Prop.Method()`), `TS-P2-98`
(unqualified refinement types), `TS-P2-103` (`shy shared func`), `TS-P2-104`
(splat), `TS-P2-105` (`as` precedence). None individually blocking; collectively
they decide whether writing the compiler is pleasant or miserable.

### 4. Tooling and verification

A debugger that works on compiled ToastScript (`Tosh.Dap` exists), and the
differential interpreted-vs-compiled corpus already described in the test
strategy — which becomes the mechanism that keeps two backends honest later.

## Proposed next step: measure readiness instead of estimating it

Write a **compiler-shaped probe** in ToastScript — a tokeniser, recursive-descent
parser, AST, and a visitor pass over a small expression language, in roughly
300 lines. It is deliberately the shape of the real thing.

It answers three questions at once:

1. Which papercuts actually bite when writing compiler-shaped code?
2. What tier does it compile to — or does it fall to Tier 3 source replay?
3. How fast is it, interpreted vs compiled?

That converts "is the language ready to host itself" from a judgement call into
a measurement, which is how everything else in this session has gone.

## Probe results, 2026-08-13

A ~370-line compiler-shaped probe was written in ToastScript: lexer, token
type, five-node AST hierarchy under a `Node` base class, recursive-descent
parser with precedence levels, and three visitor passes (evaluate, print,
count), plus custom `Error` subclasses for lex and parse failures.

**Interpreted, it works and is correct.** Operator precedence, unary minus,
parenthesisation and nested `let` scoping all produce the right answers:

```
1 + 2 * 3                              -> 7    (1 + (2 * 3))
(1 + 2) * 3                            -> 9    ((1 + 2) * 3)
-x + y * 2                             -> -2   ((-x) + (y * 2))
let a = x * 2 in a + y                 -> 24
let a = 1 in let b = 2 in (a + b) * x  -> 30
```

So the language can *express* a compiler today. Class hierarchies, `match` on
node type, recursion, custom diagnostics and primary constructors all behave.

**Compiled, it does not build.** Three checker gaps stop it, and all three are
precisely what compiler code does:

1. **Untyped locals are rejected** (`tosh.compile.implicit_dynamic`). The
   compiler enforces annotations — which is the strict mode already envisaged,
   working as intended. `--compile-allow-dynamic` relaxes it.
2. **A subclass is not accepted where its base is declared** (`TS-P2-107`).
   `return new LetNode(…)` from `-> Node` is a type mismatch, even for a
   subclass adding nothing. The expression-bodied form does not warn; the
   `return` form does.
3. **`match` arms do not narrow** (`TS-P2-108`). `_ is LetNode => $node.Value`
   reports `Member 'Value' was not found on type 'Node'`. The probe produced
   **26 instances of this single error**. Notably `if ($n is Leaf) { $n.V }`
   *does* narrow — so the checker is inconsistent between the two forms, and an
   `if`-chain visitor is a viable if ugly workaround.

**The encouraging part: these are checker bugs, not semantic ones.** The
runtime does the right thing in every case — dispatch, narrowing and
inheritance all behave correctly at execution time. The language design is not
in question; the type checker has to catch up with it. That is a far better
result than discovering the object model or dispatch semantics were wrong.

**Consequence for sequencing.** `TS-P2-107` and `TS-P2-108` are the first two
hard prerequisites for self-hosting, ahead of any backend work — without them
no compiler-shaped program compiles at all, so the interpreted-vs-compiled
performance question cannot even be asked yet.

## Can the compiler emit native code today? No.

`--compile` produces:

| artefact | what it is |
|---|---|
| `out.dll` | PE32 .NET assembly — IL |
| `out` | the stock .NET **apphost**: an ELF stub linking `libstdc++`/`libm`/`hostfxr` that boots the CLR. Contains none of the program. |
| `out.deps.json` | framework resolution data |

There is no NativeAOT wiring anywhere in the tree.

**The compiled output also still calls the engine at runtime.** A compiled binary
faulted with a stack through `Tosh.Language.ToshEngine.ConvertAnnotatedValue` —
so compiled ToastScript needs the whole tosh runtime, not merely .NET.

### The tier system answers "what would it take" precisely

| tier | meaning | retargetable to native? |
|---|---|---|
| **Tier 1** | pure IL, no `ToshHost` call at runtime | **yes** |
| **Tier 2** | runtime-hosted; calls `ToshHost`/`ToshEngine` | only by porting the engine |
| **Tier 3** | source replay — re-runs the interpreter | needs the entire engine |

So the requirement is **not** "write a native backend". It is **move the target
subset from Tier 2/3 down to Tier 1**, which is language work, not backend work.
Per the feature matrix, much sits at Tier 2 today: builtin command dispatch,
regex literals, redirections, annotated variable writes, refinement
conversions, `async`/`await`, `require`.

Ordered requirements:

1. `TS-P2-107` / `TS-P2-108` — no compiler-shaped program compiles at all today.
2. Push the target subset from Tier 2/3 to Tier 1. **The bulk of the work.**
3. A native runtime: GC, `str`/`list`/`dict`, the Native Core Library.
4. A backend emitting C. Genuinely the smallest of the four.

## "Doesn't our own runtime just recreate the startup cost?"

**No — and the distinction matters.** The CLR's ~120 ms is not the price of
*having* a runtime; it is the price of a specific set of activities:

- `hostfxr`/`hostpolicy` resolving the framework, reading `runtimeconfig.json`
  and `.deps.json`, probing paths
- mapping several megabytes of `libcoreclr`
- type-system and AppDomain initialisation
- assembly loading and metadata parsing, per assembly
- **JIT-compiling everything on the startup path** — the dominant term

A native runtime does none of it. The code is already machine code, there is no
metadata to parse, no framework to resolve, and the "runtime" is a statically
linked library — an allocator, a GC, and the core type implementations.

For scale: Go ships a substantial runtime (GC *and* a scheduler) and its
binaries start in 1–2 ms. Boehm GC initialisation is microseconds.

**"Runtime" is not "virtual machine."** The only way to reintroduce the cost is
to make the native runtime dynamically load things at startup — keep it
statically linked and lazy and it stays in the single-digit milliseconds.

## Finish all of stabilisation first, or only what is needed?

**Only what is needed — but the board is not currently organised to say which
items those are.** The recommendation is to partition it into three buckets:

1. **Blocks self-hosting** — type checker, compiled-mode tiers, differential
   interpreted-vs-compiled correctness, core language semantics. Must be done:
   writing 50k lines in the language means every papercut compounds, and once
   self-hosted, fixing a language bug requires the language.
2. **Blocks going native** — Tier 1 coverage for the target subset, and the
   definition of the no-CLR subset.
3. **Neither** — shell UX, TUI, REPL, editor and LSP polish. Genuinely
   deferrable; none of it constrains the language core.

Finish 1 and 2; defer 3. Note that `TS-P2-107`, `TS-P2-108` and `TS-P2-109` all
landed in the type checker, discovered by the first serious attempt to write
compiler-shaped code — which suggests that neighbourhood is under-tested and
likely holds more.

**A better gate than "the board is empty":** the probe compiles to Tier 1 and
runs fast. That is a measurable acceptance test for "ready to self-host", and it
is the one that actually matters.

## Board triage, 2026-08-13

45 open rows, of which 5 are withdrawn, superseded or resolved-as-not-recommended
(`TS-P1-17`, `TS-P1-36`, `TS-P2-46`, `TS-P2-57`, `TS-P2-11`). ~40 genuinely open.

### Bucket 1 — blocks self-hosting (~20 items)

**Critical — nothing compiler-shaped compiles until these land:**

| id | why it blocks |
|---|---|
| `TS-P2-107` | subclass not assignable to base — an AST hierarchy cannot be typed |
| `TS-P2-108` | `match` arms do not narrow — every visitor pass fails to check |
| `TS-P2-109` | interpreted/compiled divergence on the same program |

**Type-system soundness — a compiler stresses exactly this area:**
`TS-P2-87` (rebound variable keeps its first inferred type), `TS-P2-99`
(interfaces unusable as annotations), `TS-P2-85` (computed property on a
struct), `TS-P2-106` (class cannot name itself in its own return annotation).

**Dispatch and higher-order code — visitors and dispatch tables:**
`TS-P2-93` (callable in a property), `TS-P2-94` (`&` on a method),
`TS-P2-92` (`T.Prop.Method()`), `TS-P2-103` (`shy shared func`).

**Backend agreement — the discipline that makes multiple backends survivable:**
`TS-P1-13` (compiled vs interpreted assignment order), `TS-P1-40` (two live
index-assignment implementations).

**Ergonomics that compound at 50k lines:**
`TS-P2-89`, `TS-P2-98`, `TS-P2-104`, `TS-P2-105`, `TS-P2-91`, `TS-P3-14`
(bitwise — flag sets and hashing), `TS-P3-05` (thrown-value protocol),
`TS-P3-02` (`let` bindings), `TS-P3-04` (explicit stream/collection shape —
compiler code wants lists, not streams).

### Bucket 2 — blocks going native (~4 items, and a gap)

`TS-P2-90` (native export tables shared per library path — the C ABI is a peer
FFI in the target design, so this is correctness), `TS-P2-88` (`-> ok` yields
its return value), `TS-P3-01` (`tosh check`).

**The gap: there are no board items for tier coverage or the no-CLR subset at
all.** The single largest piece of native work — moving the target subset from
Tier 2/3 down to Tier 1 — is entirely unfiled. That should be remedied before
any scheduling conversation, because it is the item that dominates the estimate.

### Bucket 3 — neither; defer (~15 items)

Shell, tooling and CLR-interop polish: `TS-P2-09`, `TS-P2-26`, `TS-P2-95`,
`TS-P2-96`, `TS-P2-97`, `TS-P2-100`, `TS-P2-101`, `TS-P2-86`, `TS-P3-03`,
`TS-P3-06`, `TS-P3-07`, `TS-P3-08`, `TS-P3-09`, `TS-P3-12`. Plus `TS-P2-38`
(suite memory exhaustion) which blocks nothing but hurts daily development.

Note `TS-P2-96`/`TS-P2-97` are CLR-interop items — they matter for .NET mode
and are irrelevant to native mode, which is itself a useful signal about how the
board will split as the two targets diverge.

## Going native: what is gained and what is given up

### Gained

- **Startup: ~120 ms → 1–5 ms.** The transformative one for a shell. Prompts,
  hooks, subshells and CI scripts stop paying a per-invocation tax.
- **Memory**: a CLR process floor is tens of megabytes; a native tosh could be
  single-digit. Matters for many concurrent shells and for containers.
- **Distribution**: one static binary. `scp tosh server:` and it works — no
  runtime install, no version matching. For a *shell*, this is a big deal.
- **Predictable latency**: no JIT warmup on first execution of a path, and a GC
  sized for the workload rather than for enterprise services.
- **Embedding**: a native tosh with a C ABI can be embedded in other programs as
  a scripting engine. Currently embedding tosh means embedding .NET.
- **Pipeline throughput**: owning the object layout removes reflection from
  member access, which is the hot path today.

### Given up

- **The BCL.** Thirty years of batteries: `Regex`, `DateTime` with the timezone
  database, `HttpClient`, JSON, compression, cryptography, ICU-backed
  globalisation. Each is reimplemented, bound from C, or absent.
- **The .NET ecosystem in native mode.** No NuGet, no `load-assembly`. This is
  arguably tosh's biggest current differentiator, and native mode does not have
  it.
- **Reflection.** `describe-type`, `members`, `methods` and dynamic member
  access are core to how the shell *feels*, and they are CLR reflection today. A
  native ToastScript needs its own metadata and reflection system.
- **GC quality.** The CLR's collector is generational, concurrent and tuned over
  decades. Boehm is conservative: weaker pauses, no compaction, and false
  retention. For a long-running interactive shell this is a real regression.
- **Tooling.** DAP, profilers and crash dumps come free with .NET. Native means
  DWARF, a native debug adapter, and owning the whole chain.
- **Two implementations, permanently.** Two runtimes, two stdlibs, two sets of
  semantics, and a differential corpus that must never be allowed to rot. Two
  backends already disagree today (`TS-P2-109`).
- **Development velocity.** C# with its tooling is fast to work in. Every bug in
  your allocator, GC or string implementation becomes yours.

### The honest framing

The sacrifice is scoped by the dual-target design: **native mode gives up .NET;
.NET mode keeps everything.** So the real cost is not "losing the BCL" — it is
*maintaining two worlds*, forever, alone.

Which makes the decisive question: **is ~120 ms worth two implementations?**

**Falsify it cheaply first.** The resident-server-plus-thin-client architecture
recovers most of the startup win for a fraction of the effort and keeps the
entire BCL. If a warm server makes `tosh -c` feel instant, the strongest
motivation for going native evaporates, and the project narrows to
*distribution* and *embedding* — both real, but far narrower goals that might be
served by a much smaller native subset rather than a full second world.

That experiment costs days. The native path costs years. Run the experiment
first.

## The plan

Sequenced so that **every phase is independently valuable and each one can
cancel the next.** No phase asks for a leap of faith, and the expensive
commitment is deferred until the evidence is in.

### Phase 0 — INCOMPLETE; question answered, implementation deferred

**Status: the decision Phase 0 existed to make is made. The server itself is a
prototype and is not being productionised now.**

Why deferred rather than finished:

- **Its purpose was to decide, and it decided.** Everything past the
  measurement is product work, not decision input.
- **The expensive remaining piece serves the case that needs it least.** Pty
  handoff, job control and session lifetime are for *interactive* use — and
  interactive startup is paid once per terminal, now 245 ms. The 350 ms that
  actually hurts is repeated non-interactive `tosh -c`, whose client needs only
  cwd, environment, the three standard streams and an exit code. No pty.
- **There is an unresolved semantic question in front of the easy slice.** Every
  `tosh -c` is a fresh process today. A shared engine would leak variables, cwd
  and imports between invocations unless each request gets its own scope. That
  is a semantics change, and getting it wrong is a subtle correctness bug rather
  than a slow path.
- **It is a local privilege boundary.** A socket that executes arbitrary
  commands as the user needs 0700/0600 permissions and an auth token before it
  ships. Not polish.
- **Phase 1 outranks it.** `TS-P2-107`/`108`/`109` are correctness bugs
  affecting anyone writing class hierarchies today, not merely self-hosting
  gates.

If the non-interactive slice is ever picked up, it is roughly a day: client
sends cwd + env + command, server isolates per-request scope, streams stdout and
stderr separately, returns an exit code, and auto-starts on a missing socket.

The `--serve` prototype stays in the tree as the evidence for the decision.

### Phase 0 — measured result, 2026-08-13

**A warm engine behind a unix socket answers in 1.9 ms — which is entirely the
client's own process spawn.**

| | latency |
|---|---|
| cold `tosh -c 'echo hi'` (full profile) | **350.8 ms** |
| warm, over a unix socket | **1.9 ms** |
| `socat` overhead alone, against `/bin/cat` | **1.9 ms** |

The warm figure is indistinguishable from the control: tosh's contribution is
below the measurement floor, and the whole 1.9 ms is `socat` starting up. A
purpose-built client would be faster still.

Implementation was `--serve <socket>` in `src/Tosh.Cli/Program.cs`: strip the
flag before plan resolution so the remaining args resolve to a normal REPL plan
(which is what loads config, profile and autoload), then after startup serve one
newline-terminated command per connection against the already-warm engine. About
60 lines. Full suite 4,842 passing.

**What this means for the native project.**

The startup argument — the strongest motivation for going native, and the one
that made a years-long rewrite look justified — **is fully answered without any
native code.** Invocation latency drops by a factor of ~185, and the residual is
the client, not the shell.

So the native goal narrows to what it always actually was underneath:
**distribution** (one static binary, no runtime install) and **embedding** (a C
ABI scripting engine inside another program). Both are real. Neither requires
giving up the BCL, maintaining two object models, or writing a GC — and neither
is urgent.

**Recommended revision to the plan:** treat Phases 1–4 (type checker, backend
agreement, self-hosting on IL, specifying the core types) as the real roadmap,
and treat Phase 5 as optional and evidence-gated. Self-hosting on IL delivers
"ToastScript written in ToastScript" with full .NET compatibility preserved; a
native target after that is a distribution decision, not a performance one.

**Follow-up work the prototype does not cover** (none of it on the critical path
for the decision, all of it needed before this ships): pty handoff for
interactive use, cwd and environment propagation, per-client session isolation,
daemon lifetime and restart, socket permissions, and concurrent clients.

### Phase 0 — Falsify the premise (days) — original plan

Build the resident-server + thin-client. Measure `tosh -c` before and after.

The whole native project is motivated by ~120 ms of CLR startup. If a warm
server makes scripted invocation feel instant, that motivation is gone and the
native goal narrows to *distribution* and *embedding* — both real, both far
smaller, both plausibly served by a modest subset rather than a second world.

**Attempted 2026-08-13: lazy `bind native` — a modest win, not the fix.**
`NativeFunctionCommand` now creates its delegate on first call rather than at
declaration (`Lazy<Delegate>`). Measured Release-to-Release, three runs each:

| | eager | lazy |
|---|---|---|
| whole profile | ~571 ms | ~575 ms |
| `Sdl.tosh` alone | ~406 ms | ~376 ms |

So roughly **7 % off the SDL module and nothing measurable on the profile**. The
prediction that this would recover "most of the ~600 ms" was wrong.

A synthetic bind block of 15 functions did drop from 159 ms to 55 ms, which is
what motivated the change — but that improvement does not reproduce on the real
modules, so delegate emission is not the dominant term after all. Full suite
4,842 passing. The change is retained on its own merits (do not do work that may
never be needed) but it carries a real trade: **a missing export now fails on
first call rather than at declaration.**

**Where the time actually goes is still unknown.** Per-module, in-process,
Release:

```
MathTypes 16   Point 14   Vector 2   Native 1
Graphics  65   Sdl  297   Gl 113     Gtk 61      TOTAL ~571 ms
```

Hypotheses tested and rejected so far: `dlopen` of the native libraries, enum
declarations, `raw struct` emission, module nesting, class-body versus
module-level `bind`, and now delegate emission. Confirmed contributors are only
`using System.Drawing` (~70 ms, one-time) and the first `raw callback` in a
process (~120 ms, one-time).

**The profiler found it in one run.** `dotnet-trace collect --format speedscope`
around a module load, inclusive time per frame:

| frame | inclusive |
|---|---|
| `ToshEngine.ResolveTypeName` | 518 ms |
| `DotNetTypeResolver.ResolveUncached` | 514 ms |
| `DotNetTypeResolver.TryResolveFromImports` | 485 ms |

**Type-name resolution was ~90 % of module load.** Seven hypotheses had been
tested by bisection and all seven were wrong; the profiler answered it
immediately. The lesson is worth keeping: bisection is good at falsifying a
named suspect and useless at finding an unnamed one.

**Root cause.** `ResolveNativeInteropParameterType` consulted
`NativeTypeLexicon.IsCStringName` and then fell through to full CLR type
resolution — so `int`, `uint`, `ptr` and `byte`, which are most of what a bind
block declares, each scanned every import. `NativeTypeLexicon.TryResolveScalar`
exists precisely for these names and was never called. A 28-function SDL block
resolves roughly 110 parameter types this way.

**Fix.** Consult the scalar table before the CLR resolver. Also narrowed the
resolver's cache invalidation: loading an assembly can make a name resolvable
but never make a resolved name stop resolving, so only *negative* entries are
now dropped when the assembly count changes, rather than the whole cache.

**Measured, Release to Release:**

| | before | after |
|---|---|---|
| `Gl.tosh` | 113 ms | **7 ms** |
| `Gtk.tosh` | 61 ms | **8 ms** |
| `Sdl.tosh` | 297 ms | 212 ms |
| all eight modules | 571 ms | **329 ms** |
| **`tosh -c 'echo hi'` with the full profile** | **0.73 s** | **0.58 s** |

Full suite 4,842 passing. Roughly **200 ms off every shell start**, and the
modules that are almost pure `bind` blocks got 8–16× faster to load.

### Second profiler run: the negative cache was switched off

`Sdl.tosh` remained the outlier at 212 ms. Profiling it alone put
`DotNetTypeResolver.SafeGetTypes` at 296 ms inclusive — `Assembly.GetTypes()`
over every assembly loaded since the platform index was built.

**Root cause.** `TryResolveDirect` guarded its negative cache with

```csharp
_negativeResultCache.ContainsKey(name) &&
AppDomain.CurrentDomain.GetAssemblies().Length <= _platformIndexedAssemblyCount
```

but `_platformIndexedAssemblyCount` is deliberately a *pre-index snapshot* that
never advances, so the guard became permanently false the moment anything
loaded. The negative cache therefore switched itself off early in startup, and
every subsequent miss re-enumerated the full type list of every assembly past
the watermark.

Module-local names are the ones that miss: a `raw struct`, an `enum` or a class
declared in the same file is never a CLR type, so **every annotation mentioning
one paid a full assembly scan.**

**Fix.** Record the assembly count alongside each negative entry and compare
against *that*, so a repeated miss is O(1) unless an assembly has genuinely
appeared since. A global "already scanned" watermark would have been incorrect —
a name never looked up before would skip assemblies scanned on another name's
behalf.

**Cumulative result**, in-process module load, same binary shape throughout:

| module | original | + scalar fast path | + negative cache |
|---|---|---|---|
| `Graphics.tosh` | 106 ms | 110 ms | **49 ms** |
| `Sdl.tosh` | 297 ms | 180 ms | **99 ms** |
| `Gl.tosh` | 113 ms | 7 ms | 7 ms |
| `Gtk.tosh` | 61 ms | 8 ms | 9 ms |
| **all eight** | **571 ms** | **323 ms** | **199 ms** |

**~65 % off module load.** Full suite 4,842 passing after each change.

### Verified end to end, after installing the build

```
Startup Profile          before        after
  Total:                 648.7 ms  →  245.7 ms
  Profile:               628.3 ms  →  224.8 ms
```

**2.6× faster startup, 403 ms saved per invocation.**

### Where the remaining time goes, and why to stop here

A third profiler run on the fixed build:

| frame | inclusive |
|---|---|
| `DotNetTypeResolver.SafeGetTypes` | 187 ms |
| `WarmUpPlatformTypeIndex` | 180 ms |
| `BuildPlatformTypeIndex` | 178 ms |

The remainder is **building the platform type index itself** —
`EnumerateTrustedPlatformAssemblies` walks the entire
`TRUSTED_PLATFORM_ASSEMBLIES` list, loading ~200 runtime assemblies and calling
`GetTypes()` on each. That is not a bug; it is what buys the ability to resolve
any BCL type by name without a `using`.

It is already started on a background thread at `ToshRuntime` construction
(`ToshRuntime.cs:628`), so it is as overlapped as it can be — module loading
needs type resolution almost immediately and blocks on it.

That leaves roughly 70 ms of everything-else, so **further in-process
optimisation has clearly hit diminishing returns.** The two remaining ideas are
narrowing what gets indexed (a semantic regression — BCL types would stop
resolving until referenced) or persisting the index to disk keyed by runtime
version (real, but invalidation-prone).

**And a ~180 ms one-time-per-process index is exactly what a resident server
amortises to zero.** The remaining cost has become the argument for the next
phase rather than a target for this one.

Note on end-to-end numbers: a plain `Release` build and the published
single-file binary have different floors (~0.25 s versus ~0.15 s for
`--no-profile`), so process-level before/after comparisons are only meaningful
between identically-shaped builds. The in-process figures above are the reliable
measurement.

Also worth filing: `--profile-startup` is only wired for
`CliInvocationKind.Repl` (`CliInvocationResolver.cs:294`), so it silently does
nothing for `tosh --profile-startup -c '…'`, which is the invocation a startup
investigation most wants.

**Exit:** a measured number, and an explicit decision to continue or to stop at
"fast shell, one world."

### Phase 1 — Unblock compiler-shaped code (weeks)

`TS-P2-107`, `TS-P2-108`, `TS-P2-109`, then the rest of Gate A's type-system
stream.

**Acceptance is already written**: the probe in `docs/SELF_HOSTING_RFC.md`
compiles clean and runs. Today it fails with 26 instances of one error.

Valuable regardless of any native decision — these are correctness bugs in the
type checker that affect every user writing class hierarchies today.

### Phase 2 — Make the backends agree (weeks)

`TS-P3-23` differential corpus, `TS-P1-13`, `TS-P1-40`.

Two backends currently disagree on nine lines with no dynamic features. Until
that discipline exists, adding a third backend multiplies an unmeasured problem.
This is the phase that makes everything after it safe.

### Phase 3 — Self-host on IL (months)

Rewrite the compiler in ToastScript, targeting IL. Stage 0 → 1 → 2 fixpoint.

**Full .NET compatibility is preserved for free** — the CLR is never left. This
delivers the "written in itself" goal with no native code at all, and it is the
ultimate dogfooding: 50k lines will surface every remaining papercut faster than
any audit.

**Exit:** ToastScript compiles ToastScript, bit-identical across stages 1 and 2.

### Phase 4 — Specify the core (parallel with 1–3, cheap now)

`TS-P3-16`: write the specification of `str`, `list`, `date`, `regex` and the
rest in ToastScript's own terms, while they keep their BCL implementations.

This is the option-preserving move. It costs little today and cannot be
retrofitted cheaply: every doc and script written against `System.String`
semantics is lock-in. Doing this early is what keeps the native door open
without committing to walking through it.

### Phase 5 — Native, if Phase 0 said yes (years)

Gate B in order: `TS-P3-15` subset definition → `TS-P3-17`/`18`/`19` tier
promotion → `TS-P3-20`/`21` runtime → `TS-P3-22` backend.

Note the ordering is the reverse of intuition: the C backend is the *last* and
*smallest* item. The work is in defining the subset and promoting the language
out of Tier 2, not in code generation.

Encouraging datum: **57 of 72 tracked features already reach Tier 1**, with only
2 at Tier 3. The tier gap is much narrower than the original framing assumed.

### What not to do

- Do not rewrite the lexer or parser in a lower-level language. Measured: 2,502
  lines parse, bind and declare in 30 ms. It is the fastest part of the system.
- Do not split into two products. One language, two targets, `no_clr` as a
  profile.
- Do not write the native backend first. It is gated by everything above it.

## Open questions driving the discussion

1. Which goal is the real driver — self-hosting, native distribution, or
   performance? They have different solutions and only partly overlap.
2. What does "full .NET compatibility" mean precisely: the pipeline carries CLR
   objects and any assembly can be `load-assembly`'d at runtime? Or the weaker
   "can call into .NET when asked"?
3. Does "native" require no CLR in the process, or is a hosted CoreCLR
   acceptable?

## Working notes

_(appended as the discussion proceeds)_

### Three goals hiding in one question

- **(a) Self-hosting** — the language is written in itself. A dogfooding and
  credibility goal.
- **(b) Native distribution** — a tosh program runs with no .NET install.
- **(c) Performance** — the shell and its scripts get faster.

These are separable, and each has a cheaper answer than "rewrite the core in
Rust":

- (a) can be reached **without leaving .NET at all** — the existing compiler
  already emits IL, so a ToastScript-written compiler that emits IL is a
  complete bootstrap. Full .NET compatibility is preserved for free, because
  you never leave the CLR.
- (b) is what **NativeAOT** already does for .NET — but see the hard constraint
  below.
- (c) is almost certainly not bounded by the implementation language. See
  "where the time actually goes".

### Hard constraint: AOT and `load-assembly` are mutually exclusive

A fully ahead-of-time native binary has no JIT, so it cannot load and execute
IL that was not known at build time. `load-assembly`, the CLR bridge's
reflection-driven dispatch, and `Reflection.Emit` (which the native-callback
thunks and delegate factories rely on) all require a JIT.

So: **native single binary** and **load arbitrary .NET assemblies at runtime**
cannot both hold, unless the native process *hosts CoreCLR* — which brings the
JIT, the GC and the startup cost back with it.

### The crux: tosh's identity is the CLR object model

The pipeline carries .NET objects. `$x.Length` is reflection over a CLR type;
a class can `extends System.Text.StringBuilder`; values are `System.Drawing.Color`
and friends. That is not "tosh calls .NET" — the object model *is* the CLR's.

A native core therefore has to choose:

1. **Host CoreCLR, CLR objects as the only value model.** Native code holds
   handles; every member access is a managed call. The "native" core becomes a
   thin shell over managed work, paying boundary costs for little gain.
2. **Host CoreCLR, dual value model.** Native fast path for primitives, strings
   and pipeline plumbing; box into CLR only at the interop boundary. Realistic,
   but you now maintain two type systems and their conversion and identity
   rules.
3. **Native core, no CLR.** Stops being tosh — the .NET object pipeline is the
   product.

### Measured startup, 2026-08-13 (Release build, AMD Ryzen 9 9950X)

| invocation | wall time |
|---|---|
| `bash -c 'echo hi'` / `zsh -f -c` | ~0.00 s |
| `tosh --version` | 0.05–0.06 s |
| `tosh --no-profile -c 'echo hi'` | 0.12–0.15 s |
| `tosh -c 'echo hi'` (full profile) | **0.74–0.78 s** |

Marginal cost of each library in the profile (baseline 0.12 s):

| module | total | marginal |
|---|---|---|
| MathTypes, StringTypes, Point, Vector, Shell, System, Network, Filesystem, Bluetooth, Git | ~0.12 | ~0 |
| Graphics | 0.26 | +0.14 |
| Sdl | 0.53 | +0.41 |
| Gtk | 0.71 | +0.59 |
| Gl | 0.82 | +0.70 |

**Hypotheses tested and rejected:**

- *`dlopen` of the native libraries is the cost* — no. Binding one function from
  `libGL`, `libgtk-3` or `libSDL2` costs 0.11–0.14 s, i.e. within noise of the
  0.12 s baseline.
- *`Reflection.Emit` of one delegate type per native signature is the cost* — no.
  A bind block with 40 distinct signatures costs +0.02 s over one with a single
  function.

**Confirmed contributor:** assembly loading. `using System.Drawing` plus one
`new Size(1,2)` costs **+0.07 s** over a bare script. Each additional BCL
assembly a module touches pays similarly.

**Is it parse time? No — decisively.** A generated 2,502-line file declaring 500
functions and 500 classes lexes, parses, binds and declares in **0.15 s against a
0.12 s baseline — +0.03 s**. The entire profile is ~1,500 lines across 15 files
and costs ~0.62 s. Parsing is roughly twenty times cheaper than what the profile
actually spends.

**More hypotheses tested and rejected:** module-level `bind native` vs
class-body `proud bind native` (both free at 40 signatures); enum declarations
(five 40-member enums, free).

**Second confirmed contributor — and it is one-time, not per-declaration:**

| `raw callback` declarations in a file | wall time |
|---|---|
| 0 | 0.12 s |
| 1 | 0.24 s |
| 2 | 0.26 s |
| 5 | 0.26 s |
| 10 | 0.28 s |

The *first* `raw callback` in a process costs ~0.12 s; each additional one is
nearly free. Something on that path performs a one-time initialization that a
plain `bind native` does not trigger. This is new in this session and worth
finding.

**Conclusions.**

1. ~0.12 s is the CLR + tosh-init floor; `--version` at 0.05 s suggests roughly
   half of it is tosh's own startup (command registry and friends) rather than
   raw CLR.
2. The remaining ~0.6 s of a real session is *profile evaluation*, and it is
   dominated by assembly loads and per-declaration work — not by executing
   ToastScript.
3. **A native rewrite would not fix (2).** Loading `System.Drawing` costs what
   it costs whoever asks for it. It *would* fix (1).
4. The graphics modules added in this session are most of the current profile
   cost. Lazy binding — deferring both the native library load and the delegate
   creation to first call — is the obvious lever, and is worth doing regardless
   of any rewrite.

### Compiler modes — the proposal, and the seam it forces

Proposed: `--target native` and `--target dotnet`; importing .NET in native mode
is a compile error; a small "Native Core Library" mirrors the shape of the .NET
core library.

The profile machinery for this **already exists in part** —
`docs/COMPILED_TOSH.md` defines Bucket A (pure language), Bucket B (library),
Bucket C (shell-only) and proposes ToastScript-Shell vs ToastScript-Lang
profiles. Native mode is Bucket A + Bucket B with a different backend.

The unresolved question is where the type seam is cut. There are two answers and
they are not variations of each other:

**Seam A — same names, two implementations.** `string.Replace`, `Math.Clamp`,
`DateTime.Now` exist in both modes; native gets a hand-written version, .NET
gets the BCL. Attractive because scripts look identical in both modes.

The problem is that semantic divergence is guaranteed and unbounded:
`String.Format` specifiers, culture-aware comparison (see `TS-P2-75`), `double`
round-tripping, `DateTime` timezone rules, and the whole .NET `Regex` dialect.
Each is a place where one script silently produces different output depending on
the target. Matching .NET `Regex` alone is plausibly harder than the compiler.

**Seam B — ToastScript owns its core types; .NET is a foreign world.**
ToastScript specifies `str`, `int`, `dec`, `list`, `dict`, `date`, `duration`,
`regex` with its own semantics, implemented once per backend against one
conformance corpus. CLR values are a distinct kind, converted at the boundary.

Seam B is the design the "call into .NET when asked" fallback describes, and it
is stronger than Seam A rather than a concession: one specification you control,
one corpus that must pass under both targets, and a native implementation that
implements *your* string rather than chasing Microsoft's.

**Seam B is cheaper than it looks, because the indirection already exists.**
The pipeline does not cast to `System.Drawing.Color` and read `.R` — it goes
through `IShellRecordObject`, `ReflectionObjectAccessor`, `ShellIndexingUtilities`
and `TypeConversion`. Member access is already a protocol, not a field load.

### The real gate: native mode needs to prove no CLR value escapes into it

`--target native` cannot simply reject `load-assembly` and `using`. `$x.Length`
must work when `$x` is a string and must fail to compile when `$x` is a CLR
object — which requires knowing statically which values are CLR-backed. Today
the language is largely dynamic, so an unknown type would have to be rejected
conservatively, which could be extremely noisy.

**This makes the typing discipline the gate on native mode, more than codegen.**
It is also the part that most affects the language everyone writes today.

### Runtime questions that follow from a native target

- **GC.** Closures, cyclic object graphs and shell allocation patterns need one.
  Boehm is the pragmatic start; refcounting leaks cycles; writing one is a
  project. In .NET mode the CLR GC already exists, so the two targets are best
  understood as **two runtimes behind one front end**, not one runtime with a
  switch.
- **String encoding.** .NET strings are UTF-16; a native ToastScript would
  sanely be UTF-8. Every string crossing the boundary transcodes, and a shell
  pipes a lot of text.
- **Codegen target.** Emitting C is the pragmatic first backend — every
  platform's optimiser for free, easy bootstrapping and debugging (Nim and Vala
  do this). LLVM IR is better codegen for a much larger dependency.

### Assembly is the one option to rule out

Not portability-theatre — concrete reasons: a compiler is a large, allocation-
heavy tree-walker whose performance comes from data structure choices, not
instruction selection; you would hand-write exception unwinding, calling
conventions and register allocation; there is no ecosystem for the unglamorous
parts (Unicode, hashing, TLS); and hosting CoreCLR means driving a C API
(`nethost`/`hostfxr`) whose marshalling glue would dwarf the compiler. Assembly
earns its place in hot kernels — SIMD scanning in the lexer, say — not as an
implementation language.
