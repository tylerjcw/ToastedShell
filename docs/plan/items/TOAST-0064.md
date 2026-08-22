---
id: TOAST-0064
title: "A CLR type annotation blocks start-up on a 17,000-name platform index"
status: proposed
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

**The first CLR type annotation in a script costs about 100 ms**, because resolving it blocks
on a platform type index covering every type in every loaded assembly — 17,139 fully
qualified names and 14,851 simple ones. The index is built on a background task at start-up,
so a script that never names a CLR type pays nothing; one that names a single type waits for
the whole thing.

Measured 2026-08-22 against a build published with the package's own flags, interleaved
rounds, minimum of eleven:

| Script | Min |
|---|---|
| empty file | 100 ms |
| `func q(p: string) => clear` — an alias | 111 ms |
| `class Local { }` / `func q(p: Local) => clear` | 109 ms |
| `func q(p: StringBuilder) => clear` | **202 ms** |
| `func q(p: System.Text.StringBuilder) => clear` | **221 ms** |

An alias and a same-file class resolve without the index. Any CLR name waits for it, and
waits once — thirty-one further annotations add about 15 ms between them.

This is not hypothetical: `~/.config/tosh/autoload/aliases.tosh` annotates a parameter with
`ToastLib.Filesystem.DirectoryName`, and the built-in `--profile-startup` attributes 171 ms
of a 188 ms autoload to that one file.

## Why it matters more than a benchmark usually would

This is a **login shell**. The cost is paid on every terminal, every subshell, every
`$(...)` command substitution, and every script a script calls. It is also the one number a
person feels continuously without ever profiling anything.

`TOAST-0037` owns compiler performance budgets. Nothing owns start-up.

## Where the time goes — 2026-08-22

Profiled rather than guessed, against a published build, with a bare .NET R2R single-file
app as the floor:

| | Min | What it is |
|---|---|---|
| bare .NET R2R app | 15 ms | the CLR and apphost, and nothing else |
| `tosh --safe` | 100 ms | the engine, with no config, profile or autoload |
| `pwsh -NoProfile` | 145 ms | |
| `tosh --no-profile` | 247 ms | the above **plus this machine's config and autoload** |
| `tosh` (full) | 374 ms | plus `profile.tosh` |

**The headline this item was filed with was wrong, and in the flattering direction for
PowerShell.** TōSh's own start-up is 100 ms against pwsh's 145 ms — it is *faster*. The
earlier comparison put TōSh **with a user's config** against pwsh **without its profile**,
because `--no-profile` skips `profile.tosh` and not `config.tosh` or `autoload/`. The name
invites exactly that mistake.

The three costs, in order:

1. **~100 ms — the platform type index**, paid by the first CLR type annotation. The
   dominant cost, and the only one that is avoidable rather than inherent.
2. **~34 ms — `LoadBuiltinRunesAsync`**, which parses and evaluates ToastScript during
   `ToshEngine`'s constructor, before the first prompt.
3. **~47 ms — `ToshRuntime.CreateDefault`**, of which the runes above are a part.

Everything after that is this machine's own configuration, which is not the language's
cost — though `aliases.tosh` costing 171 ms is a direct consequence of cost 1.

## What is already known

Start-up is not dominated by the recursion work of `TOAST-0049` — that was measured
directly and costs nothing: the same build with the deep-stack path bypassed started in
325 ms against 320 ms with it, and enlarging the thread stack measured 286 ms against
330 ms without.

### Correction — 2026-08-22: there is no recent regression

This item was filed saying current `master` was ~80 ms slower than the installed build, and
that a bisect would name the change responsible. **That was a measurement error**, and the
kind worth recording: a `dotnet build` output was being compared against a *package* binary,
and the package is published with `PublishReadyToRun=true`. The gap was ReadyToRun against
JIT, not a commit.

Publishing current `master` with the package's own flags settles it:

| Build | Min | Median |
|---|---|---|
| installed `/usr/bin/tosh` (R2R) | 253 ms | 272 ms |
| current `master`, published (R2R) | **250 ms** | 297 ms |
| current `master`, `dotnet build` | 318 ms | 363 ms |
| `pwsh` 7.6.3 | 141 ms | 172 ms |

So nothing regressed, and the comparison that matters is the first row against the last:
both are ReadyToRun, and TōSh takes about **1.8x** as long to reach a prompt.

The lesson is the same one `TOAST-0049` ran into three times: a startup number is only
meaningful against a build produced the same way.

### Half of it fixed — 2026-08-22

**A qualified name is now answered by the loaded assemblies, and only falls to the index when
they cannot.** `Assembly.GetType` is a hash probe rather than a scan, so this costs one
lookup per loaded assembly and does not enumerate types.

| Script | Before | After |
|---|---|---|
| `func q(p: System.Text.StringBuilder) => clear` | 216 ms | **110 ms** |
| 31 of them | 208 ms | **106 ms** |
| `func q(p: StringBuilder)` — a simple name | 227 ms | 222 ms |
| `func q(p: string)` — an alias | 106 ms | 110 ms |

A qualified annotation now costs the same as an alias. A **simple** name deliberately still
goes through the index: `TS-P2-66` measured all 16,727 of those answers and pinned an order,
because a naive scan resolves `Complex` to
`System.Threading.PortableThreadPool+HillClimbing+Complex`. Restricting the fast path to
names containing a dot leaves that untouched, and the tests assert it.

