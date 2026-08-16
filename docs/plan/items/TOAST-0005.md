---
id: TOAST-0005
title: "Split ToshEngine.cs and ToshParser.cs into partial classes by concern"
status: open
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

Phase A2 of [the separation plan](../../TOAST_SEPARATION_PLAN.md).

| File | Lines |
|---|---:|
| `ToshEngine.cs` | 19,318 |
| `ToshParser.cs` | 13,931 |

Two files are two-thirds of the language project. Splitting them is not cosmetic: it
is how the language/shell boundary gets decided, because deciding which partial each
method belongs to *is* deciding which side it is on.

## Acceptance

- [ ] Both files split into partial classes by concern — statements, expressions, declarations, classes, native interop, diagnostics
- [ ] Every moved method moves **verbatim**; the diff is a move, not an edit
- [ ] The suite passes unchanged, before and after, with no test edited to accommodate the split
- [ ] Anything noticed while moving is **filed, not fixed** — list the filed items here
- [ ] `BuiltInDisplayProfiles.cs` (6,501), `DisplayEngine.cs` (4,308), `ToshClassDefinition.cs` (3,769), `ToshLanguageFeatures.cs` (3,363) and `ToshHost.cs` (3,042) assessed, and split or explicitly left alone with a reason

## Notes

The discipline is refusing to fix anything while moving it. A behavioural change
hidden inside a 19,000-line mechanical diff is the hardest thing to review and the
easiest place for a regression to survive.

`git mv` where whole files move, so blame follows.

`TOAST-0002` — the scattered statement-dispatch lookahead — is easier to guard once
the parser's scan sites are grouped, so sequence it after this rather than before.
