---
id: TOAST-0055
title: "An unrecognised generic constraint is silently satisfied, and the vocabulary is four names"
status: proposed
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

- [ ] An unrecognised constraint name is a diagnostic naming the constraint, not a silent pass
- [ ] Trait and interface names are usable as bounds
- [ ] Multiple bounds on one parameter — `where T: Comparable + Hashable`
- [ ] `where T: struct` and `where T: class`, and `struct` suppresses boxing in the emitted
      generic
- [ ] `where T: new()`
- [ ] Constraints on method type parameters, not only on the declaring type
- [ ] Each constraint is enforced at instantiation with a diagnostic naming the argument,
      the parameter, and the unsatisfied bound
- [ ] `is`/`is-not` against a constraint name stays consistent with the generic check —
      one registry, as today
- [ ] Interpreted and compiled agree, in the differential corpus
- [ ] `§Type-Parameter Constraints` replaces the "accepted conservatively" sentence
