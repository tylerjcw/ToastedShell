---
id: TOAST-0103
title: "unfold cannot terminate: returning null raises instead of ending the sequence"
status: complete
area: toast
priority: 3
opened: 2026-08-30
---

## Problem

`unfold`'s contract is that the callable returns a `[value, next-state]` pair, or `null` to stop.
Returning `null` does not stop it — it raises. The example from the command reference fails
verbatim:

```tosh
❯ unfold 1 func(n) => if ($n <= 5) { [$n, ($n + 1)] } else { null } | collect
✖  tosh.runtime.unfold_requires_single_result
│  'unfold' operations must produce exactly one value per input item.
│  1 │ echo (unfold 1 func(n) => if ($n <= 5) { [$n, ($n + 1)] } else { null } | collect)
│            ┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄─▶ this operation produced no values
╰────┤ help: return exactly one value from the lambda or block for each input item.
```

A ternary in place of the `if` fails the same way, so it is the `null` and not the statement form.

## What still works

Only the non-terminating use:

```tosh
❯ unfold 1 func(n) => [$n, ($n + 1)] | first 5 | collect
1 2 3 4 5
```

## Why it matters

Termination is the only thing `unfold` has that `iterate` does not. `iterate seed f` already
generates an infinite sequence from a seed, and `| take-while` bounds it. An `unfold` that cannot
stop is `iterate` with a more awkward callable — the whole reason to reach for it is gone.

Found while building `ToastLib.Math.Sequences`, where the naturally-unfolding sequences had to be
written as explicit loops instead.

## Where to look

The diagnostic is `tosh.runtime.unfold_requires_single_result`, raised by the generic
"one value per input item" check. A `null` return is reaching that check as *no values* rather
than being recognised as the stop signal, so the fix is likely to distinguish "the callable
returned null" from "the callable produced nothing" before the arity check runs.

## Acceptance

- [x] The command-reference example runs and yields 1 2 3 4 5
- [x] A callable returning `null` on the first call yields an empty sequence, not an error
- [x] A callable that genuinely produces no value still raises `unfold_requires_single_result`
      — **at the commands where `null` has no meaning**, which is where the distinction can
      be drawn. See below.
- [x] Terminating and non-terminating uses are both covered in the corpus
- [x] The documented contract and the behaviour agree

## Closed — 2026-09-20: already fixed, never closed

The item was stale. The behaviour it reports no longer occurs, on the installed binary as
well as a fresh build, and `UnfoldStopsOnNullTests` has covered it for some time. Verified
against every criterion rather than the headline example alone:

| | |
|---|---|
| command-reference example | yields five values |
| `null` on the first call | empty sequence, no error |
| arrow, ternary and `return null` spellings | all stop |
| non-terminating use, bounded by `first 5` | five values |
| documented contract | "or null to stop" — agrees |

The third criterion asked for something that cannot be asked of `unfold` itself. An arrow
body or block whose value *is* `null` produces no pipeline value at all, so inside the
command "returned null" and "produced nothing" are the same zero results — there is nothing
left to tell apart.

It is answered one level up instead. The value-context collapse (`TS-P1-20`: none to null)
is applied only where `null` carries a meaning. `unfold` is such a place, because null means
stop. `map`, `sort-by` and `get` are not, so a lambda producing nothing there is still
`map_requires_single_result` and `sort_by_requires_single_result` — confirmed, and pinned by
`A_map_lambda_that_produces_nothing_is_still_an_error`. A mistake is still named wherever
naming it is possible.