The tests are correctness tests, not timing ones, and **the negative control passes** — which
is the right outcome and worth saying plainly rather than dressing up. Removing the fast path
changes no answer, because a pure performance change must not. What it changes is measured
above, out of process, which is the only place it can be.

### The other half, fixed — the index is now cached between runs

**The platform index is written to `$XDG_CACHE_HOME/tosh/` and read back by the next
process**, keyed to a fingerprint of the framework description and the trusted-platform
assembly list — so it is only ever read for the same set of assemblies it was built from.
Anything unreadable, stale or malformed is ignored rather than repaired.

What makes it pay is *how* it is read. A cached **miss** is as authoritative as a hit, which
is the case that was costing the time: proving `ToastLib.Shell.ZClearScreen` is not a CLR
type no longer loads a single assembly.

It is a **sorted record file, searched in place** rather than parsed into a dictionary. That
is not a detail — the first version built a `Dictionary` of all 32,000 entries and cost
about 60 ms to load, which is most of what the cache exists to save:

| Probe | Cold | Dictionary cache | Searched-in-place cache |
|---|---|---|---|
| `func q(p: ToastLib.Filesystem.DirectoryName)` | 279 ms | 219 ms | **165 ms** |
| `func q(p: StringBuilder)` | 263 ms | 180 ms | **157 ms** |
| empty file | 138 ms | 145 ms | 145 ms |

Both resolution paths use it: the binder's, and `TryResolveDirect`, which is what the
*interpreter* uses. Caching only the first moved the cost rather than removing it — the
150 ms simply reappeared in `profile.tosh`, which resolves types through the second.

Measured end to end against this machine's own configuration, published build, interleaved:

| | Before | After |
|---|---|---|
| `tosh --safe` | 108 ms | 110 ms |
| `tosh --no-profile` (config + autoload) | 249 ms | **153 ms** |
| full login shell | 361 ms | 337 ms |
| `aliases.tosh` alone | 153 ms | **14 ms** |

The full shell improves least because `profile.tosh` spends its time on fifteen `require`
statements loading a ToastScript library — real parsing work, and nothing to do with types.

### What the item was wrong about, twice over

The line held responsible was `func md(path: ToastLib.Filesystem.DirectoryName)`. Removing it
saved 12 ms. Bisecting the file found the real one two blocks earlier:

```tosh
func zcl => ToastLib.Shell.ZClearScreen()
```

A *body*, not an annotation — and the cost is one-time, so deleting it would only have moved
the 150 ms to `hcl` on the next line. That is why removing the annotation barely helped, and
why "which line is it" was the wrong question: it was the first of many, and any one of them
would do.

### The half that was left, and it is the half this machine paid

The fix helps a name that *is* a CLR type. It does nothing for one that is not, because
proving a name absent still means consulting assemblies that have not been loaded:

| Script | After the fix |
|---|---|
| empty file | 102 ms |
| `func q(p: ToastLib.Filesystem.DirectoryName) => clear` | **205 ms** |
| 31 of them | 210 ms |

`ToastLib` is written in ToastScript — `~/.config/tosh/lib/*.tosh` — so that annotation names
a type the CLR has never heard of, and the index is built to establish it. That is the whole
of `aliases.tosh`'s 153 ms, and why this machine's start-up did not move.

Two ways out, and choosing between them is a decision rather than a tidy-up:

1. **Cache the index across runs.** The trusted-platform set is fixed for a runtime version,
   so it could be built once and read back. Correct, and a real piece of work.
2. **Answer from assembly names.** For `A.B.C`, ask whether any assembly on the list has a
   name that could plausibly contain that namespace. Cheap, and a heuristic — assembly names
   do not have to match their namespaces, so it can be wrong in a way that silently turns a
   concrete type into `dynamic`.

Left open deliberately: a wrong answer here is a type annotation quietly meaning nothing.

## Acceptance

- [x] Start-up is profiled rather than guessed, and the top three costs are named with
      numbers
- [x] The ~80 ms that recent commits added is bisected to the change that added it —
      **there was no such regression**; it was a dev build measured against a ReadyToRun
      package. Corrected above rather than deleted, because the mistake is the useful part
- [x] A CLR type annotation does not wait for the whole platform index — **when the type
      exists**. A name that is not a CLR type still builds it, which is the open half
- [x] A name that is *not* a CLR type does not build the index either — the cached index
      answers a miss authoritatively
- [ ] A budget exists and is asserted, so the next regression fails a test rather than being
      noticed a month later
- [x] Measured against `pwsh` on the same machine, using the interleaved minimum-of-N method
      above rather than a single timed run — and against a build published the same way
- [ ] A negative control

## Notes

Raised by the user asking what PowerShell's start-up time was under the same test.

Three separate measurement errors were made getting to this, all the same shape — comparing
things built or configured differently — and each was caught only by re-measuring rather than
by thinking harder:

1. A `dotnet build` output against a **ReadyToRun package**, which invented an 80 ms
   regression that did not exist.
2. TōSh **with** a user's config against pwsh **without** its profile, which reversed the
   result: TōSh is faster, not 1.8x slower.
3. The per-annotation cost read as 3.4 ms each, from dividing a one-time 100 ms by the 31
   annotations that happened to be in the file. Varying the count showed it flat.

A startup number is only meaningful against a build produced the same way and a configuration
loaded the same way.
