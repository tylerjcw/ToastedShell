---
id: TOAST-0055
title: "An unrecognised generic constraint is silently satisfied, and the vocabulary is four names"
status: partial
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

`§Type-Parameter Constraints` says unknown constraint names "are accepted conservatively
(reserved for future user-defined constraints)". Measured, that means a fabricated name
disables the constraint entirely, with no diagnostic:

```tosh
class B<T>(v: T) where T: TotallyMadeUpConstraint { prop value: T = $v }
new B<string>("hi")        # accepted
```

So a typo does not narrow the type — it removes the check. The failure is silent and in the
safe-looking direction, which is the worst combination: the declaration reads as constrained
and behaves as unconstrained.

`callconv` already takes the opposite position for the same shape of problem — "An
unrecognised name is an error rather than a silent fallback." The two should agree.

## The vocabulary is also the whole vocabulary

Four constraints exist: `Numeric`/`Number`/`INumber`, `Add`/`Sub`/`Mul`/`Div`, `Comparable`,
and `Eq` — which is documented as "always satisfied (placeholder)". There is no way to write:

- a trait or interface bound — `where T: Display`, `where T: Comparable + Hashable`
- `where T: struct`, which on the CLR is what lets a generic instantiate over a value type
  without boxing
- `where T: new()`
- a constraint on a method's own type parameters rather than the type's

The `struct` constraint is the one with consequences beyond ergonomics: without it every
generic container over a value type boxes, which is the difference between a math or
graphics type being usable in a loop and not.

## Two halves, one design

The silent-acceptance defect could be fixed alone — reject unknown names — but doing so
without extending the vocabulary would break declarations that are currently written
against names the registry does not know. The rejection and the extension want to land
together, with a migration note for anything relying on the current behaviour.

Related: `TOAST-0020` records that a trait's declared member types are not enforced on the
implementing class. Trait bounds here and trait enforcement there are the same guarantee
seen from the two ends.

## Acceptance

- [x] An unrecognised constraint name is a diagnostic naming the constraint, not a silent pass
- [x] Trait and interface names are usable as bounds
- [x] Multiple bounds on one parameter — `where T: Comparable + Hashable`
- [x] `where T: struct` and `where T: class` — *interpreted only; boxing in the emitted
      generic is compiler work and out of scope while compiled ToastScript is an experiment*
- [x] `where T: new()`
- [ ] Constraints on method type parameters, not only on the declaring type
- [x] Each constraint is enforced at instantiation with a diagnostic naming the argument,
      the parameter, and the unsatisfied bound
- [x] `is`/`is-not` against a constraint name stays consistent with the generic check —
      one registry, as today
- [ ] Interpreted and compiled agree, in the differential corpus
- [x] `§Type-Parameter Constraints` replaces the "accepted conservatively" sentence
- [ ] A built-in constraint is enforced when the type argument is a ToastScript class
      (**found while doing the above — see below**)

## Measured before starting — 2026-09-20

Most of what this item describes was already built, under a "Phase 4.7 — C#-style special
constraints" comment in `ToshTypeParameterConstraintRegistry`. The item said the vocabulary
was four names. It was eleven, and interface and class bounds already worked — `TOAST-0125`
built those and this item was never re-read against them.

| Probe | Before | After |
|---|---|---|
| `where T: struct` accepts `int`, refuses `string` | works | unchanged |
| `where T: class` refuses `int` | works | unchanged |
| `where T: unmanaged` refuses `string` | works | unchanged |
| `where T: Speaks` (interface) accepts an implementor, refuses `int` | works | unchanged |
| `where T: Animal` (class) accepts a subclass, refuses `int` | works | unchanged |
| `where T: Numeric, Comparable` — both enforced | works | unchanged |
| `42 is TotallyMadeUp` | `false` | unchanged |
| `where T: TotallyMadeUp` | **accepted** | **refused, suggests a near name** |
| `where T: Numeric + Comparable` | "Class definitions require a body" | accepted, both enforced |
| `where T: new()` | "Class definitions require a body" | accepted |

The `+` and `new()` failures were the same failure: the clause stopped at a token it did not
expect, the class parser then met a bareword where it wanted a body, and the error blamed the
body. `new()` was in the registry the whole time and nothing could spell it.

## The asymmetry that made the defect obvious

`42 is TotallyMadeUp` answered `false` while `where T: TotallyMadeUp` accepted everything —
one registry, two opposite answers for an unknown name. Recognition is now a question about
the *name*, asked before any question about the type argument, so the four validation sites
(class, record, interface, generic function) agree. Three of them consulted only the CLR
resolver, so a declared interface read as "unrecognised" there; refusing on that flag alone
would have turned this into the wrong error the conservative rule existed to prevent.

## What is still open

**A built-in constraint is not enforced against a ToastScript class argument.** Found while
verifying the above:

```tosh
class Thing { }
class B<T>(v: T) where T: Numeric { prop value: T = $v }
new B<Thing>(new Thing())      # accepted
```

`where T: struct` accepts it too. The CLR bound is null for a ToastScript class — user
classes share a backing type — and the registry branch skips the check on the stated ground
that precise enforcement happens at the next concrete instantiation, which for a user class
never comes. This is a larger hole than the typo one and a different mechanism, so it is
recorded rather than absorbed.

Doing it properly is a design question, not a patch: `Numeric`, `struct` and `unmanaged`
definitively fail for a user class and `class` definitively passes, but `Add` and `Comparable`
should consult the class's own operator overloads — which is what would make `where T: Add`
work for the user math and graphics types this item cares about.

Method-level type parameters and the compiled-mode agreement remain untouched; the second is
compiler work.
