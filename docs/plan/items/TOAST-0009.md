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
- [~] A differential harness compares old evaluator against new over the conformance corpus,
      and both agree exactly — **the harness exists and is green**; it compares the two
      evaluators that exist *today*, and is where a bound-tree evaluator will hang.
- [ ] Streaming laziness preserved — `TS-P2-113` and `TS-P2-89` both have scars here
- [ ] Generators, cancellation and `NativeCallbackScope` re-entrancy all preserved
- [ ] Allocation per iteration measured before and after, A/B against a worktree build

## Where this sits, corrected 2026-08-17

Two readings of this item were offered and both were wrong.

It is **not** the gateway to self-hosting. `SELF_HOSTING_RFC.md` defines readiness
precisely — the existing compiler compiling a Tōast-written one, plus Tier-1 feature
coverage — and the evaluator appears nowhere in it.

It is **not** merely a performance item either. The RFC's §Shared front end says *"The
interpreter consumes the same bound representation where practical. It is not an
independent definition of language semantics"*, and Phase C's first task is *"Freeze the
canonical bound tree and lowered IR contracts."* That is this item.

So this is **Phase C work** and should be sequenced with Phase C rather than by its
allocation numbers. Its prerequisite is Phase B — the compiler-subset gaps — not the
25% expression win that first motivated it. The allocation improvement is a consequence
of routing evaluation through the bound tree, not the reason to do it.

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

## Starting work — 2026-09-04

### The allocation case is much weaker than the item states

Re-measured with `bench/Tosh.AllocationProbe`, 200,000 iterations, best of three:

| Shape | Item's "before" | Item's "after fast path" | **Today** |
|---|---:|---:|---:|
| empty `for` iteration | 2,797 B | 2,187 B | **1,377 B** |
| `$s = ($t)` | 8,034 B | 2,825 B | **1,705 B** |
| `$s = ($t + 1)` | 11,146 B | 6,057 B | **1,873 B** |

The cost *over an empty loop* is now 200–832 bytes depending on shape, not thousands. Whatever
else has changed since 2026-08-16, the 2,545-byte state-machine box the item describes is no
longer what a small expression pays.

This does not retire the item — the item says so itself: the reason is Phase C, freezing the
canonical bound tree as the shared front end, and *"the allocation improvement is a consequence
of routing evaluation through the bound tree, not the reason to do it."* But anyone deciding
whether to spend the rewrite should decide on the Phase C argument, because the performance one
has largely been collected already.

### The differential harness is in, and it is not vacuous

`EvaluatorDifferentialTests` evaluates each shape twice — once normally, once with the
synchronous fast path suppressed through a new internal seam — and requires the two to agree,
on failures as well as values.

It tests a real duplication that exists **now**. Every shape `TryEvaluateSimpleArgument` answers
is a second copy of a case in the thirty-nine-case switch; the comment on it says exactly that,
and relied on each case declining rather than guessing. That was a discipline with nothing
checking it.

Negative control run rather than assumed: making the fast path compute `-` where it should
compute `+` fails four of the twenty-seven cases. A harness whose two sides cannot disagree
would have stayed green.

### What the rewrite still needs

The seam is the point. A bound-tree evaluator hangs off the same switch the harness already
flips, so nodes can migrate one shape at a time with every step checked against the evaluator
they are replacing. That is the only way a rewrite of semantics-carrying code — with streaming
laziness, generators, cancellation and `NativeCallbackScope` re-entrancy all riding on it — can
be done in slices rather than as one unreviewable jump.
