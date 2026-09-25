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
- [x] `where T: struct` and `where T: class`
- [x] `where T: new()`
- [x] Constraints on method type parameters, not only on the declaring type
- [x] Each constraint is enforced at instantiation with a diagnostic naming the argument,
      the parameter, and the unsatisfied bound
- [x] `is`/`is-not` against a constraint name stays consistent with the generic check —
      one registry, as today
- [x] `§Type-Parameter Constraints` replaces the "accepted conservatively" sentence
- [x] A built-in constraint is enforced when the type argument is a ToastScript class
      (**found while doing the above**)

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

## A declared type as the argument

The larger half, found while verifying the first. `where T: Numeric` accepted
`new B<Thing>(…)` for any class `Thing`, and `where T: struct` accepted it too: the built-ins
are predicates over a CLR `Type`, a ToastScript class has none of its own — user classes share
a backing type — so the bound arrived null and every check was skipped, on the ground that
precise enforcement happens at the next concrete instantiation. For a declared type that
instantiation never comes.

`ToshDeclaredTypeConstraints` answers the same constraints from the declaration instead. A
null answer still means no opinion, so a genuinely forwarded or unresolved argument stays
conservative — the old rule was right, it was simply applied to everything rather than to the
cases that warrant it.

| Constraint | A declared type |
|---|---|
| `Numeric` / `Number` / `INumber` | refused — nothing declared is a CLR numeric primitive |
| `Add` / `Sub` / `Mul` / `Div` | the class declares the matching operator, inherited included |
| `Comparable` | an enum, or a class overloading `<`, `<=`, `>` or `>=` |
| `class` | a class, record or interface |
| `struct` | a struct or enum |
| `Eq`, `notnull` | satisfied |
| `unmanaged` | refused — a declared value type may still hold references |
| `new` / `new()` | constructible with no arguments |

`Add` is the one the item was really about: it is the constraint a math or graphics type
wants, and it is now answered by whether the class says it can be added, which is what an
operator overload is.

### Two things measured while building it

**A trait cannot declare an operator.** `func +` inside a trait body is refused by the parser
with "Expected a variable name", so trait-provided arithmetic is not a route to satisfying
`Add`. The first draft walked used traits looking for one; that code could never run and was
removed rather than left in untested. The author's own vector types are the case worth
checking against, and they declare `func +(o) => $this.Combine($o, "+")` on the class while
`uses Componentwise` supplies the ordinary method it delegates to.

**A record or struct type argument fails earlier, for an unrelated reason.**
`new B<Rec>(new Rec(1))` reports `tosh.runtime.annotation_conversion_failed` — "'B.value'
produced a value that is not a 'Rec'" — before any constraint is consulted. Not touched here.

## However the type parameter got its type

The remaining criterion read "constraints on method type parameters, not only on the
declaring type". Measured, a method's own type parameter was *already* enforced, both for an
explicit type argument and an inferred one. The gap was somewhere else and narrower:

| Call | Before | After |
|---|---|---|
| `func F<T>(x) where T: Numeric` then `F("a")` — inferred | refused | unchanged |
| the same, `F<string>("a")` — explicit | **accepted** | **refused** |
| `(new C()).F<string>("a")` — method, explicit | refused | unchanged |

A constraint was verified on the *first binding* of a type parameter, and an explicit
call-site type argument seeds that binding directly, so the check was already behind it.
Inference reached it. The same function and the same constraint therefore gave opposite
answers depending on whether the caller wrote the type out — and a method was unaffected,
which is what made the gap look like it was about methods.

`EnforceTypeParameterConstraints` is that check, lifted out of the inference path so the seed
path can run it too. It is validated after the whole seeding loop rather than inside it, so a
constraint mentioning another type parameter sees every explicit binding rather than only the
ones seeded before it. The diagnostic blames the type argument the caller wrote —
`type argument 'string' does not satisfy 'Numeric'` — rather than an ordinary argument that
was not at fault.

## What is still open

Two unrelated limitations found while probing, neither a regression — both reproduce on the
installed binary:

- `new Mod.Class<T>(…)` cannot construct a generic class through a module alias or a
  qualified module path.
- `record R<T>(x: T)` cannot annotate a field with its own type parameter.

> Compiler-agreement criteria removed 2026-09-25: there is no compiler (`TOAST-ARCH-01`).
