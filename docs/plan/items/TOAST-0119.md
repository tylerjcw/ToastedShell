---
id: TOAST-0119
title: "The argument-cost guard measures a body the fast-path flag does not control, so its two budgets are vacuous"
status: complete
area: toast
priority: 2
opened: 2026-09-10
---

## Problem

`ArgumentEvaluationCostTests.The_measurement_can_tell_the_two_paths_apart` has failed since
the commit that introduced it (`01a9a3e`, `TOAST-0009`), with the same numbers every time
and on a clean checkout of that commit alone:

```
expected the slow path to cost more; fast=1882 slow=1882
```

It is the control for the two budget assertions above it — it exists to prove that
suppressing the fast path changes which code runs. It does not, for that body. And because
the budgets are `delta < budget`, a delta of zero passes both. The two guards that are
supposed to catch a return to the old shape currently cannot fail.

## Measurement

Suppressing the fast path changes nothing for any body containing an operator:

```
$s = ($t)                    fast=  1714 slow=  1794 delta=    80
$s = ($t + 1)                fast=  1882 slow=  1882 delta=     0
$s = ($t + 1) + 1            fast=  2010 slow=  2010 delta=     0
var q = ($t + 1)             fast=  2010 slow=  2010 delta=     0
echo ($t + 1) | ignore       fast=  5619 slow=  5619 delta=     0
$s = $t                      fast=  1546 slow=  1546 delta=     0
```

Only `$s = ($t)` moves at all, and by 80 bytes rather than the 2,520 the commit message
records. A later run of the same control reported `fast=1882 slow=1859` — the suppressed
path measuring *cheaper* — which is what a difference of nothing plus noise looks like.

## What it turned out to be — the opposite of the guess above

The first diagnosis here was that the body never reached the argument switch. That was
wrong, and a counter on `EvaluateArgumentSlowAsync` settled it in one run:

```
$s = ($t)                  slow-path entries: normal=1  suppressed=205
$s = ($t + 1)              slow-path entries: normal=1  suppressed=305
echo ($t + 1) | ignore     slow-path entries: normal=1  suppressed=305
$s = (($t + 1) + 2)        slow-path entries: normal=1  suppressed=505
```

The seam works exactly as designed. Suppressing the fast path sends three hundred extra
arguments per run through the switch — and that costs no measurable bytes, because
`TOAST-0009`'s extraction left entering the switch so nearly free that three hundred
entries do not move a byte count above its own noise.

So the control was failing *because the optimisation succeeded*. Its premise — that the
slow path must cost more — stopped being true the moment the work it guards was done.

## Fix

A byte count is the wrong instrument for "did the other path run". The control now counts
the entries, which answers it exactly and cannot be washed out by noise. The two budgets
assert the same thing first, so neither can silently measure nothing again: they were
passing only because their delta was zero, which a "less than" bound cannot distinguish
from a genuinely cheap switch.

The budgets themselves are kept as written. They still catch what they were built for — a
re-inlining of cases into `EvaluateArgumentSlowAsync` would allocate per entry, and there
are three hundred entries per run to multiply it by.

## Acceptance

- [x] The control passes, and asserts something that can fail for the right reason
- [x] The body used is *shown* to enter the argument switch rather than assumed to
- [x] The budgets assert path-distinguishability before asserting bytes
- [x] A deliberate re-inlining of cases into `EvaluateArgumentSlowAsync` trips them
