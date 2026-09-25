---
id: TOAST-0056
title: "Unary and indexer operators cannot be overloaded, so a math value type has no natural syntax"
status: partial
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

- [x] Prefix `-` and `not` are overloadable, by method name as the binary forms are
- [x] An indexer is a specified language feature with `get` and `set` — *multi-argument
      indexers are **not** covered, and the spec now says why rather than leaving it implied*
- [ ] A value type may define a compound assignment that mutates rather than reallocating,
      and the specification states when the mutating form is chosen
- [x] The `Vec` example in `§Operator Overloading` is extended to negation and indexing, and
      it runs as a conformance fixture
- [ ] Unary and indexer resolution consult CLR `op_*` methods, consistently with `TOAST-0051`
- [x] The "Limitations" subsection is removed rather than reworded — *it was rewritten to
      three real limitations instead, which is the honest version of this*

## Measured — 2026-09-20

Most of this landed and the item was never re-read against it. The specification was brought
up to date at the same time; this item and `AGENTS.md` were not, so both went on describing a
language that stopped existing.

| Probe | Item says | Measured |
|---|---|---|
| `func -()` then `-(new V(3))` | unwritable | **works** — `-3` |
| `func not()` then `not (new B(1))` | — | **works** |
| `func [](i)` then `$v[0]` | parser-level only, not guaranteed | **works**, and specified |
| `func []=(i, v)` then `$v[1] = 99` | not mentioned | **works**, and specified |
| `$v[1] += 1` through `[]`/`[]=` | — | **works** — reads through one, writes through the other |
| `$v += $w` via `func +` | desugars to the binary form | unchanged, and by design |
| `$m[$i, $j]` | wanted | refused — `tosh.parser.unsupported_double_index` |
| `-$timespan` (CLR `op_UnaryNegation`) | wanted | refused |

`§Operator Overloading` already carries `Unary Operators` and `Indexers` subsections and a
`Limitations` list of exactly the three things still missing. It is accurate. This item was
the stale copy.

## What is genuinely left

- **Multi-argument indexing.** Not an oversight: a comma inside brackets already selects
  between `[value]`, `[key,]` and `[,value]`, so `$m[$i, $j]` collides with a spelling that
  means something else. Closing it needs a decision about that grammar, not an implementation.
- **CLR `op_*` for unary and indexer resolution.** `TOAST-0051` did this for the binary
  operators; the unary and indexer paths never got it, so `-$timespan` fails where
  `$a + $b` on the same kind of type succeeds.
- **A mutating compound assignment.** Still desugars, so `$v += $w` allocates. This is the
  one remaining piece of the original complaint that is about value types rather than syntax.

> Compiler-agreement criteria removed 2026-09-25: there is no compiler (`TOAST-ARCH-01`).
