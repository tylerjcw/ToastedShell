---
id: TOAST-0108
title: "A union whose variant names collide with the prelude's is checked against the prelude's union instead"
status: complete
area: toast
priority: 1
opened: 2026-09-02
---

## Problem

Exhaustiveness identifies the union from the arms, on the reasoning that "a variant name belongs
to exactly one union". `TOAST-0083` ended that: `Option` and `Result` are now ambient, so every
`Some`, `None`, `Ok` and `Err` a user declares collides with one.

Measured, with `union Maybe { Some(v: int) Nothing }`:

```
match (Maybe.Some(1)) { Some(v) => $v }

✖ tosh.bind.match_not_exhaustive
  This match over 'Option' does not cover None.
    'Option' declares: Some, None
```

A union the author never wrote, a variant that is not in theirs, and an error on code whose only
fault is the name `Some`. Adding the missing arm — `Nothing => 0` — made the diagnostic
disappear, but for the wrong reason: `Some` resolved to `Option` and `Nothing` to `Maybe`, the
two disagreed, and the check gave up silently. So the same declaration got a false error in one
shape and no checking at all in the other.

## Cause

Two lines, in opposite directions.

`CollectVariantUnions` seeds ambient unions and then walks the source, with the comment *"Ambient
first, so a declaration in this source overwrites it. That is the shadowing rule the rest of the
language already follows."* The walk it calls uses `unions.TryAdd(variant.Name, shape)`, which by
definition does not overwrite. The comment described the intended rule; the code did the reverse.

`TOAST-0095` had already met the same wall from the other side and left a note on
`CollectUnionsByName` saying so — "`Some` belongs to `Option` and to anything else that declares
one, and the last collected wins" — but solved it only for *qualified* patterns, where the author
names the union outright.

## Fix

A variant name maps to a **candidate list** rather than one union, source declarations first.

The arms then disambiguate each other: each names a variant, each variant has a candidate set,
and the union being matched is in all of them, so the intersection is the answer whenever it is
a single union. `Some` alone is ambiguous between `Option` and a user's `Maybe`; `Some` with
`Nothing` is not. Where the intersection still holds more than one, the source declaration wins —
the same shadowing rule, now actually implemented. Only a name ambiguous between two *source*
unions gives up, because there the language itself has no answer.

Resolving from one arm would have been wrong in the other direction too. Given a source
`Trio { Some, Middle, Last }` and a match on `Some` and `None`, first-arm resolution picks `Trio`
— source outranks ambient — and demands `Middle` and `Last`: a false error on a match that is
exhaustive over `Option`. Intersecting is what avoids both failures at once, and there is a test
asserting the *absence* of a diagnostic for exactly that shape.

## Acceptance

- [x] A source union shadowing an ambient one is named in its own diagnostic, with its own
      uncovered variants
- [x] The same union fully covered is accepted, and for the right reason rather than by the
      check bailing
- [x] An ambient union still resolves when nothing shadows it
- [x] Arms disambiguate each other where one alone could not, without a false error
- [x] A match mixing two unions is left alone rather than measured against whichever won
- [x] Qualified arms are unaffected — the qualifier still names the union outright

## Notes

Found while measuring the existing check before extending it for `TOAST-0054`'s nested coverage.
Reading the diff would not have shown it: both lines are individually reasonable, and the comment
asserts the behaviour the code contradicts. It took running a union that collides.

The general shape is worth keeping. Adding names to a prelude is not a neutral act — it changes
the resolution environment of every source that did not ask for them, and any place that assumed
a name was unique becomes wrong at that moment. `TOAST-0083` shipped the prelude; this is its
first bill.
