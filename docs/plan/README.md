# The plan

Work items for Tōast, TōSh, Tōme and Crumb. One item is one file.

## Where things are

| Path | What it is |
|---|---|
| `items/` | One file per item. The only place an item is edited. |
| `ACTIVE.md`, `PROPOSED.md`, `DEFERRED.md`, `COMPLETE.md` | **Generated indexes.** Do not edit; run `scripts/plan.tosh index`. |
| `legacy/COMPLETE.md` | The 176 items closed under the old stabilization board, frozen. |

The indexes are generated because the board they replaced was not. It carried a
hand-written snapshot with the instruction *"when a status changes there, change it
here in the same edit"*, and it drifted twenty items out of date — which is what
hand-maintained summaries do. Anything derivable is derived.

## Item files

```markdown
---
id: TOAST-0007
title: Splat arguments are rejected in two parser positions
status: partial
area: toast
priority: 2
opened: 2026-08-14
---

## Problem

What is wrong, with evidence. Measurements beat adjectives.

## Acceptance

- [x] `f(...$list)` spreads into the callee's parameters
- [ ] a splat in a pipeline stage
- [ ] a splat combined with named arguments

## Notes

Anything learned along the way — near misses, rejected approaches, why a
control was weak. This is the part that is expensive to rediscover.
```

### Fields

| Field | Values |
|---|---|
| `id` | `<AREA>-NNNN`, or a legacy `TS-P<n>-NN` |
| `title` | One line, no trailing full stop |
| `status` | `proposed` `open` `research` `in-progress` `partial` `complete` `deferred` `withdrawn` |
| `area` | `toast` `tosh` `tome` `crumb` `plan` `legacy` |
| `priority` | `0`–`3` |
| `opened` | `YYYY-MM-DD` |
| `closed` | `YYYY-MM-DD`, once resolved |
| `legacy` | The old ID, when one exists |

### IDs

New items take an **area prefix**: `TOAST-0001` for the language, `TOSH-0001` for the
shell, then `TOME-`, `CRUMB-`, and `PLAN-` for work on this system itself.

Area is in the identity because it is stable — an item rarely changes which component
it belongs to, while priority changes often. The old scheme put the priority tier in
the ID (`TS-P2-104`), which is why a re-prioritised item either kept a misleading
number or lost its identity. **Priority is a field.**

Filing an item therefore forces the language-or-shell question, which is the same
question the Tōast/TōSh separation asks. That is deliberate.

`TS-P*-*` IDs are **compatibility only**. They are not issued any more; they survive
because 853 references across 251 source and test files point at them. They retire as
their items close.

## Sub-tasks

Acceptance criteria are checkboxes, and **an item cannot be `complete` while an
unchecked box remains**. `scripts/plan.tosh check` enforces this.

This exists because the old board had no way to say "done in part". Authors invented
one: six rows grew an undeclared fifth column holding remaining work, and since
markdown drops cells past the header, **none of it was ever visible in the rendered
document**. Three of those six were the items marked "Complete for named functions",
"Complete for declarations" and "Complete for calls" — complete-looking rows with
known gaps. `partial` is the honest status, and the gaps are now boxes.

When a remainder is big enough to need its own citation in code, or its own history,
file it as a separate item and link it instead. `TS-P1-07` and `TS-P2-71` did exactly
that, refiling into `TS-P1-45` and `TS-P1-44`.

## Using it

```
scripts/plan.tosh counts             # derived totals
scripts/plan.tosh list active        # one board
scripts/plan.tosh show TOAST-0007    # one item
scripts/plan.tosh check              # integrity; non-zero on a fault
scripts/plan.tosh index              # regenerate the indexes
```

`check` verifies what a human edit can quietly break: duplicate IDs, a status outside
the vocabulary, an item marked complete with unchecked boxes, a missing required
field, and an ID cited in code that no longer exists on any board.
