---
id: TOAST-0074
title: "The two backends refuse the same return conversion in different words"
status: complete
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

- [x] Both backends produce the same text for a refused return conversion
- [x] The message names the function, not a slot
- [x] The case moves from `KnownDivergences()` into `Corpus()`
- [x] A parameter conversion refusal is checked the same way, or recorded as still differing
- [x] A negative control

## Resolution — 2026-08-24

The portable `ToastMessages` catalog now owns the function-return conversion title and
label. The interpreter uses those shared strings, and the compiled emitter carries the
source function name into a dedicated `ToshHost.CheckReturnType` boundary. That boundary
performs the ordinary annotation conversion, translates only
`tosh.runtime.annotation_conversion_failed` into the function-specific return diagnostic,
and preserves refinement predicate/coercion diagnostics unchanged.

The null-return case moved from `KnownDivergences()` into `Corpus()`, beside a nullable
return that succeeds as its negative control. The required parameter-side check found a
larger behavior divergence: a compiled direct call accepts `null` for a non-nullable
`string` parameter while interpreter overload binding rejects it. That is recorded and
pinned separately as `TOAST-0075`; it is not disguised as part of this wording-only item.

The focused CLR and differential selection passes 154 tests; the full suite passes 6,557
with the existing language-surface negative probe skipped.
