---
id: TOAST-0030
title: "The compiled backend does not implement the semantics `docs/spec/` states, in four distinct ways"
status: complete
area: toast
priority: 2
opened: 2026-08-20
closed: 2026-08-21
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

### Causes B and C done

**Cause C** was two messages and one exception *kind*. `ToastMessages` in `Tosh.Runtime`
now holds the wording, and the compiled member-of-null raises a `ToshDiagnosticException`
rather than a `NullReferenceException` — because `catch (e) { $e is Diagnostic }` is
written against the language's failure model, and a host exception is not visible in those
terms. Asserted separately from the message, since comparing rendered text alone would
have accepted two different exception types with matching words.

The `+` guidance moved *down* into `OperatorEvaluator.Add` rather than being copied. The
interpreter has a string-concatenation arm that fires before `Add`; the compiled path
reaches `Add` directly, so the portable method was raising the bare sentence and losing the
remedy that makes raising reasonable at all.

**Cause B** was one function with its own opinion. `ToshHost.SeedFromValue` walked any
`IEnumerable` and special-cased `string` — a second answer to "which values are sequences?"
that disagreed with the language's in *both directions*: dictionaries and records spread
when they are single values, and ranges did not spread when they are sequences.

It now yields one value marked `SpreadableSequence`, which is exactly what an expression
head does interpreted, and decides nothing itself.

That exposed the same defect `TOAST-0028` stage 1 found interpreted: `RunUserFuncStage`
walked its input directly, and got away with it only because the head pre-expanded
everything. Three tests failed the moment the head stopped. It goes through
`ReplaySingleInputCollectionAsync` now, like every other consumer.

Nine cases added, four of them controls — an array, nested arrays, a string and a set —
because getting a dictionary to count 1 could equally have been done by making everything a
single value. The negative control fails eight and leaves all four controls passing.

### Cause A completed, and `TS-P1-47` with it

**`class E extends Error` now compiles.** `CanEmitClrClassShell` refused any base not
declared in the same unit and handed the whole declaration to source replay, which failed
at runtime with "Command 'class' was not found". An emitted type can now derive from a real
CLR parent, resolved through the same `TryResolveToastTypeName` the compiled `new` uses, so
a name means one thing wherever it appears.

Deliberately conservative: a sealed type cannot be a parent, and one with no reachable
parameterless constructor cannot be chained to. In both cases source replay remains the
honest answer, because truncating the hierarchy at `System.Object` would give the class a
different identity than it has interpreted — quietly.

`TOAST-0031`'s deferred corpus case landed with it: `Failure` / `Error` / `Diagnostic` are
now asserted across both backends, which could not be written while a class extending
`Error` did not compile at all.

**`TS-P1-47` was a third consumer of the same question.** The annotation path walked
`classInstance.Definition.BaseClass` — over *interpreter* instances. A compiled class is a
real CLR type, fails that pattern, and fell through to `TypeConversion.TryConvert`, which
reported that a `DiffLeaf` could not be converted to the base it already derives from.

It asks `OperatorEvaluator.IsInstanceOfShellType` now, so an annotation and `is` cannot
disagree about a type name.

**Placement was the whole of it, and the first attempt was wrong.** Asking before
conversion looks obviously right — why convert something that already is the type? — and it
broke `var a: array = [1, 2]`, which bound as `array<int>` instead of `array`. An annotation
may legitimately *retype* a value it already matches. Conversion means "make it this"; the
new check only answers "it already is this", so it belongs where conversion has declined.
One suite run found that; nothing about reading the code would have.

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

- [x] **Cause A** — `new Error(…)`, `class E extends Error`, `is Error`, `is Failure` and
      `is Diagnostic` behave identically on both backends
- [x] **Cause B** — a dictionary, a record and a range have the same shape on both backends
- [x] **Cause C** — the member-of-null and `null + "a"` messages come from one place
- [x] **Cause D** — `is` against a declared base is true at any depth, compiled, and
      `TS-P1-47`'s base-annotated variable accepts a subclass
- [x] Every rule fixed lives in `Tosh.Runtime` and is called by both backends, rather than
      implemented twice and compared
- [x] Each case moves from `KnownDivergences()` into `Corpus()` as it is fixed
- [x] Once a class compiles, the corpus gains a `Failure` / `Error` / `Diagnostic` case —
      moved here from `TOAST-0031`, which could not add one while `class E extends Error`
      does not compile at all
- [x] The siblings found by probing are in the corpus too, not just the five recorded
- [x] A negative control

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
