---
id: TOAST-0119
title: "The argument-cost guard measures a body the fast-path flag does not control, so its two budgets are vacuous"
status: proposed
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

## What it is not

`TryEvaluateSimpleArgument` does have the case: `OperatorArgumentSyntax` guarded by
`IsSynchronousArithmeticOperator` with two `IsPrimitiveNumber` operands, which `$t + 1`
satisfies. And `SuppressSimpleArgumentFastPath` is read on every call to
`EvaluateArgumentAsync`. So the seam works where it is reached.

The likely explanation is that these bodies do not reach it: an assignment's right-hand
side is evaluated through the pipeline path, so the flag on the argument switch never
applies to the expression being measured. That would explain both the zeros and why
`$s = ($t)` — whose shape has an extra step somewhere — shows a small non-zero delta.

Worth confirming before fixing, because if it is right the number in the commit message
was measured against something else and the budget figures need re-deriving rather than
adjusting.

## Shape of a fix

Measure a body that certainly enters the argument switch, which means finding one and
proving it — a counter on `EvaluateArgumentSlowAsync` under the same seam would do it, and
would keep the benchmark honest afterwards. Then re-derive the two budgets from the new
baseline and record how the number was obtained, so the next drift is checkable.

## Acceptance

- [ ] The control passes: suppressing the fast path measurably changes the cost
- [ ] The body used is shown to enter the argument switch, not assumed to
- [ ] The two budgets are re-derived from that body and their derivation recorded
- [ ] A deliberate re-inlining of cases into `EvaluateArgumentSlowAsync` trips them
