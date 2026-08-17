---
id: TOAST-0017
title: "A bare interpolation hole shifts an unspecified DateTime by the local offset"
status: complete
area: toast
priority: 2
opened: 2026-08-17
closed: 2026-08-17
---

## Problem

`$"{$d}"` and `$"{$d:HH:mm:ss}"` disagree about the same value, and the bare form moves
the clock. Measured on a UTC−4 machine:

```tosh
var n = new DateTime(2026, 8, 17, 12, 0, 0)   # Kind = Unspecified
$"{$n}"            # 2026-08-17 08:00:00   ← shifted back four hours
$"{$n:HH:mm:ss}"   # 12:00:00              ← as written

var u = $n.ToUniversalTime()                  # Kind = Utc, 16:00
$"{$u}"            # 2026-08-17 12:00:00
$"{$u:HH:mm:ss}"   # 16:00:00
```

The bare hole renders through the `DateTime` display profile, whose `Local` mode — the
default — calls `value.ToLocalTime()`. .NET's `ToLocalTime` **assumes `Unspecified` means
UTC** and converts. So a wall-clock literal is treated as UTC and shifted by the local
offset, and `12:00` is read back as `08:00`.

The specifier form does not go through the profile and does not shift.

Two separate faults:

1. **A value written as `12:00` renders as `08:00`.** No timezone was stated, so none
   should be applied.
2. **The two holes disagree**, and which one matches the literal flips with `Kind` — for
   the `Utc` value the bare form is the one that reads naturally and the specifier is the
   raw instant.

## Acceptance

- [x] A `DateTime` with `Kind = Unspecified` renders with its own clock reading, unshifted
- [x] `Kind = Utc` and `Kind = Local` each render unambiguously, and the rule is written
      down rather than inferred from a call to `ToLocalTime`
- [x] The bare hole and a specifier hole agree about which instant they are describing
- [x] `DateTimeOffset`, `DateOnly` and `TimeOnly` given named defaults alongside
- [x] A negative control — the whole stage-2 flip is one revertible commit

## Resolution — 2026-08-17

Closed with `TOAST-0014` stage 2, as the item asked: both changed what a bare hole
produces, and doing them separately would have changed it twice.

The shift is gone because the profile is gone. `ToastRenderer` never calls `ToLocalTime`,
so a value written `12:00` renders `12:00` whatever its `Kind`.

**One thing the item did not anticipate.** Removing the profile is not enough on its own —
the invariant culture's *own* default for a `DateTime` is `08/17/2026 12:00:00`, month
first, which is a locale convention wearing "invariant" as a disguise. So the temporal
defaults are **named** rather than inherited: `yyyy-MM-dd HH:mm:ss`, and matching forms for
`DateTimeOffset`, `DateOnly` and `TimeOnly`.

That was nearly missed. The test asserted `Contains("12:00:00")`, which passed while the
date rendered `08/17/2026`; it now asserts the exact string.

## Notes

Found during the `Phase A` survey while answering "what should `$"{x}"` render a
`DateTime` as". It is a `TOAST-0014` neighbour but **not** the same bug: `TOAST-0014` is
that the bare hole varies with shell configuration, and this is that its default is wrong
even with configuration untouched.

Worth fixing *with* `TOAST-0014` rather than before it, since both change what the bare
hole produces and doing them separately means changing it twice.

The wider finding that makes this cheap: **the specifier path is already portable.**
`$"{$d:HH:mm:ss}"` does not consult display profiles and does not vary when
`$tosh.Config.Display.DateTime.ScalarMode` changes. The architecture Phase A wants already
exists on one of the two paths; the bare hole is the one that needs to join it.
