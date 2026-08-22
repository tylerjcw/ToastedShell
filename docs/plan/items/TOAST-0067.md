---
id: TOAST-0067
title: "`echo` with several arguments emits one value each interpreted and one joined string compiled"
status: proposed
area: toast
priority: 2
opened: 2026-08-22
---

## Problem

```tosh
echo 1 2
```

| Backend | Output |
|---|---|
| interpreted | two pipeline values — `1` and `2`, rendered as a two-row table |
| compiled | one value — the string `1 2` |

The same holds for `echo "a" "b"`. It is the arity that matters, not the type: every corpus
case using a single argument agrees on both backends, which is why this went unnoticed.

## Why it matters

`echo` is the most-used command in the language, and this is not a rendering difference — it
is a difference in **how many values reach the pipeline**:

```tosh
(echo 1 2) | count      # 2 interpreted, 1 compiled
```

So anything downstream of a multi-argument `echo` sees a different shape. `TOAST-0028`
settled that the producer decides a collection's shape; this is a producer disagreeing with
itself across backends.

## Which is right

The interpreted answer looks correct: `echo a b` yielding two values is what makes
`echo $items` and `echo a b` behave the same way, and the compiled backend joining them is
the special case. But this has not been specified, and `§Value Rendering` does not say what
`echo` yields for several arguments — so the item needs the rule stated before either side is
called wrong.

## Acceptance

- [ ] `docs/spec/` states what `echo` yields for one argument and for several
- [ ] Both backends agree, and `(echo 1 2) | count` is the same on each
- [ ] The case moves from `KnownDivergences()` into `Corpus()`
- [ ] A negative control

## Notes

Found while adding a record-literal case to the differential corpus for `TOAST-0034` — the
case was written as `echo $r.a $r.b`, and the divergence it reported was `echo`'s rather than
the record's. The corpus case was rewritten to use interpolation, since it is about
inference; this is the finding it turned up on the way.
