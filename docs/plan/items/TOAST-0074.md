---
id: TOAST-0074
title: "The two backends refuse the same return conversion in different words"
status: proposed
area: toast
priority: 3
opened: 2026-08-23
---

## Problem

`func g() -> string { return null }` is refused by both backends, which is right — the
nullability rule says a non-nullable annotation does not accept null. They say so
differently:

| Backend | Message |
|---|---|
| interpreted | `Function 'g' returned a value that could not be converted to 'string'.` |
| compiled | `'return value' produced a value that could not be converted to 'string'.` |

The behaviour agrees. Only the wording does not, and the compiled phrasing is the weaker of
the two: *'return value'* names a slot rather than the function the reader wrote.

## Why it is recorded rather than ignored

The differential corpus compares messages on purpose — `TOAST-0018` settled that "both
raise, identically" is what the specification asks for, because a message is part of a
behaviour rather than decoration on it. A pair that agrees on refusing and disagrees on why
is exactly the shape that check exists to surface.

Priority 3: nothing computes a wrong answer, and no program behaves differently. It is a
quality-of-diagnostic gap, found while closing `TOAST-0066`.

## Where to look

The interpreted text comes from the return-conversion path in `ToshEngine`; the compiled one
from `ToshHost.CheckType`, which is given a slot description rather than the function's name.
Passing the function name through — as the interpreted path already does — is likely the
whole fix, and would let both come from one place the way `TOAST-0030` did for `new` and
`is`.

## Acceptance

- [ ] Both backends produce the same text for a refused return conversion
- [ ] The message names the function, not a slot
- [ ] The case moves from `KnownDivergences()` into `Corpus()`
- [ ] A parameter conversion refusal is checked the same way, or recorded as still differing
- [ ] A negative control
