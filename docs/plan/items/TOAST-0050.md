---
id: TOAST-0050
title: "A tuple type resolves but cannot be written in an annotation"
status: open
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

`TypeNameResolver` resolves `(int, string)` to a proper `TupleType` — measured, with a
`DisplayName` of `(Int32, String)`, and `()` and `(int, string, bool)` resolve too. The
*parser* rejects it in both positions where it would be used:

```tosh
func two() -> (int, string) { return (1, "a") }   # tosh.parser.expected_type_name
var t: (int, string) = (1, "a")                   # tosh.parser.expected_type_name
```

So the type exists, resolves, has a display form, and cannot be spelled.

## Why a compiler wants it

Two-value returns are what a compiler does constantly: a parsed node **and** the position
after it, a value **and** the diagnostics collected reaching it, a token **and** whether it
was synthesised. Written today, each needs a declared record — which is fine for a shape
used repeatedly, and heavy for one used once.

## Scope

The gap is in the *annotation* grammar, not the type model. `ToshParser` accepts a type name
where an annotation is expected and has no production for a parenthesised list; the resolver
behind it already parses `(a, b)` into a `TupleNode`.

Tuple *values* — `(1, "a")` — and tuple destructuring already work; only the type annotation
is missing.

## Acceptance

- [ ] `func f() -> (int, string)` parses and binds
- [ ] `var t: (int, string) = …` parses and binds
- [ ] A parameter may be annotated with a tuple type
- [ ] Arity and element mismatches are reported, not silently accepted
- [ ] Nested — `(int, (string, bool))` — and empty `()` behave as the resolver already says
- [ ] The interpreted and compiled backends agree
- [ ] `docs/spec/` states the annotation form
- [ ] A negative control

## Notes

Split out of `TOAST-0048`'s audit, which found three orphans in the type model. This is the
one with the clearest use and the smallest fix — the type is already built.
