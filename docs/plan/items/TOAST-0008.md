---
id: TOAST-0008
title: "Rename the language surface from Tosh to Toast, keeping every existing spelling working"
status: open
area: toast
priority: 3
opened: 2026-08-16
---

## Problem

Phase A5 of [the separation plan](../../TOAST_SEPARATION_PLAN.md). The measured
surface:

| Surface | Count | Approach |
|---|---:|---|
| Assemblies `Tosh.*` | 18 | One commit; mechanical |
| Diagnostic codes `tosh.*` | 534 | One prefix, `toast.*`; `hush` accepts `tosh.*` indefinitely |
| `$tosh.` in docs, examples, libraries | 131 | Alias `$toast`, keep `$tosh` working |
| `.tosh` source files | 51 | Both extensions recognised |

## Acceptance

- [ ] Assemblies renamed
- [ ] Diagnostic codes take the `toast.*` prefix, with `hush` accepting both spellings
- [ ] `$toast` is the preferred spelling and `$tosh` still resolves
- [ ] `.toast` is recognised alongside `.tosh`
- [ ] `~/.config/tosh`, the `tosh` binary and the Arch package are **not** renamed
- [ ] `EditorSurfaceParityTests`, `LanguageSurfaceParityTests` and `SyncAsyncTwinInventoryTests` updated — they encode current names and are the checklist for this, not an obstacle
- [ ] `docs/diagnostic-codes.md` regenerated rather than edited

## Notes

Rename for identity; do not rename what someone has already typed into a file on their
machine. TōSh is a logon shell and a working machine must not break.

Sequence after `TOAST-0004` so this is a find-and-replace over a tree that already
compiles in the target shape.

A two-prefix split of the diagnostic codes was considered and rejected:
`tosh.runtime.*` is 327 of 534 codes and is the genuinely mixed bucket, holding
`annotation_unknown_type` beside `unknown_command`. Splitting means hand-judging 61%
of the codes to correctly place the fourteen that are unambiguously shell-only.
