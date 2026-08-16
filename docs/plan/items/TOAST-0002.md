---
id: TOAST-0002
title: "Statement dispatch is decided by scattered lookahead predicates that must agree by hand"
status: proposed
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

`TS-P2-11` proposed a Pratt rewrite of the expression layers, measured it, and
recommended against it — the expression cascade is already an explicit nine-level
precedence chain with correct associativity. But it found the scatter it was aimed at
is real and sits somewhere else, and said so: **statement dispatch**, which a Pratt
expression parser does not touch. This is that remainder, filed where it lives.

Counted in `src/Tosh.Language/Parsing/ToshParser.cs` (13,931 lines):

| | Count |
|---|---:|
| `LooksLike*` predicates | 59 |
| `Is*Token` predicates | 27 |
| `Peek(` lookahead sites | 229 |

The cost is not the count, it is that these must **agree with one another by hand**,
and the board records what happens when they do not:

- `TS-P2-105` — `as` was added to the precedence chain, but not to every "does this
  look like an expression?" scan. A bare `$x as int` stopped parsing as an
  expression while every case with a second operator still worked, so the corpus
  missed it.
- `TS-P2-116` — the unary operators could not open a statement. The binary operators
  are found by scans that look for an operator *after* the leading token, and a
  unary operator **is** the leading token, so no scan could ever have seen it.
  `not true` was read as a command name; `var x = not true` bound nothing, printed
  nothing and exited 0.
- Adding the six bitwise word operators (`TS-P3-14`) meant editing seven separate
  hand-maintained scan sites, and `export flags enum` still failed to parse because
  one modifier list was missed. The bare form worked, which is why the corpus did
  not catch it.

Three defects of the same shape, each found by hand, each after the fact.

## Acceptance

- [ ] Establish what the real invariant is — most likely "every operator token is known to every site that asks whether something is an expression"
- [ ] Make it hold **by construction or by test**, not by review: a single table the scans consult, or a tripwire that fails when a token is known to one site and not another
- [ ] Adding a new operator requires editing one place, demonstrated by adding one
- [ ] The three defects above are covered by the guard, checked by reverting each fix and confirming the guard fires
- [ ] No accepted syntax changes; the characterization corpus from `TS-P2-11` passes unchanged

## Notes

**Not a rewrite.** `TS-P2-11` already established that rewriting this parser for
style carries real regression risk across 13,000 lines and buys nothing user-visible.
The value here is a guard that makes disagreement impossible, which is a much smaller
change than restructuring the dispatch.

Sequence this against the file split in `TOAST_SEPARATION_PLAN.md` A2 — the parser is
one of the two monoliths being divided into partial classes. A guard is easier to add
once the scan sites are grouped by concern, and the split itself will make any
remaining disagreement more visible. Doing this first would mean touching the same
code twice.

The parity-tripwire pattern already used by `EditorSurfaceParityTests`,
`LanguageSurfaceParityTests` and `SyncAsyncTwinInventoryTests` is the model. Those
encode "these two surfaces must list the same things" and have caught real drift.
