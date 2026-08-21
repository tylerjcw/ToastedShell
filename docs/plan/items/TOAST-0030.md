---
id: TOAST-0030
title: "The compiled backend does not implement the semantics `docs/spec/` states, in four distinct ways"
status: open
area: toast
priority: 2
opened: 2026-08-20
absorbs: TS-P1-47
---

## Problem

`TOAST-0018` specified eight core concerns and wrote a corpus for each. Running a
representative subset across **both** backends — which is what Phase A's exit asks for —
found five places where the compiled backend does not do what the specification says.

## Re-measured 2026-08-21: five symptoms, four causes

Before starting, every recorded case was run against both backends and the boundary of
each was probed. **The five are symptoms of four causes, and each cause also breaks things
that were never recorded.** An item that fixed "the five" would close while the same code
paths still diverge.

| Cause | Recorded symptoms | Also broken, found by probing |
|---|---|---|
| **A.** The compiler does not know Tōast's built-in type names | `class E extends Error` does not compile; `$e is Error` is false | `new Error("x")` — *"unknown type 'Error' in `new` expression"*; `$e is Failure` false. `is Exception` **works**, so it knows CLR names and not Tōast ones |
| **B.** Collection shape | a dictionary counts 2 | a **record** counts 2; `{\| a = 1 \|} \| first` yields `[a, 1]` rather than the record; a **range** counts 1 compiled against 3 interpreted |
| **C.** Diagnostic messages | member-of-null; `null + "a"` | — |
| **D.** `is` against a declared base class | *not recorded* | `class B { }` / `class D extends B { }` gives `(new D()) is B` **false** compiled. Inheritance itself works — inherited *and* overridden properties are both correct — so it is the type test alone |

Cause D absorbs **`TS-P1-47`** ("a base-annotated variable rejects a subclass when
compiled"), which is very likely the same root reached from the annotation side rather
than the `is` side.

## Decisions — 2026-08-21

Taken with the user before starting.

1. **Fix the causes, not the five symptoms.** A cause fixed once fixes every symptom it
   has, including the ones nobody wrote down.
2. **The shared rules move into `Tosh.Runtime`**, and both backends call them. Not
   "compiled calls the interpreter": `Tosh.Compiler.Runtime` already references
   `Tosh.Language` and delegating there would deepen the exact dependency Phase B's exit
   says to remove — *"the probe compiles and runs through the normal IL path without an
   interpreter dependency"*. Putting a rule in the portable runtime makes divergence
   **structurally impossible** for that rule rather than merely tested.
3. **`TS-P1-47` is folded in**; the other three recorded divergences are not.

### Why the message duplication is the whole item in miniature

Reaching a member of `null` is one rule. It is written twice:

- `src/Tosh.Language/ToshEngine.Arguments.cs` — *"Cannot read member 'Length' of null. Use
  '?.' to yield null instead."*
- `src/Tosh.Compiler.Runtime/ToshHost.cs` — *"member access 'Length' on null target"*

Neither is in the portable runtime, so there was nowhere for them to be the same. That is
how all four causes arose, and it is what decision 2 is for.

## Progress — 2026-08-21

### Cause D done, and cause A half done

Both were **one missing lookup each**, and both lookups now live in `Tosh.Runtime`, which
is what decision 2 asked for. Neither needed the emitter.

**Cause A, first half.** The compiled `new` consulted user-declared types and then
`Type.GetType`, which needs an assembly qualifier — so every bare name failed, and Tōast's
own names have no CLR spelling to find. `DotNetTypeResolver.TryResolveToastTypeName` now
answers both, aliases first and the platform index second.

That one lookup closed **two** recorded divergences, because the failure was itself
throwable:

```tosh
try { throw new Error("x") } catch (e) { echo ($e is Error) }
```

compiled, the `catch` was catching the *resolution error* and correctly reporting that an
`InvalidOperationException` is not an `Error`. The recorded case "`is Error` is false
compiled" was never a bug in `is` at all. Worth stating plainly: the item described a
symptom two removes from its cause.

**Cause D.** `is` compared `actualType.Name` exactly, and a declared hierarchy only
answered correctly because the interpreter's instances are `ToshClassInstance`, which
implements `IShellTypeCheckable` and walks itself. A compiled class is a real emitted CLR
type with real inheritance and no such interface. Inherited *and* overridden properties
were both correct compiled, which is what made this look like a type-test bug rather than
an inheritance one — and it was: the walk was missing from the shared operator, so it was
added there.

Six cases moved into `Corpus()`. The negative control fails exactly five of them and
leaves `is-unrelated-class-is-false` passing, which is how a control earns its place.

### Cause A, second half: `class E extends Error` is source replay

Still divergent, and it is a bigger piece than the first half.
`BoundUnitEmitter.CanEmitClrClassShell` returns false unless the base is a **user-declared**
class, and the comment says why: rather than truncate the hierarchy at `System.Object`, the
whole declaration is left "for source replay". Source replay then fails at runtime with
"Command 'class' was not found".

So this is not a missing lookup but a missing capability — emitting a CLR base that is a
real `Type` rather than another `TypeBuilder` shell. It is also **Phase B's own second
bullet**, "remove compiler-subset source replay and implicit dynamic fallbacks", arriving
from underneath.

## Where they are recorded

`DifferentialExecutionTests.KnownDivergences()`, each asserted to **still** diverge. That is
a tripwire rather than an endorsement: when one is fixed, the test fails and says so, and
the case moves up into `Corpus()`.

## Acceptance

- [~] **Cause A** (half) — `new Error(…)`, `class E extends Error`, `is Error`, `is Failure` and
      `is Diagnostic` behave identically on both backends
- [ ] **Cause B** — a dictionary, a record and a range have the same shape on both backends
- [ ] **Cause C** — the member-of-null and `null + "a"` messages come from one place
- [x] **Cause D** — `is` against a declared base is true at any depth, compiled, and
      `TS-P1-47`'s base-annotated variable accepts a subclass
- [ ] Every rule fixed lives in `Tosh.Runtime` and is called by both backends, rather than
      implemented twice and compared
- [ ] Each case moves from `KnownDivergences()` into `Corpus()` as it is fixed
- [ ] Once a class compiles, the corpus gains a `Failure` / `Error` / `Diagnostic` case —
      moved here from `TOAST-0031`, which could not add one while `class E extends Error`
      does not compile at all
- [ ] The siblings found by probing are in the corpus too, not just the five recorded
- [ ] A negative control

## Notes

Filed rather than fixed because it is compiler work, and compiled ToastScript is an
experiment until the interpreted language is solid — so no compiler work was proposed while
closing `TOAST-0018`.

**The finding that matters more than the five entries** is that they existed at all. Eight
concerns were specified and given a corpus, and every one of those corpora ran a single
backend. The specifications therefore described the interpreter, and only running them
across both showed where that was not the same thing as describing the language. That is
exactly what Phase A's exit criterion is for, and it earned its place on first use.

Related: `TOAST-0022` records the earlier interpreted/compiled divergences — rendering a
class through a `Display` trait, and an interpolation hole's format clause — by the same
mechanism.
