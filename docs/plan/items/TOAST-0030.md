---
id: TOAST-0030
title: "The compiled backend does not implement five of the semantics `docs/spec/` now states"
status: open
area: toast
priority: 2
opened: 2026-08-20
---

## Problem

`TOAST-0018` specified eight core concerns and wrote a corpus for each. Running a
representative subset across **both** backends — which is what Phase A's exit asks for —
found five places where the compiled backend does not do what the specification says.

Each is a claim about the *language* that only the interpreter currently honours:

| Case | Specified | Interpreted | Compiled |
|---|---|---|---|
| `{% "a" => 1, "b" => 2 %} \| count` | a dictionary is one value | `1` | **`2`** |
| `class E extends Error` | a declared error type | accepted | **"Command 'class' was not found"** |
| `catch (e) { $e is Error }` | true for an `Error` | `true` | **`false`** |
| `$x.Length` where `$x` is null | reports the member and suggests `?.` | that message | **`NullReferenceException`** |
| `null + "a"` | raises, naming `?? ""` | that message | older message, no guidance |

The first three are semantic: a program that runs correctly interpreted gives a different
*answer* compiled. The last two differ only in the message, which matters less but is still
a divergence a conformance corpus has to account for.

## Where they are recorded

`DifferentialExecutionTests.KnownDivergences()`, each asserted to **still** diverge. That is
a tripwire rather than an endorsement: when one is fixed, the test fails and says so, and
the case moves up into `Corpus()`.

## Acceptance

- [ ] A dictionary counts as one value on both backends
- [ ] `class E extends Error` compiles
- [ ] `is Error` is true for a declared error type and for a caught `Error`, compiled
- [ ] Reaching a member of `null` reports it with the same message on both backends
- [ ] `null + "a"` raises with the same message on both backends
- [ ] Each case moves from `KnownDivergences()` into `Corpus()` as it is fixed
- [ ] Once a class compiles, the corpus gains a `Failure` / `Error` / `Diagnostic` case —
      moved here from `TOAST-0031`, which could not add one while `class E extends Error`
      does not compile at all
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
