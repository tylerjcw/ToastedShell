---
id: TOAST-0056
title: "Unary and indexer operators cannot be overloaded, so a math value type has no natural syntax"
status: proposed
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

`§Operator Overloading` records three limitations, and together they mean a vector or matrix
type cannot present the syntax its domain expects:

- **Unary operators are not overloadable.** `-$v` is unwritable; the type must offer
  `$v.negate()`.
- **Indexers are "parser-level wired but not yet a spec-level guarantee"**, with the
  specification advising named methods instead. `$m[$i, $j]` on a matrix is the canonical
  case, and `$v[0]` on a vector nearly as common.
- **Compound assignment desugars to the binary form**, so `$v += $w` allocates a fresh value
  where a value type would want to write in place.

## Why this matters beyond ergonomics

Once `TOAST-0051` lands, CLR types with operators work — `Vector3 + Vector3` starts
returning a `Vector3`. That makes the remaining gaps conspicuous rather than academic:
addition works and negation does not; `Vector3.Dot` works and `$m[1, 2]` does not. A partial
operator surface is harder to explain than no operator surface.

These are also the operators a *user-declared* math type needs. `§Operator Overloading`'s own
example is a `Vec` class, and that example cannot express `-$a` or `$a[0]`.

## Ordering

`TOAST-0051` first — it establishes that operators reach value types at all. This item
completes the surface. `TOAST-0057`'s blessed aliases then have something coherent to bless.

`TS-P3-03` (reverse/static operator hooks) is the third piece of the same subsystem: which
operand's method is consulted when the two differ.

## Acceptance

- [ ] Prefix `-` and `not` are overloadable, by method name as the binary forms are
- [ ] An indexer is a specified language feature with `get` and `set`, and multi-argument
      indexers (`$m[$i, $j]`) are covered
- [ ] A value type may define a compound assignment that mutates rather than reallocating,
      and the specification states when the mutating form is chosen
- [ ] The `Vec` example in `§Operator Overloading` is extended to negation and indexing, and
      it runs as a conformance fixture
- [ ] Unary and indexer resolution consult CLR `op_*` methods, consistently with `TOAST-0051`
- [ ] Interpreted and compiled agree, in the differential corpus
- [ ] The "Limitations" subsection is removed rather than reworded
