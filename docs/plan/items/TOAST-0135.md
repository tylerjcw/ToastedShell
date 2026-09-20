---
id: TOAST-0135
title: "The compiled backend diverges from the interpreter in six recorded places"
status: open
area: toast
priority: 3
opened: 2026-09-20
---

## Why this exists

Six items finished their interpreted work and then could not close, because each ended on
the same criterion: *interpreter and compiler agree*. They do not, deliberately — compiled
ToastScript is an experiment until the interpreted language is solid, so no new surface is
added there. Each item recorded its own divergence and stayed in ACTIVE to hold it.

That put six finished items in the active list and spread one question — *where does
compiled disagree with interpreted?* — across six places nobody re-reads. This is the
ledger instead. They are closed; their detail stays in their bodies.

## The divergences

| What | Interpreted | Compiled | Recorded in |
|---|---|---|---|
| A literal form for a value whose state is not entirely constructor arguments | works | diverges | [`TOAST-0091`](TOAST-0091.md) |
| Round-trip notation — writing a value to a file and reading it back as itself | works | does not agree | [`TOAST-0092`](TOAST-0092.md) |
| `Option`/`Result` sharing one contract across interpreter, docs, help and type metadata | works | does not | [`TOAST-0083`](TOAST-0083.md) |
| `is` against a nested type, and qualified variant patterns | works | deferred, no new surface | [`TOAST-0095`](TOAST-0095.md) |
| `match` binding a union's fields, in the differential corpus | works | not started, not next | [`TOAST-0053`](TOAST-0053.md) |
| A `static func` added by `extend` | works | deferred, no new surface | [`TOAST-0097`](TOAST-0097.md) |

Each closed item carries the reason and the measurement. This table is the index, not a
replacement for them.

## What would close this

Nothing, until compiled ToastScript stops being an experiment. It is filed as a ledger, not
a defect: the divergences are decisions, and the decision is that the interpreted language
comes first.

Closing it means one of two things, and it matters which:

- the compiled backend caught up on all six, or
- they were re-decided as permanent, at which point this becomes documentation of the
  compiled subset rather than a list of gaps

A seventh divergence should be added here rather than left in its own item, which is the whole
point of having this.

## Also watched here

`TOAST-0090` deferred a `prefer-path` analysis with a trigger: revisit only when
`TOAST-0092`'s notation needs the distinction between a path and a lookup *enforced*.
`TOAST-0092` is closed, so the trigger is noted in its body — and here too, because a
condition on future work is no use in a closed item alone. Neither `.` nor `::` is preferred
today and `.` on a type is not being deprecated.
