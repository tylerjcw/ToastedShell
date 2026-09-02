---
id: TOAST-0103
title: "unfold cannot terminate: returning null raises instead of ending the sequence"
status: proposed
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

- [ ] The command-reference example runs and yields 1 2 3 4 5
- [ ] A callable returning `null` on the first call yields an empty sequence, not an error
- [ ] A callable that genuinely produces no value still raises `unfold_requires_single_result`
- [ ] Terminating and non-terminating uses are both covered in the corpus
- [ ] The documented contract and the behaviour agree
