---
id: TOAST-0009
title: "Replace the switch-based evaluator with a bound-tree evaluator"
status: proposed
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

Phase B of [the separation plan](../../TOAST_SEPARATION_PLAN.md), and the one place a
rewrite is genuinely warranted.

Measured with `GC.GetTotalAllocatedBytes`:

| Shape | Before | After the fast path |
|---|---:|---:|
| empty `for` iteration | 2,797 B | 2,187 B |
| `$s = ($t)` | 8,034 B | 2,825 B |
| `$s = ($t + 1)` | 11,146 B | 6,057 B |

The cause is not that evaluation is async. It is that **`EvaluateArgumentAsync` is one
`async` method handling thirty-nine node shapes**, so its state-machine box carries the
locals of every branch — about 2,545 bytes per entry whatever the expression was. A
literal paid for the largest case in the switch.

The synchronous pre-dispatch already added is a workaround, and cannot be extended
indefinitely: every shape added is a second copy of semantics to keep in step, which is
the `TS-P1-24` failure mode.

`Lowerer` already produces a `BoundUnit` that the engine runs for its side effects and
then discards, with a comment saying evaluation will route through it. That is the
rewrite.

## Acceptance

- [ ] Each bound node type carries its own evaluate method; dispatch is a virtual call, not a thirty-nine-case switch
- [ ] Shapes that cannot suspend are synchronous by construction rather than special-cased
- [ ] A differential harness compares old evaluator against new over the conformance corpus, and both agree exactly
- [ ] Streaming laziness preserved — `TS-P2-113` and `TS-P2-89` both have scars here
- [ ] Generators, cancellation and `NativeCallbackScope` re-entrancy all preserved
- [ ] Allocation per iteration measured before and after, A/B against a worktree build

## Notes

**Do this last.** It rewrites semantics-carrying code and wants small reviewable files
and a settled boundary to land against — so after `TOAST-0005` and `TOAST-0006`. Done
during a reorganisation it would be a behavioural change hidden inside a mechanical
diff.

Expected result stated up front so it is not judged against the wrong target: within
5–10× of CPython on a tight loop seems plausible; parity does not. CPython interns
small integers and runs a purpose-built bytecode loop.

Benchmarks must be chosen per change and compared A/B against a worktree build. Two
standing benchmarks built from `$x += 1` and `$x == N` did not move at all for a change
worth 25% on expressions, and cross-run comparison produced a false regression twice.
