---
id: TOAST-0007
title: "Split Tosh.Stdlib into language-level and shell-level commands"
status: open
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

Phase A4 of [the separation plan](../../TOAST_SEPARATION_PLAN.md). Already grouped by
category, which makes it unusually tractable:

| Language-level (moves to Tōast) | Shell-level (stays TōSh) |
|---|---|
| Pipeline (4,327), Clr (2,213), Text (1,748) | Filesystem (6,279), Sys (4,203) |
| Concurrency (1,045), Functional (648) | Shell (3,754), Net (1,931) |
| Time (615), Data (507), Maths | Processes (1,636), Display, Tssp |

`map`, `where`, `count` and `sort` are as much part of Tōast as `for` is. `ls`, `ps`
and `systemctl` are not.

## Acceptance

- [ ] Categories divided as above, with any reclassification argued in this file rather than done silently
- [ ] Language-level commands work in a host with no shell present
- [ ] Command help, metadata and completion still resolve across both halves
- [ ] The suite passes unchanged

## Correction 2026-08-17: `Filesystem` splits *within itself*

The table above files `Filesystem` (6,279) shell-side as a block, on the rule that "`ls`,
`ps` and `systemctl` are not [language]". That rule is right about `ls`; it is wrong
about the category.

`read-file`, `write-file`, `open` and `close` are **language-level**. A self-hosting
Tōast has to read its own source, and any program in a systems language opens files —
that is `FILE*` in C and `Stream` in C#, not a shell verb. `ls`, `df`, `du`, `chmod` and
`chown` are shell verbs.

So `Filesystem` is not assignable as a block: it holds both, and the split runs through
it. The same question should be asked of `Sys` (4,203) and `Processes` (1,636) before
either is moved wholesale — a self-hosting language needs to spawn a process, even if it
does not need `systemctl`.

## Notes

The borderline cases are worth deciding explicitly rather than by where a file
currently sits: `Time` and `Data` read as language, `Net` reads as shell, and `Clr` is
language only because the CLR bridge is how Tōast reaches types at all.

## State after 2026-08-25

Boundary preparation in `TOAST-0006` proved the first language-side groups in a host with no
`ToshRuntime`: CLR, Data/hash, Time, Pipeline (except `inspect`), Text (except `wc`'s display
override), Functional runtime helpers, and pure Concurrency (`async`, `race`, `settle`,
`timeout`). `spawn` and `scope` now explicitly recover TōSh's opaque command host because they
manipulate its concrete job table.

Two reclassifications are explicit:

- `inspect` is shell-side even though its directory says Pipeline. Its defining behavior is an
  interactive inline tree browser and its fallback is a shell object inspector.
- `wc`'s counting core is language-side, but `--show`/`--hide` currently attach preferences to
  TōSh's display engine. Split that presentation decoration from the counter instead of moving
  a display-selection type into the language contract.

The next implementation slice is to divide `BuiltInCommands.RegisterDefaults` into explicit
language and shell registrars while it is still one assembly. The full registrar remains their
composition for compatibility. Once callers and tests choose a registrar deliberately, the two
sets can move to separate projects without a behavioral diff; this is also the prerequisite for
removing `ToshEngine(ToshRuntime)` and completing `TOAST-0006`'s physical assembly acceptance.
