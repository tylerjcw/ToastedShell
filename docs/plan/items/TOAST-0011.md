---
id: TOAST-0011
title: "A TōSh closure cannot be passed where C wants a function pointer"
status: open
area: toast
priority: 2
opened: 2026-08-16
---

## Problem

The one interop shape still missing. `func qsort(nint, nuint, nuint, callback)` is
rejected — there is no way to hand a TōSh closure to native code expecting a function
pointer.

Everything around it landed in 2026-07: `raw struct`, inline arrays and char buffers,
struct-by-value in both directions, pointer-to-struct walking, `out`/`ref`, success
contracts and errno. Callbacks are what is left, and they are the shape that turns FFI
from "can call a C function" into "can participate in a C API".

The cost is concrete and currently being paid. `~/.config/tosh/lib/Bluetooth.tosh`
cannot use sd-bus, so it re-spawns `bluetoothctl` on **every property read** —
`$h.Name`, `$h.IsPaired`, `$h.Battery` is three processes. libarchive progress
callbacks and every `qsort`-shaped API are blocked the same way.

## Acceptance

- [ ] A closure can be passed where a C function pointer is expected, via `Marshal.GetFunctionPointerForDelegate`
- [ ] A lifetime story exists and is documented — a rooted handle owned by the binding, or an explicit scope — so the delegate cannot be collected while native code still holds the pointer
- [ ] A callback that outlives the call that registered it works, since that is the case a naive implementation gets wrong
- [ ] Exceptions thrown inside a callback are handled without unwinding across native frames, consistent with `NativeCallbackScope`
- [ ] `qsort` works end to end as the canonical test
- [ ] sd-bus is reachable well enough for `Bluetooth.tosh` to stop spawning a process per property read

## Notes

`NativeCallbackScope` already exists because a callback cannot throw across the C
frames it runs on — so half the hard thinking is done and the constraint is known.

This is the item that decides whether Tōast's FFI is comparable to Nim's or C#'s.
SDL2, OpenGL, GTK3, GtkSharp and Avalonia have all been driven from TōSh already; every
one of them is event-driven, and callbacks are how events arrive.
