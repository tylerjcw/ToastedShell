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

## Notes

The borderline cases are worth deciding explicitly rather than by where a file
currently sits: `Time` and `Data` read as language, `Net` reads as shell, and `Clr` is
language only because the CLR bridge is how Tōast reaches types at all.
