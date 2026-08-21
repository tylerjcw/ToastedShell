---
id: TOAST-0037
title: "The compiler has four diagnostic codes and no performance budget"
status: open
area: toast
priority: 3
opened: 2026-08-21
---

## Problem

Phase B's fourth bullet: "define compiler diagnostics and performance budgets". Neither
exists in a form anything can be held to.

**Diagnostics.** `DiagnosticCodeManifest.g.cs` carries **four** `tosh.compile.*` codes,
while the emitter raises at least eleven distinct "unsupported *X*" failures as free text
(`Diagnostics.Add($"unsupported statement: …")`). A free-text failure cannot be tested for,
suppressed, documented, or explained by `explain-error` — the mechanism the rest of the
language's failures use.

**Budgets.** No file in the repository states a compile-time or run-time budget. `bench/`
measures, but nothing says what number would count as a regression, so a measurement is
only ever compared to the last one by a person who remembers it.

## Why it is priority 3 rather than 2

Nothing is *wrong* today; the compiler is simply not yet something a contributor can be held
to. The other three Phase B items change behaviour, and their diagnostics are the natural
moment to define codes for — so doing this first would mean guessing at codes for failures
that are about to be fixed.

Sequence it after `TOAST-0034` and `TOAST-0035`, whose work will delete some of the eleven
messages outright.

## Acceptance

- [ ] Every emitter failure has a code in the manifest, not free text
- [ ] Each code has an `explain-error` entry
- [ ] A stated compile-time budget for the readiness probe, and a test that fails when it
      is exceeded
- [ ] A stated run-time budget for compiled versus interpreted on the same probe
- [ ] A negative control

## Notes

The eleven free-text messages are listed in `TOAST-0035`, which needs the same enumeration
for a different reason — one to implement them, this one to name them.
