---
id: TOAST-0013
title: "Thirty-two engine methods run past 100 lines, and the largest two are 1,030 and 546"
status: proposed
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

Splitting `ToshEngine.cs` into partial files (`TOAST-0005`) makes the *file* sizes
reasonable and does nothing at all about the *member* sizes. Measured across the engine
and its partials after five slices:

| Members | Lines | Share of member code |
|---|---:|---:|
| ≥ 500 lines | 1,576 | 8% |
| ≥ 200 lines | 2,329 | 11% |
| ≥ 100 lines | 5,954 | **29%** |

Thirty-two members are over 100 lines. The largest ten:

```
1030  EvaluateArgumentSlowAsync        546  EvaluateClassDefinitionAsync
 283  EvaluateParseResultAsync         238  EvaluateMemberAssignmentAsync
 232  ExecuteBlockStatementsAsync      178  BindFunctionParameters
 173  ExecuteFunctionAsync             169  TryConvertAnnotatedValue
 166  ExecuteCommandSyntaxAsync        165  WriteAutoHelp
```

**`EvaluateArgumentSlowAsync` is not just the biggest — it is the one `TOAST-0009`
already blames.** That item's evidence is that "`EvaluateArgumentAsync` is one `async`
method handling thirty-nine node shapes, so its state-machine box carries the locals of
every branch — about 2,545 bytes per entry, whatever the expression was. A literal paid
for the largest case in the switch." The 1,030 lines and the 2,545 bytes are the same
fact seen from two directions: an `async` state machine is sized by the method
containing it, so in this codebase method length is not only a readability question, it
is an allocation question.

That is what makes this worth filing rather than shrugging at. Everywhere else on this
list, size costs review effort. Here it costs memory on every argument evaluated.

## Acceptance

- [ ] `EvaluateArgumentSlowAsync` decomposed so that each node shape's locals are not carried by every other shape's state machine
- [ ] Allocation per argument evaluation measured before and after, A/B against a worktree build
- [ ] `EvaluateClassDefinitionAsync` (546) decomposed, or a reason recorded for leaving it whole
- [ ] The remaining members over 200 lines triaged: split, or explicitly kept with the reason
- [ ] No behavioural change — the suite passes unchanged at every step, as with the file split

## Notes

**Deliberately not done during `TOAST-0005`.** That phase moves code verbatim and
proves it by multiset comparison; decomposing a method is a change of shape that no
such proof covers, and mixing the two would put a behavioural change inside a
mechanical diff — the exact failure mode the split is disciplined against.

**Probably belongs to `TOAST-0009` rather than standing alone.** The bound-tree
evaluator replaces the thirty-nine-case switch with per-node evaluate methods, which
decomposes `EvaluateArgumentSlowAsync` by construction rather than by hand. Doing it
twice would be waste. Filed separately because the measurement is a finding in its own
right, and because the other thirty-one members are not addressed by that rewrite at
all.

If it is done by hand first, do it behind the differential harness `TOAST-0009` wants,
not against the suite alone.

## Re-measured — 2026-09-20

Swept with the rest of the proposed items. The headline moved in both directions, so the
figures above are the 2026-08 reading and not the current one.

| Member | Then | Now |
|---|---:|---:|
| `EvaluateArgumentSlowAsync` | 1,030 | **115** |
| `EvaluateClassDefinitionAsync` | 546 | **618** |
| `TryConvertAnnotatedValue` | 169 | 346 |
| `EvaluateParseResultAsync` | 283 | 275 |
| members over 100 lines (`ToshEngine*.cs`) | 32 | **37** |

**The one this item leaned on is gone.** `EvaluateArgumentSlowAsync` was split in
`01a9a3ee` — "each argument shape gets its own state machine" — which is
[`TOAST-0009`](TOAST-0009.md) doing the work this item said belonged to the evaluator
rewrite. That is the item's own thesis confirmed: the length and the per-entry allocation
were one fact, and fixing the allocation fixed the length.

**Everything else drifted the wrong way.** The count rose from 32 to 37, the largest member is
now a different one, and `TryConvertAnnotatedValue` doubled. So this is not close to done; it
has one fewer outlier and more of the ordinary problem.

`src/Tosh.Language/ToshEngine.Arguments.cs` still opens by describing
`EvaluateArgumentSlowAsync` as "**1,030 lines on its own** — the largest member in the engine".
Corrected in the same change as this note.

Method lengths here were measured by brace-matching with string literals and line comments
stripped, over `src/Tosh.Language/ToshEngine*.cs`. A first attempt capped the search at 400
lines and reported a cluster of methods at exactly 401, which is the cap rather than a length —
worth knowing before trusting a re-measurement.
