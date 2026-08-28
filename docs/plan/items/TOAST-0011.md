---
id: TOAST-0011
title: "A TōSh closure cannot be passed where C wants a function pointer"
status: partial
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

## Verified done — 2026-08-28

**This item was filed on 2026-08-16 and the language work landed since, without the item being
updated.** `raw callback` exists, is parsed, bound, thunked and documented; what follows is
what was checked rather than assumed, on `6c2d8d1`.

`qsort` — the canonical test this item names — sorts end to end from a ToastScript closure:

```tosh
raw callback Comparer(a: ptr, b: ptr) -> int

hermit class Libc {
    bind native "libc.so.6" {
        func qsort(base: ptr, count: nuint, size: nuint, compare: Comparer) -> void
    }
}
```

`[5, 3, 9, 1, 7]` comes back `1, 3, 5, 7, 9`.

**Lifetime**, both documented and checked. `§Native Interop` states the rule — *a thunk passed
to a library is kept alive for as long as that library is loaded, so a callee that stores it
(GLFW does) keeps working after the registering call returns*. Empirically, the same
`&compare_ints` sorts a second array after a forced `GC.Collect` / `WaitForPendingFinalizers` /
`GC.Collect`, so the thunk is genuinely rooted rather than incidentally alive.

**A throw inside a callback** is carried out rather than unwound across the C frames: a `try`
around the native call catches it, and the process keeps running. `NativeCallbackScope` is what
does it, and the spec's `§Threading` says so.

**The author's own `Gl.tosh`** registers real GLFW key and error callbacks and its comment now
reads "not a language limit any more" — the deferred-dispatch case in practice.

### One boundary found

`on_exit` from libc registers successfully and returns 0, but the handler does not run at
process exit. Almost certainly because the engine is gone by teardown, so there is nothing left
to re-enter. Worth knowing before someone reaches for an atexit-shaped API; it is not the case
this item was filed for, which is a library storing a callback and calling it during a later
call the script makes.

### What is genuinely left is not language work

`~/.config/tosh/lib/Bluetooth.tosh` still spawns `bluetoothctl` — eleven times. Nothing stops
it using sd-bus now; the binding simply has not been written, and it lives in the author's
shell configuration rather than in this repository. The box stays open because the item chose
it as the proof, but it is an application of a finished feature, not a gap in one.

## Acceptance

- [x] A closure can be passed where a C function pointer is expected — `raw callback`, and `&name` satisfies it
- [x] A lifetime story exists and is documented — the thunk lives as long as the library is loaded; checked against a forced GC, not only read
- [x] A callback that outlives the call that registered it works — GLFW in `Gl.tosh`, and thunk reuse after a full GC. `on_exit` at process teardown is the one boundary; see above
- [x] Exceptions thrown inside a callback are handled without unwinding across native frames — a `try` around the native call catches, and the process continues
- [x] `qsort` works end to end as the canonical test
- [ ] sd-bus is reachable well enough for `Bluetooth.tosh` to stop spawning a process per property read — the only box left, and it is a binding to write in the author's own library rather than language work

## Notes

`NativeCallbackScope` already exists because a callback cannot throw across the C
frames it runs on — so half the hard thinking is done and the constraint is known.

This is the item that decides whether Tōast's FFI is comparable to Nim's or C#'s.
SDL2, OpenGL, GTK3, GtkSharp and Avalonia have all been driven from TōSh already; every
one of them is event-driven, and callbacks are how events arrive.
